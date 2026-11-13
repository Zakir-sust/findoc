using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using FinDoc.Api.Chat;

namespace FinDoc.Tests.Chat;

/// <summary>
/// Checks the SSE framing of POST /chat with a fake IChatClient: no model, no web server, no database.
/// The handler is called directly with a DefaultHttpContext whose response body is a MemoryStream.
/// (The full chat path against real services is the integration test in C15.)
/// </summary>
public class ChatEndpointsTests
{
    [Fact]
    public async Task Streams_each_chunk_as_a_data_event_then_done()
    {
        var (context, body) = NewHttpContext();
        var chatClient = new FakeChatClient(["Hello", "", " two\nlines"]);

        await ChatEndpoints.StreamAnswerAsync(new ChatRequest("Hi?", null), chatClient, NullLoggerFactory.Instance, context);

        Assert.Equal("text/event-stream", context.Response.ContentType);
        Assert.Equal(
            "data: {\"text\":\"Hello\"}\n\n" +            // the empty chunk is skipped
            "data: {\"text\":\" two\\nlines\"}\n\n" +     // the newline is JSON-escaped, so the event stays one line
            "event: done\ndata: {}\n\n",
            ReadBody(body));
    }

    [Fact]
    public async Task Model_failure_mid_stream_ends_with_an_error_event()
    {
        var (context, body) = NewHttpContext();
        var chatClient = new FakeChatClient(["Hello"], failAfterChunks: true);

        await ChatEndpoints.StreamAnswerAsync(new ChatRequest("Hi?", null), chatClient, NullLoggerFactory.Instance, context);

        Assert.Equal(
            "data: {\"text\":\"Hello\"}\n\n" +
            "event: error\ndata: {\"message\":\"The model call failed.\"}\n\n",
            ReadBody(body));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_question_is_a_400(string? question)
    {
        var (context, body) = NewHttpContext();

        var result = await ChatEndpoints.StreamAnswerAsync(
            new ChatRequest(question, null), new FakeChatClient([]), NullLoggerFactory.Instance, context);

        Assert.IsType<BadRequest<string>>(result);
        Assert.Equal("", ReadBody(body)); // nothing streamed
    }

    private static (DefaultHttpContext Context, MemoryStream Body) NewHttpContext()
    {
        var body = new MemoryStream();
        var context = new DefaultHttpContext();
        context.Response.Body = body;
        return (context, body);
    }

    private static string ReadBody(MemoryStream body) => Encoding.UTF8.GetString(body.ToArray());

    /// <summary>Streams the given chunks as if a model produced them; optionally throws after the last one.</summary>
    private sealed class FakeChatClient(string[] chunks, bool failAfterChunks = false) : IChatClient
    {
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
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
