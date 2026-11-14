using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OllamaSharp;
using FinDoc.Api.Chat;
using FinDoc.Core.Data;
using FinDoc.Core.Prompting;
using FinDoc.Core.Retrieval;
using Xunit.Abstractions;

namespace FinDoc.Tests.Prompting;

/// <summary>
/// The C12 spec's two questions, end to end on the local stack: retrieve the top 5 from Apple's report →
/// CitationPrompt.Build → qwen3.5:9b through the same IChatClient pipeline as /chat (num_ctx 16384, thinking off).
/// Needs `docker compose up` and Ollama: `dotnet test --filter "FullyQualifiedName~CitationPromptIntegration"`.
/// A real model, so a run can differ from the last one: each test prints the sources and the answer.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CitationPromptIntegrationTests : IDisposable
{
    private const string ConnectionString = "Host=localhost;Port=5432;Database=findoc;Username=findoc;Password=findoc";
    private const string Report = "apple-fy2026";

    private readonly FinDocDbContext _db;
    private readonly OllamaApiClient _embeddings;
    private readonly ServiceProvider _services;
    private readonly Retriever _retriever;
    private readonly IChatClient _chat;
    private readonly ITestOutputHelper _output;

    public CitationPromptIntegrationTests(ITestOutputHelper output)
    {
        _output = output;

        var dbOptions = new DbContextOptionsBuilder<FinDocDbContext>()
            .UseNpgsql(ConnectionString, npgsql => npgsql.UseVector())
            .Options;
        _db = new FinDocDbContext(dbOptions);
        _embeddings = new OllamaApiClient(new Uri("http://localhost:11434"), "qwen3-embedding:0.6b");
        _retriever = new Retriever(_db, _embeddings);

        // The API's own registration (C10), so the test talks to the model exactly the way /chat does.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Chat:Provider"] = "Ollama",
                ["Chat:Ollama:Endpoint"] = "http://localhost:11434",
                ["Chat:Ollama:Model"] = "qwen3.5:9b",
            })
            .Build();
        _services = new ServiceCollection().AddLogging().AddFinDocChatClient(config).BuildServiceProvider();
        _chat = _services.GetRequiredService<IChatClient>();
    }

    [Fact]
    public async Task Revenue_for_a_year_not_in_the_report_gets_exactly_the_refusal()
    {
        // Apple's fiscal 2026 10-K covers 2026, 2025 and 2024 only. The near miss in its sources: 2024's $391,035M.
        var (answer, _) = await AskAsync("What's the revenue of Apple in 2023?");

        Assert.Equal(CitationPrompt.Refusal, answer.Trim());
    }

    [Fact]
    public async Task Net_income_growth_is_calculated_and_cited_to_item_8()
    {
        // Item 8, the income statement: $93,736M (2024) → $132,175M (2026) = +41.0077%, so 41.01 to 2 decimals.
        // Flaky by nature (a real model, 14 Nov, 4 runs of the current rules): 2 clean answers pass · 1 said
        // "Not in Source", then answered anyway, and still passes (no check for that yet → C18) · 1 rounded to 41.00%
        // and fails (model arithmetic → numbers from a tool, C17). Read the printed answer, not just the colour.
        var (answer, labels) = await AskAsync("How much did Apple's net income increase from 2024 to 2026, in percentage?");

        // The number is cited where it is used. Run 1 passed a plain Contains("41.01") although 41.01 only sat in the
        // working ("Calculation: … ≈ 41.01%") with no citation near it. Run 2's good answer put the citation on the line
        // under "**Answer:** 41.01%", so the next line counts too.
        Assert.True(CitedAfterNumber(answer, "41.01", "sources-[apple-fy2026]-[Item 8]"),
            "41.01 is never followed by sources-[apple-fy2026]-[Item 8] on its line or the next one.");
        Assert.All(CitationPrompt.FindCitations(answer), c => Assert.Contains(c, labels)); // every citation is a label we actually sent
    }

    /// <summary>
    /// True when a line has the number and the citation comes after it on that line, or on the next non-empty line.
    /// Blank lines are skipped, so a blank line between the answer and its citation doesn't fail it.
    /// </summary>
    private static bool CitedAfterNumber(string answer, string number, string citation)
    {
        var lines = answer.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

        for (var i = 0; i < lines.Count; i++)
        {
            var at = lines[i].IndexOf(number, StringComparison.Ordinal);
            if (at < 0)
                continue;

            if (lines[i].IndexOf(citation, at, StringComparison.Ordinal) > at)
                return true;

            if (i + 1 < lines.Count && lines[i + 1].Contains(citation, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private async Task<(string Answer, List<string> Labels)> AskAsync(string question)
    {
        var chunks = await _retriever.SearchAsync(question, k: 5, report: Report);
        var labels = chunks.Select(CitationPrompt.LabelFor).ToList();
        for (var i = 0; i < chunks.Count; i++)
            _output.WriteLine($"#{i + 1}  {labels[i],-34} {chunks[i].Score:F3}");

        var response = await _chat.GetResponseAsync(CitationPrompt.Build(question, chunks));
        _output.WriteLine($"Answer: {response.Text}");

        return (response.Text, labels);
    }

    public void Dispose()
    {
        _db.Dispose();
        _embeddings.Dispose();
        _services.Dispose();
    }
}
