using Microsoft.EntityFrameworkCore;
using OllamaSharp;
using FinDoc.Core.Data;
using FinDoc.Core.Retrieval;
using Xunit.Abstractions;

namespace FinDoc.Tests.Retrieval;

/// <summary>
/// Runs against the real local stack: the compose Postgres (the 4 annual reports ingested by scripts/download-sources.sh + the upload loop in the README) and Ollama on the host.
/// Not a unit test: it needs `docker compose up` and Ollama running. Run it alone with
/// `dotnet test --filter Category=Integration`. (C15 adds the Testcontainers version with seeded chunks.)
/// </summary>
[Trait("Category", "Integration")]
public sealed class RetrieverTests : IDisposable
{
    // The same local dev database as src/FinDoc.Ingestion/appsettings.json.
    private const string ConnectionString = "Host=localhost;Port=5432;Database=findoc;Username=findoc;Password=findoc";
    private const string Report = "apple-fy2026";

    private readonly FinDocDbContext _db;
    private readonly OllamaApiClient _ollama;
    private readonly Retriever _retriever;
    private readonly ITestOutputHelper _output;

    // xUnit creates a new instance of this class for every test, so each test gets its own DbContext.
    public RetrieverTests(ITestOutputHelper output)
    {
        _output = output;
        var options = new DbContextOptionsBuilder<FinDocDbContext>()
            .UseNpgsql(ConnectionString, npgsql => npgsql.UseVector())
            .Options;
        _db = new FinDocDbContext(options);

        // The same embedding model the worker used at ingestion (C09), or the distances mean nothing.
        _ollama = new OllamaApiClient(new Uri("http://localhost:11434"), "qwen3-embedding:0.6b");
        _retriever = new Retriever(_db, _ollama);
    }

    // g02, g05 and g07 from evals/golden.draft.json (Apple's fiscal 2026 10-K). Known miss, kept out on purpose:
    // g04 "How many employees…" ranks its chunk 33rd (one sentence inside an ~800-token Item 1 chunk). The evals (C22) count it.
    [Theory]
    [InlineData("How much did Apple spend on research and development in fiscal 2026, and how did that change?", "Item 7")]
    [InlineData("How much did the iPhone sell for Apple in fiscal 2026?", "Item 7")]
    [InlineData("Where is Apple headquartered?", "Item 2")]
    public async Task KnownQuestion_HitsExpectedItem_InTop5(string question, string expectedItem)
    {
        var results = await _retriever.SearchAsync(question, k: 5, report: Report);

        for (var i = 0; i < results.Count; i++)
            _output.WriteLine($"#{i + 1}  {results[i].Item ?? "-",-11} {results[i].Score:F3}");

        Assert.Contains(expectedItem, results.Select(r => r.Item));
    }

    [Fact]
    public async Task ReportFilter_ReturnsKChunks_AllFromThatReport_NearestFirst()
    {
        var results = await _retriever.SearchAsync("research and development expense", k: 5, report: Report);

        Assert.Equal(5, results.Count);
        Assert.All(results, r => Assert.Equal(Report, r.Report));
        Assert.Equal(results.OrderByDescending(r => r.Score), results); // scores never go up
    }

    public void Dispose()
    {
        _db.Dispose();
        _ollama.Dispose();
    }
}
