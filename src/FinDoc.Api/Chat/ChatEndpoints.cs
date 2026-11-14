using System.Data.Common;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.AI;
using FinDoc.Core.Prompting;
using FinDoc.Core.Retrieval;

namespace FinDoc.Api.Chat;

/// <param name="Question">Required.</param>
/// <param name="Report">Optional filter, e.g. "apple-fy2026": search only that report's chunks.</param>
public sealed record ChatRequest(string? Question, string? Report);

public static class ChatEndpoints
{
    /// <summary>
    /// Chunks per question (C13 spec). Fewer gives the model less to answer from; more means more prompt tokens,
    /// and the model reads the whole prompt before its first token, so k also sets the wait for that token.
    /// </summary>
    internal const int TopK = 5;

    internal const string SearchFailed = "The report search failed. Try again.";
    internal const string ModelFailed = "The model call failed. Try again.";

    // Web defaults = camelCase ({"text":...}). The relaxed encoder keeps ' < & and non-ASCII readable in curl
    // instead of ' etc.; that's safe here because this JSON goes into an event stream, never into HTML.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static IEndpointRouteBuilder MapChatEndpoints(this IEndpointRouteBuilder app)
    {
        // curl -N -X POST http://localhost:5080/chat -H 'Content-Type: application/json' \
        //   -d '{"question":"How many shares did Tesla grant in its 2025 CEO Performance Award?","report":"tesla-fy2025"}'
        app.MapPost("/chat", StreamAnswerAsync);
        return app;
    }

    /// <summary>
    /// Answers from the reports as Server-Sent Events: the top-k chunks → the C12 citation prompt → the model.
    /// Events: one <c>data:</c> per token chunk, then <c>event: sources</c> (the chunks the answer cited), then
    /// <c>event: done</c>. A failure after the stream has opened ends it with <c>event: error</c> instead.
    /// </summary>
    /// <remarks>
    /// .NET 9 has no built-in SSE result (TypedResults.ServerSentEvents arrives in .NET 10), so the frames are written by hand.
    /// Internal so the unit tests can call it with a fake IRetriever and IChatClient.
    /// </remarks>
    internal static async Task<IResult> StreamAnswerAsync(
        ChatRequest request, IRetriever retriever, IChatClient chatClient, ILoggerFactory loggerFactory, HttpContext httpContext)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
            return Results.BadRequest("The question is empty.");

        var logger = loggerFactory.CreateLogger(typeof(ChatEndpoints));
        var response = httpContext.Response;

        // Cancelled when the client disconnects (Ctrl-C in curl, a closed browser tab). It goes to every call below
        // (the embedding call, the DB query, the model call), so all of them stop instead of running on for nobody.
        var ct = httpContext.RequestAborted;

        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache"; // proxies and browsers must not cache or replay a live stream
        httpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering(); // no-op on Kestrel; matters behind compression

        // Send the 200 + headers now, before retrieval (C13 spec): the client sees the stream open at once.
        // From here on the status code can't change: every failure is reported as an "error" event.
        await response.StartAsync(ct);

        // 1. Retrieve. Always the model next, even with zero chunks (an unknown report): the C12 rule makes the
        //    model answer "Not in Source", the same as when the 5 chunks don't fit (C13 spec).
        List<RetrievedChunk> chunks;
        try
        {
            chunks = await retriever.SearchAsync(request.Question, TopK, request.Report, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogInformation("Client disconnected during retrieval; chat stopped.");
            return Results.Empty;
        }
        catch (Exception ex)
        {
            // Two calls happen inside the search: Postgres (a DbException when it fails) and the embedding model.
            logger.LogError(ex, "Retrieval failed.");
            await WriteEventAsync(response, "error", new ErrorEvent(ex is DbException ? SearchFailed : ModelFailed), CancellationToken.None);
            return Results.Empty;
        }

        logger.LogInformation("Retrieved {Count} chunks (report filter: {Report}): {Labels}",
            chunks.Count, request.Report ?? "none", string.Join(", ", chunks.Select(CitationPrompt.LabelFor)));

        // 2. The C12 prompt: labelled sources + the question.
        var messages = CitationPrompt.Build(request.Question, chunks);

        // 3. Stream the answer, keeping a copy: the citations can only be read once the whole answer is in
        //    (a label can arrive split across several token chunks).
        var answer = new StringBuilder();
        try
        {
            await foreach (var update in chatClient.GetStreamingResponseAsync(messages, cancellationToken: ct))
            {
                // Some updates carry no text (e.g. the last one, which only has the token usage).
                if (string.IsNullOrEmpty(update.Text))
                    continue;

                answer.Append(update.Text);
                await WriteEventAsync(response, eventName: null, new TokenEvent(update.Text), ct);
            }

            // 4. The sources the answer cited, after the tokens (C13 spec). A refusal cites nothing: an empty list.
            await WriteEventAsync(response, "sources", CitedSources(answer.ToString(), chunks, logger), ct);

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
            await WriteEventAsync(response, "error", new ErrorEvent(ModelFailed), CancellationToken.None);
        }

        return Results.Empty; // the response body is already written
    }

    /// <summary>
    /// Every chunk whose label the answer cites, nearest first, with its item and text (for the sources panel, C26).
    /// Two chunks from the same item share a label, so one citation can bring back both.
    /// A citation that matches no label sent is made up: logged, never sent to the client (C13 spec).
    /// </summary>
    internal static List<SourceEvent> CitedSources(string answer, IReadOnlyList<RetrievedChunk> chunks, ILogger logger)
    {
        var cited = CitationPrompt.FindCitations(answer).ToHashSet();
        var sent = chunks.Select(CitationPrompt.LabelFor).ToHashSet();

        var madeUp = cited.Except(sent).ToList();
        if (madeUp.Count > 0)
            logger.LogWarning("The answer cited labels that were not sent (dropped): {Labels}", string.Join(", ", madeUp));

        return chunks
            .Where(c => cited.Contains(CitationPrompt.LabelFor(c)))
            .Select(c => new SourceEvent(CitationPrompt.LabelFor(c), c.Item, c.Content))
            .ToList();
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
    /// event is one line, even the sources event with whole chunks in it.
    /// </summary>
    private static async Task WriteEventAsync<T>(HttpResponse response, string? eventName, T payload, CancellationToken ct)
    {
        var eventLine = eventName is null ? "" : $"event: {eventName}\n";
        var json = JsonSerializer.Serialize(payload, JsonOptions);

        await response.WriteAsync($"{eventLine}data: {json}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }

    private sealed record TokenEvent(string Text);

    /// <summary>One cited source: its label (as cited), its 10-K item, and the chunk text.</summary>
    internal sealed record SourceEvent(string Label, string? Item, string Text);

    private sealed record ErrorEvent(string Message);
}
