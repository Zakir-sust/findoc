using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pgvector;
using Testcontainers.PostgreSql;
using FinDoc.Core.Data;
using FinDoc.Core.Documents;

namespace FinDoc.Tests.Chat;

/// <summary>
/// The whole /chat path in one process: HTTP → the real endpoint → the real Retriever → real SQL on a real pgvector
/// → the real C12 prompt → SSE. Only the two models are fake: the embedder returns hand-made vectors and the chat
/// client records the prompt it was sent. Needs Docker only (a throwaway container per test run), no Ollama,
/// no compose stack. Still `Category=Integration`: it's slow (container start ~5 s) and needs Docker.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ChatPathTests(ChatApiFactory factory) : IClassFixture<ChatApiFactory>
{
    [Fact]
    public async Task The_prompt_carries_the_5_nearest_chunks_of_the_filtered_report_only()
    {
        var body = await Ask(report: "tesla-fy2025");

        var prompt = factory.ChatClient.LastUserMessage;
        // The 5 nearest Tesla chunks, nearest first (the order the prompt lists them in).
        AssertInOrder(prompt, Seed.TeslaItem8, Seed.TeslaItem7, Seed.TeslaItem1A, Seed.TeslaItem5, Seed.TeslaItem9A);
        Assert.DoesNotContain(Seed.TeslaItem2Far, prompt); // the 6th Tesla chunk: k = 5 cut it
        Assert.DoesNotContain(Seed.AppleItem8, prompt);    // the nearest chunk of all, but the wrong report

        // The answer cited Tesla's Item 8, so the sources event carries that chunk, then the stream ends.
        Assert.Contains(
            "event: sources\ndata: [{\"label\":\"sources-[tesla-fy2025]-[Item 8]\",\"item\":\"Item 8\",\"text\":\"" + Seed.TeslaItem8 + "\"}]\n\n",
            body);
        Assert.EndsWith("event: done\ndata: {}\n\n", body);
    }

    [Fact]
    public async Task Without_a_filter_the_nearest_chunk_of_any_report_comes_first()
    {
        await Ask(report: null);

        var prompt = factory.ChatClient.LastUserMessage;
        AssertInOrder(prompt, Seed.AppleItem8, Seed.TeslaItem8, Seed.TeslaItem7, Seed.TeslaItem1A, Seed.TeslaItem5);
        Assert.DoesNotContain(Seed.TeslaItem9A, prompt); // now 6th overall
    }

    [Fact]
    public async Task A_citation_to_a_label_that_was_not_sent_is_dropped_from_the_sources()
    {
        // Item 99 doesn't exist anywhere; Item 2 is a real Tesla chunk in the DB, but k = 5 cut it, so it was never sent.
        var body = await Ask(report: "tesla-fy2025", answer:
        [
            "About 423.7 million shares sources-[tesla-fy2025]-[Item 8]. ",
            "See also sources-[tesla-fy2025]-[Item 99] and sources-[tesla-fy2025]-[Item 2].",
        ]);

        // Only the cited label that was actually sent survives.
        Assert.Contains(
            "event: sources\ndata: [{\"label\":\"sources-[tesla-fy2025]-[Item 8]\",\"item\":\"Item 8\",\"text\":\"" + Seed.TeslaItem8 + "\"}]\n\n",
            body);
        Assert.DoesNotContain(Seed.TeslaItem2Far, body); // the real-but-unsent chunk's text never reaches the client
        Assert.EndsWith("event: done\ndata: {}\n\n", body);
    }

    private async Task<string> Ask(string? report, string[]? answer = null)
    {
        // The fixture's chat client is shared by the tests in this class (they run one at a time): set its answer per test.
        factory.ChatClient.Answer = answer ?? RecordingChatClient.CitesTeslaItem8;
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/chat", new { question = Seed.Question, report });
        response.EnsureSuccessStatusCode();
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        return await response.Content.ReadAsStringAsync();
    }

    private static void AssertInOrder(string text, params string[] parts)
    {
        var positions = parts.Select(p => text.IndexOf(p, StringComparison.Ordinal)).ToList();
        Assert.All(positions, p => Assert.True(p >= 0, "a chunk is missing from the prompt"));
        Assert.Equal(positions.Order(), positions);
    }
}

/// <summary>
/// Boots the real API (Program.cs) against a throwaway pgvector container, with the two models swapped for fakes.
/// One container for the whole test class (xUnit class fixture), seeded once.
/// </summary>
public sealed class ChatApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    // The same image as docker-compose.yml, so the vector extension and its version match.
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg17").Build();

    public RecordingChatClient ChatClient { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Not Development: no user secrets and no appsettings.Development.json, so the run doesn't depend on this laptop.
        builder.UseEnvironment("Testing");
        // Read by Program.cs at start-up; MigrateAsync then creates the tables in the empty container.
        builder.UseSetting("ConnectionStrings:FinDoc", _postgres.GetConnectionString());
        // Start-up still validates the Chat settings (Ollama needs no secret); the client itself is replaced below.
        builder.UseSetting("Chat:Provider", "Ollama");

        // Runs after Program.cs registered the real ones: remove them, add the fakes.
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IEmbeddingGenerator<string, Embedding<float>>>();
            services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new FakeEmbedder());
            services.RemoveAll<IChatClient>();
            services.AddSingleton<IChatClient>(ChatClient);
        });
    }

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        // Touching Services builds and starts the host, which runs the EF migrations.
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinDocDbContext>();
        db.Documents.AddRange(Seed.Documents());
        await db.SaveChangesAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}

/// <summary>
/// Hand-made vectors decide the ranking. The question points along axis 0; each chunk's vector is set so its cosine
/// similarity to the question is exactly the number given (cos·axis0 + sin·axis1, a unit vector).
/// </summary>
internal static class Seed
{
    public const string Question = "How many shares did Tesla grant in its 2025 CEO Performance Award?";

    public const string TeslaItem8 = "Tesla granted approximately 423.7 million shares in 12 tranches.";
    public const string TeslaItem7 = "Total revenues grew in 2025.";
    public const string TeslaItem1A = "Risk factors: key personnel.";
    public const string TeslaItem5 = "Market for the registrant's common equity.";
    public const string TeslaItem9A = "Controls and procedures.";
    public const string TeslaItem2Far = "Properties: Gigafactory Texas.";
    public const string AppleItem8 = "Apple granted restricted stock units to employees.";

    public static readonly float[] QuestionVector = Unit(axis: 0);

    public static IEnumerable<Document> Documents()
    {
        yield return Report("tesla-fy2025",
            ("Item 8", TeslaItem8, Similar(0.99)),
            ("Item 7", TeslaItem7, Similar(0.90)),
            ("Item 1A", TeslaItem1A, Similar(0.80)),
            ("Item 5", TeslaItem5, Similar(0.70)),
            ("Item 9A", TeslaItem9A, Similar(0.60)),
            ("Item 2", TeslaItem2Far, Unit(axis: 2))); // similarity 0: unrelated
        yield return Report("apple-fy2026",
            ("Item 8", AppleItem8, Similar(1.00))); // the same direction as the question: nearest of all
    }

    private static Document Report(string name, params (string Item, string Content, float[] Vector)[] chunks) => new()
    {
        FileName = name + ".pdf",
        StoragePath = $"/seed/{name}.pdf",
        Status = DocumentStatus.Ingested,
        UploadedAt = DateTimeOffset.UtcNow,
        Chunks = chunks.Select((c, i) => new Chunk
        {
            Ordinal = i,
            Item = c.Item,
            Content = c.Content,
            Embedding = new Vector(c.Vector),
        }).ToList(),
    };

    /// <summary>A unit vector whose cosine similarity to the question (axis 0) is <paramref name="cos"/>.</summary>
    private static float[] Similar(double cos)
    {
        var v = new float[FinDocDbContext.EmbeddingDimensions];
        v[0] = (float)cos;
        v[1] = (float)Math.Sqrt(1 - cos * cos);
        return v;
    }

    private static float[] Unit(int axis)
    {
        var v = new float[FinDocDbContext.EmbeddingDimensions];
        v[axis] = 1;
        return v;
    }
}

/// <summary>Returns the question's hand-made vector. Any other text is a test bug, so it throws.</summary>
internal sealed class FakeEmbedder : IEmbeddingGenerator<string, Embedding<float>>
{
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(v => v == Seed.Question
            ? new Embedding<float>(Seed.QuestionVector)
            : throw new InvalidOperationException($"No hand-made vector for \"{v}\"."))));

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

/// <summary>Records the prompt it was sent and streams <see cref="Answer"/> token by token (by default, a citation of Tesla's Item 8).</summary>
public sealed class RecordingChatClient : IChatClient
{
    public static readonly string[] CitesTeslaItem8 = ["About 423.7 million shares ", "sources-[tesla-fy2025]-[Item 8]"];

    public string[] Answer { get; set; } = CitesTeslaItem8;

    public string LastUserMessage { get; private set; } = "";

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        LastUserMessage = messages.Single(m => m.Role == ChatRole.User).Text;
        foreach (var token in Answer)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, token);
        }
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("/chat only streams.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
