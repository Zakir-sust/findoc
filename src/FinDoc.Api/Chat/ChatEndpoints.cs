using System.Data.Common;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.AI;
using FinDoc.Core.Metrics;
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
    /// Answers from the reports as Server-Sent Events: the top-k chunks → the C12 citation prompt → the model,
    /// which may call get_financial_metric (C17) before it answers.
    /// Events: one <c>data:</c> per token chunk, then <c>event: sources</c> (the chunks the answer cited), then
    /// <c>event: done</c>. A failure after the stream has opened ends it with <c>event: error</c> instead.
    /// </summary>
    /// <remarks>
    /// .NET 9 has no built-in SSE result (TypedResults.ServerSentEvents arrives in .NET 10), so the frames are written by hand.
    /// Internal so the unit tests can call it with a fake IRetriever and IChatClient.
    /// </remarks>
    internal static async Task<IResult> StreamAnswerAsync(
        ChatRequest request, IRetriever retriever, IChatClient chatClient, FinancialMetricTool metricTool,
        ILoggerFactory loggerFactory, HttpContext httpContext)
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

        // 3. Offer the tool (C17). The model decides whether to call it; the function-invocation layer in the
        //    pipeline runs it and calls the model again. A call and its result arrive as updates with no text.
        var options = new ChatOptions { Tools = [metricTool.Function] };

        // 4. Stream the answer, keeping a copy: the citations can only be read once the whole answer is in
        //    (a label can arrive split across several token chunks).
        var answer = new StringBuilder();
        var toolResults = new List<MetricResult>();
        UsageDetails? usage = null;
        try
        {
            await foreach (var update in chatClient.GetStreamingResponseAsync(messages, options, ct))
            {
                // Each model call reports its token counts once, usually in its last update (which has no text).
                // A tool call means two model calls, so they are added up: both are billed.
                foreach (var usageContent in update.Contents.OfType<UsageContent>())
                    (usage ??= new UsageDetails()).Add(usageContent.Details);

                // What get_financial_metric returned: its citation label counts as a source sent (step 5).
                foreach (var functionResult in update.Contents.OfType<FunctionResultContent>())
                    if (ReadMetricResult(functionResult.Result) is { } metric)
                        toolResults.Add(metric);

                if (string.IsNullOrEmpty(update.Text))
                    continue;

                answer.Append(update.Text);
                await WriteEventAsync(response, eventName: null, new TokenEvent(update.Text), ct);
            }

            LogUsage(logger, usage);

            // 5. The sources the answer cited, after the tokens (C13 spec). A refusal cites nothing: an empty list.
            await WriteEventAsync(response, "sources", CitedSources(answer.ToString(), chunks, toolResults, logger), ct);

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
    /// Every chunk whose label the answer cites, nearest first, with its item and text (for the sources panel, C26),
    /// then every get_financial_metric result it cites (C17), with the table row as its text.
    /// Two chunks from the same item share a label, so one citation can bring back both.
    /// A citation that matches no label sent (no chunk, no tool result) is made up: logged, never sent to the client (C13 spec).
    /// </summary>
    internal static List<SourceEvent> CitedSources(
        string answer, IReadOnlyList<RetrievedChunk> chunks, IReadOnlyList<MetricResult> toolResults, ILogger logger)
    {
        var cited = CitationPrompt.FindCitations(answer).ToHashSet();
        var found = toolResults.Where(r => r is { Found: true, Citation: not null }).ToList();
        var sent = chunks.Select(CitationPrompt.LabelFor).Concat(found.Select(r => r.Citation!)).ToHashSet();

        var madeUp = cited.Except(sent).ToList();
        if (madeUp.Count > 0)
            logger.LogWarning("The answer cited labels that were not sent (dropped): {Labels}", string.Join(", ", madeUp));

        var fromChunks = chunks
            .Where(c => cited.Contains(CitationPrompt.LabelFor(c)))
            .Select(c => new SourceEvent(CitationPrompt.LabelFor(c), c.Item, c.Content));
        var fromTool = found
            .Where(r => cited.Contains(r.Citation!))
            .Select(r => new SourceEvent(r.Citation!, r.Item,
                $"{FinancialMetricTool.Name}: {r.Company} {r.Metric}, fiscal {r.FiscalYear} = {r.Value} {r.Unit}"));

        return fromChunks.Concat(fromTool).ToList();
    }

    /// <summary>
    /// A get_financial_metric result as it comes back in the stream: the result object itself, or its JSON
    /// (MEAI serializes a function's return value). A failed call carries an error message instead: ignored here.
    /// </summary>
    private static MetricResult? ReadMetricResult(object? result) => result switch
    {
        MetricResult metric => metric,
        JsonElement { ValueKind: JsonValueKind.Object } json => json.Deserialize<MetricResult>(FinancialMetricTool.JsonOptions),
        _ => null,
    };

    /// <summary>
    /// One line per answer with the provider's own token counts: what a hosted model bills for (C14).
    /// Cached input = prompt tokens the provider served from its prompt cache (billed cheaper on Azure OpenAI).
    /// C19 turns this into cost per request and the /stats page.
    /// </summary>
    private static void LogUsage(ILogger logger, UsageDetails? usage)
    {
        if (usage is null)
        {
            logger.LogInformation("The model reported no token usage for this answer.");
            return;
        }

        logger.LogInformation("Model usage: {InputTokens} input tokens ({CachedInputTokens} cached), {OutputTokens} output tokens",
            usage.InputTokenCount, usage.CachedInputTokenCount ?? 0, usage.OutputTokenCount);
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
