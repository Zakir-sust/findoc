using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using FinDoc.Api.Chat;
using FinDoc.Core.Retrieval;

namespace FinDoc.Tests.Chat;

/// <summary>
/// POST /chat with a fake IRetriever and a fake IChatClient: no model, no web server, no database.
/// The handler is called directly with a DefaultHttpContext whose response body is a MemoryStream.
/// (The full chat path against real services is the integration test in C15.)
/// </summary>
public class ChatEndpointsTests
{
    private static readonly RetrievedChunk Item8 = new("tesla-fy2025", "Item 8", "granted approximately 423.7 million shares", 0.71);
    private static readonly RetrievedChunk Item8Next = new("tesla-fy2025", "Item 8", "the award vests in 12 tranches", 0.66);
    private static readonly RetrievedChunk Item7 = new("tesla-fy2025", "Item 7", "total revenues", 0.52);

    private const string Item8Label = "sources-[tesla-fy2025]-[Item 8]";

    [Fact]
    public async Task Streams_tokens_then_the_cited_sources_then_done()
    {
        var (context, body) = NewHttpContext();
        var retriever = new FakeRetriever([Item8, Item7]);
        // The citation arrives split across three token chunks, as a real model streams it.
        var chatClient = new FakeChatClient(["About 423.7 million shares ", "", "sources-[tesla-", "fy2025]-[Item 8]"]);

        await Ask(context, retriever, chatClient);

        Assert.Equal("text/event-stream", context.Response.ContentType);
        Assert.Equal(
            "data: {\"text\":\"About 423.7 million shares \"}\n\n" +   // the empty chunk is skipped
            "data: {\"text\":\"sources-[tesla-\"}\n\n" +
            "data: {\"text\":\"fy2025]-[Item 8]\"}\n\n" +
            "event: sources\ndata: [{\"label\":\"sources-[tesla-fy2025]-[Item 8]\",\"item\":\"Item 8\",\"text\":\"granted approximately 423.7 million shares\"}]\n\n" +
            "event: done\ndata: {}\n\n",
            ReadBody(body));
    }

    [Fact]
    public async Task Searches_top_5_with_the_report_filter_and_puts_the_sources_in_the_prompt()
    {
        var (context, _) = NewHttpContext();
        var retriever = new FakeRetriever([Item8, Item7]);
        var chatClient = new FakeChatClient(["Not in Source"]);

        await Ask(context, retriever, chatClient, question: "How many shares?", report: "tesla-fy2025");

        Assert.Equal(("How many shares?", 5, "tesla-fy2025"), (retriever.Question, retriever.K, retriever.Report));
        Assert.Equal(context.RequestAborted, retriever.Token); // a disconnect stops the search too

        var user = chatClient.Messages!.Single(m => m.Role == ChatRole.User).Text;
        Assert.Contains($"{Item8Label}\ngranted approximately 423.7 million shares", user);
        Assert.Contains("sources-[tesla-fy2025]-[Item 7]\ntotal revenues", user);
        Assert.EndsWith("Question: How many shares?", user);
    }

    [Fact]
    public async Task A_made_up_citation_is_not_sent_to_the_client()
    {
        var (context, body) = NewHttpContext();
        var chatClient = new FakeChatClient([$"423.7 million shares {Item8Label}, sources-[tesla-fy2025]-[Item 1A]"]);

        await Ask(context, new FakeRetriever([Item8, Item7]), chatClient);

        var sources = SourcesEvent(ReadBody(body));
        Assert.Contains(Item8Label, sources);
        Assert.DoesNotContain("Item 1A", sources); // cited, but never sent: dropped
        Assert.DoesNotContain("Item 7", sources);  // sent, but not cited
    }

    [Fact]
    public async Task A_citation_with_the_bracket_moved_left_still_brings_back_its_source()
    {
        // C14: gpt-4.1-mini wrote this instead of the exact label, and the answer came back with "sources: []".
        var (context, body) = NewHttpContext();
        var chatClient = new FakeChatClient(["About 423.7 million shares ", "[sources-tesla-", "fy2025]-[Item 8]"]);

        await Ask(context, new FakeRetriever([Item8, Item7]), chatClient);

        var sources = SourcesEvent(ReadBody(body));
        Assert.Contains($"\"label\":\"{Item8Label}\"", sources); // reported as the exact label sent
        Assert.DoesNotContain("Item 7", sources);
    }

    [Fact]
    public async Task One_citation_brings_back_every_chunk_with_that_label()
    {
        var (context, body) = NewHttpContext();
        var chatClient = new FakeChatClient([$"423.7 million shares {Item8Label}"]);

        await Ask(context, new FakeRetriever([Item8, Item8Next]), chatClient);

        var sources = SourcesEvent(ReadBody(body));
        Assert.Contains("423.7 million shares", sources);
        Assert.Contains("12 tranches", sources);
    }

    [Fact]
    public async Task Zero_chunks_still_asks_the_model_and_sends_an_empty_sources_list()
    {
        var (context, body) = NewHttpContext();
        var chatClient = new FakeChatClient(["Not in Source"]);

        await Ask(context, new FakeRetriever([]), chatClient, report: "no-such-report");

        Assert.Equal(1, chatClient.Calls); // the model says "Not in Source", not the endpoint (C13 spec)
        Assert.Equal(
            "data: {\"text\":\"Not in Source\"}\n\n" +
            "event: sources\ndata: []\n\n" +
            "event: done\ndata: {}\n\n",
            ReadBody(body));
    }

    [Fact]
    public async Task A_database_failure_is_a_search_error_and_the_model_is_not_called()
    {
        var (context, body) = NewHttpContext();
        var chatClient = new FakeChatClient(["never"]);

        await Ask(context, new FakeRetriever(failWith: new FakeDbException()), chatClient);

        Assert.Equal(0, chatClient.Calls);
        Assert.Equal($"event: error\ndata: {{\"message\":\"{ChatEndpoints.SearchFailed}\"}}\n\n", ReadBody(body));
    }

    [Fact]
    public async Task An_embedding_failure_is_a_model_error_and_the_chat_model_is_not_called()
    {
        var (context, body) = NewHttpContext();
        var chatClient = new FakeChatClient(["never"]);

        await Ask(context, new FakeRetriever(failWith: new HttpRequestException("Connection refused")), chatClient);

        Assert.Equal(0, chatClient.Calls);
        Assert.Equal($"event: error\ndata: {{\"message\":\"{ChatEndpoints.ModelFailed}\"}}\n\n", ReadBody(body));
    }

    [Fact]
    public async Task Model_failure_mid_stream_ends_with_an_error_event_and_no_sources()
    {
        var (context, body) = NewHttpContext();
        var chatClient = new FakeChatClient(["Hello"], failAfterChunks: true);

        await Ask(context, new FakeRetriever([Item8]), chatClient);

        Assert.Equal(
            "data: {\"text\":\"Hello\"}\n\n" +
            $"event: error\ndata: {{\"message\":\"{ChatEndpoints.ModelFailed}\"}}\n\n",
            ReadBody(body));
    }

    [Fact]
    public async Task A_disconnect_during_retrieval_writes_nothing_and_skips_the_model()
    {
        var (context, body) = NewHttpContext();
        using var disconnect = new CancellationTokenSource();
        context.RequestAborted = disconnect.Token;
        var chatClient = new FakeChatClient(["never"]);

        await Ask(context, new FakeRetriever(onSearch: disconnect.Cancel), chatClient);

        Assert.Equal(0, chatClient.Calls);
        Assert.Equal("", ReadBody(body)); // no error event: nobody is there to read it, and it isn't a server error
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_question_is_a_400(string? question)
    {
        var (context, body) = NewHttpContext();
        var retriever = new FakeRetriever([Item8]);

        var result = await ChatEndpoints.StreamAnswerAsync(
            new ChatRequest(question, null), retriever, new FakeChatClient([]), NullLoggerFactory.Instance, context);

        Assert.IsType<BadRequest<string>>(result);
        Assert.Equal("", ReadBody(body)); // nothing streamed
        Assert.Null(retriever.Question);  // nothing searched
    }

    private static Task<IResult> Ask(
        HttpContext context, IRetriever retriever, IChatClient chatClient,
        string question = "How many shares were granted?", string? report = null) =>
        ChatEndpoints.StreamAnswerAsync(new ChatRequest(question, report), retriever, chatClient, NullLoggerFactory.Instance, context);

    private static (DefaultHttpContext Context, MemoryStream Body) NewHttpContext()
    {
        var body = new MemoryStream();
        var context = new DefaultHttpContext();
        context.Response.Body = body;
        return (context, body);
    }

    private static string ReadBody(MemoryStream body) => Encoding.UTF8.GetString(body.ToArray());

    /// <summary>The data line of the "sources" event.</summary>
    private static string SourcesEvent(string body) =>
        body.Split("\n\n").Single(e => e.StartsWith("event: sources\n"));

    /// <summary>Returns fixed chunks and records what it was asked; or throws, as the DB or the embedding model would.</summary>
    private sealed class FakeRetriever(
        List<RetrievedChunk>? chunks = null, Exception? failWith = null, Action? onSearch = null) : IRetriever
    {
        public string? Question { get; private set; }
        public int K { get; private set; }
        public string? Report { get; private set; }
        public CancellationToken Token { get; private set; }

        public async Task<List<RetrievedChunk>> SearchAsync(string question, int k = 5, string? report = null, CancellationToken ct = default)
        {
            (Question, K, Report, Token) = (question, k, report, ct);
            await Task.Yield();

            onSearch?.Invoke();               // e.g. the client disconnects while the search runs
            ct.ThrowIfCancellationRequested(); // what EF Core and the HTTP client do with a cancelled token
            if (failWith is not null)
                throw failWith;

            return chunks ?? [];
        }
    }

    private sealed class FakeDbException() : DbException("Connection refused");

    /// <summary>Streams the given chunks as if a model produced them and records the messages it was sent.</summary>
    private sealed class FakeChatClient(string[] chunks, bool failAfterChunks = false) : IChatClient
    {
        public int Calls { get; private set; }
        public List<ChatMessage>? Messages { get; private set; }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            Messages = messages.ToList();

            foreach (var chunk in chunks)
            {
                await Task.Yield(); // behave like a real async stream
                yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
            }

            if (failAfterChunks)
                throw new HttpRequestException("Connection refused");
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("/chat only streams.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
