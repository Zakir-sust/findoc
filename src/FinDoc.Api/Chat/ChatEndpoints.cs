using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.AI;

namespace FinDoc.Api.Chat;

/// <param name="Question">Required.</param>
/// <param name="Treaty">Optional filter, e.g. "us-bangladesh-2004". Not used until retrieval plugs in (C13).</param>
public sealed record ChatRequest(string? Question, string? Treaty);

public static class ChatEndpoints
{
    // Stub prompt for C10: no documents yet, just enough to see tokens stream.
    private const string StubSystemPrompt =
        "You are FinDoc, an assistant for questions about tax treaties. Answer briefly and plainly.";

    // Web defaults = camelCase ({"text":...}). The relaxed encoder keeps ' < & and non-ASCII readable in curl
    // instead of ' etc.; that's safe here because this JSON goes into an event stream, never into HTML.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static IEndpointRouteBuilder MapChatEndpoints(this IEndpointRouteBuilder app)
    {
        // curl -N -X POST http://localhost:5080/chat -H 'Content-Type: application/json' -d '{"question":"Say hello."}'
        app.MapPost("/chat", StreamAnswerAsync);
        return app;
    }

    /// <summary>
    /// Streams the answer as Server-Sent Events: one <c>data:</c> event per token chunk, then <c>event: done</c>
    /// (or <c>event: error</c> if the model call fails after the stream has started).
    /// </summary>
    /// <remarks>
    /// .NET 9 has no built-in SSE result (TypedResults.ServerSentEvents arrives in .NET 10), so the frames are written by hand.
    /// Internal so the unit tests can call it with a fake IChatClient.
    /// </remarks>
    internal static async Task<IResult> StreamAnswerAsync(
        ChatRequest request, IChatClient chatClient, ILoggerFactory loggerFactory, HttpContext httpContext)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
            return Results.BadRequest("The question is empty.");

        // TODO(C13): retrieve with Retriever.SearchAsync and build the messages with CitationPrompt.Build (mine)
        List<ChatMessage> messages =
        [
            new(ChatRole.System, StubSystemPrompt),
            new(ChatRole.User, request.Question),
        ];

        var logger = loggerFactory.CreateLogger(typeof(ChatEndpoints));
        var response = httpContext.Response;

        // Cancelled when the client disconnects (Ctrl-C in curl, a closed browser tab). Passing it to the model call
        // cancels the HTTP request to Ollama/Azure, so generation stops instead of running on for nobody.
        var ct = httpContext.RequestAborted;

        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache"; // proxies and browsers must not cache or replay a live stream
        httpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering(); // no-op on Kestrel; matters behind compression

        // Send the 200 + headers now, so the client sees the stream open while the model is still loading.
        // From here on the status code can't change: errors are reported as an "error" event instead.
        await response.StartAsync(ct);

        try
        {
            await foreach (var update in chatClient.GetStreamingResponseAsync(messages, cancellationToken: ct))
            {
                // Some updates carry no text (e.g. the last one, which only has the token usage).
                if (string.IsNullOrEmpty(update.Text))
                    continue;

                await WriteEventAsync(response, eventName: null, new TokenEvent(update.Text), ct);
            }

            // EventSource drops an event that has no data line, so "done" carries an empty object.
            await WriteEventAsync(response, "done", new { }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The client left: nobody to write to, and not a server error.
            logger.LogInformation("Client disconnected; chat stream stopped.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Chat stream failed.");
            // Keep the details in the server log; the client only learns that it failed.
            await WriteEventAsync(response, "error", new ErrorEvent("The model call failed."), CancellationToken.None);
        }

        return Results.Empty; // the response body is already written
    }

    /// <summary>
    /// Writes one SSE frame and flushes it, so the client gets it now rather than when a buffer fills:
    /// <code>
    /// data: {"text":"Hello"}
    ///
    /// event: done
    /// data: {}
    ///
    /// </code>
    /// Why JSON and not the raw token: a token can contain a newline, and in SSE a newline ends the "data:" line.
    /// The text after it would be read as a new (unknown) field and dropped. JSON escapes the newline as \n, so every
    /// event is one line. It also leaves room for more fields later (citations).
    /// </summary>
    private static async Task WriteEventAsync<T>(HttpResponse response, string? eventName, T payload, CancellationToken ct)
    {
        var eventLine = eventName is null ? "" : $"event: {eventName}\n";
        var json = JsonSerializer.Serialize(payload, JsonOptions);

        await response.WriteAsync($"{eventLine}data: {json}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }

    private sealed record TokenEvent(string Text);

    private sealed record ErrorEvent(string Message);
}
