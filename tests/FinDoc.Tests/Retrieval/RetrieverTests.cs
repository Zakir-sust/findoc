using Microsoft.EntityFrameworkCore;
using OllamaSharp;
using FinDoc.Core.Data;
using FinDoc.Core.Retrieval;
using Xunit.Abstractions;

namespace FinDoc.Tests.Retrieval;

/// <summary>
/// Runs against the real local stack: the compose Postgres (the 9 PDFs ingested in C09) and Ollama on the host.
/// Not a unit test: it needs `docker compose up` and Ollama running. Run it alone with
/// `dotnet test --filter Category=Integration`. (C15 adds the Testcontainers version with seeded chunks.)
/// </summary>
[Trait("Category", "Integration")]
public sealed class RetrieverTests : IDisposable
{
    // The same local dev database as src/FinDoc.Ingestion/appsettings.json.
    private const string ConnectionString = "Host=localhost;Port=5432;Database=findoc;Username=findoc;Password=findoc";
    private const string Treaty = "us-bangladesh-2004";

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

    // g21, g22 and g24 from evals/golden.draft.json.
    [Theory]
    [InlineData("What withholding rate applies to dividends paid by a Bangladeshi company to a US company owning 10% of its voting stock?", "Article 10")]
    [InlineData("What is the treaty rate on interest paid from Bangladesh to a US resident?", "Article 11")]
    [InlineData("How does the US-Bangladesh treaty define a permanent establishment?", "Article 5")]
    public async Task KnownQuestion_HitsExpectedArticle_InTop5(string question, string expectedArticle)
    {
        var results = await _retriever.SearchAsync(question, k: 5, treaty: Treaty);

        for (var i = 0; i < results.Count; i++)
            _output.WriteLine($"#{i + 1}  {results[i].Article ?? "-",-11} {results[i].Score:F3}");

        Assert.Contains(expectedArticle, results.Select(r => r.Article));
    }

    [Fact]
    public async Task TreatyFilter_ReturnsKChunks_AllFromThatTreaty_NearestFirst()
    {
        var results = await _retriever.SearchAsync("withholding tax on dividends", k: 5, treaty: Treaty);

        Assert.Equal(5, results.Count);
        Assert.All(results, r => Assert.Equal(Treaty, r.Treaty));
        Assert.Equal(results.OrderByDescending(r => r.Score), results); // scores never go up
    }

    public void Dispose()
    {
        _db.Dispose();
        _ollama.Dispose();
    }
}
