using System.Diagnostics;
using Microsoft.Extensions.AI;
using Pgvector;
using FinDoc.Core.Chunking;
using FinDoc.Core.Data;
using FinDoc.Core.Documents;

namespace FinDoc.Ingestion;

/// <summary>
/// The ingestion pipeline for one document: PDF → text → (chunks → embeddings) → Postgres.
/// Returns only after the database commit, so the consumer can ack safely afterwards.
/// </summary>
public sealed class DocumentIngestor(
    FinDocDbContext db,
    PdfTextExtractor extractor,
    IChunker chunker,
    IEmbeddingGenerator<string, Embedding<float>> generator,
    ILogger<DocumentIngestor> logger)
{
    /// <summary>Chunks per Ollama request: big enough to be fast, small enough to stay far below the 100 s HttpClient timeout.</summary>
    private const int BatchSize = 32;

    public async Task IngestAsync(Guid documentId, CancellationToken ct)
    {
        var document = await db.Documents.FindAsync([documentId], ct);
        if (document is null)
        {
            // Nothing to do: the row is gone (or never committed). Ack so it doesn't loop forever.
            logger.LogWarning("Document {DocumentId} not found; skipping", documentId);
            return;
        }

        if (document.Status is DocumentStatus.Extracted or DocumentStatus.Ingested)
        {
            // A redelivery (e.g. the worker crashed after commit but before ack). Already done.
            logger.LogInformation("Document {DocumentId} already {Status}; skipping", documentId, document.Status);
            return;
        }

        try
        {
            // 1. Text extraction (PdfPig).
            var text = extractor.Extract(document.StoragePath);
            var fullText = text.FullText;
            logger.LogInformation(
                "Extracted {TextLength} characters from {PageCount} pages of {FileName} ({DocumentId})",
                fullText.Length, text.Pages.Count, document.FileName, documentId);

            // 2. Chunk by 10-K item (C08). Report = the file name without ".pdf", e.g. "apple-fy2026".
            var report = Path.GetFileNameWithoutExtension(document.FileName);
            var textChunks = chunker.Chunk(text.Pages, report);

            // 3. Embed in batches, one Ollama request per batch, and add one Chunk row per TextChunk.
            //    textChunks.Chunk(32) is LINQ's Enumerable.Chunk (not our Chunk entity): it cuts the list, in order,
            //    into arrays of up to 32 items: [0..31], [32..63], ..., and the last array is shorter.
            var stopwatch = Stopwatch.StartNew();
            foreach (var batch in textChunks.Chunk(BatchSize))
            {
                var inputs = batch.Select(EmbeddingInput).ToList();
                var embeddings = await generator.GenerateAsync(inputs, cancellationToken: ct);

                // The generator returns one embedding per input, in input order: embeddings[i] is batch[i]'s vector.
                // Zip pairs them up, but silently stops at the shorter list, so check the counts first.
                if (embeddings.Count != batch.Length)
                    throw new InvalidOperationException($"Asked for {batch.Length} embeddings, got {embeddings.Count}.");

                foreach (var (textChunk, embedding) in batch.Zip(embeddings))
                {
                    document.Chunks.Add(new Chunk
                    {
                        Ordinal = textChunk.Ordinal,
                        Content = textChunk.Content,
                        Item = textChunk.Item is { } item ? $"Item {item}" : null,
                        TokenCount = textChunk.TokenCount,
                        Embedding = new Vector(embedding.Vector),
                    });
                }
            }
            logger.LogInformation("Embedded {ChunkCount} chunks of {Report} in {Seconds:F1} s",
                textChunks.Count, report, stopwatch.Elapsed.TotalSeconds);

            document.PageCount = text.Pages.Count;
            document.TextLength = fullText.Length;
            document.Status = DocumentStatus.Ingested;
            document.Error = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Ingestion failed for {DocumentId}", documentId);
            document.Status = DocumentStatus.Failed;
            document.Error = ex.Message;
        }

        document.ProcessedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct); // the commit; the consumer acks only after this returns
    }

    /// <summary>
    /// The text that gets embedded: the heading ("Item 1A Risk Factors") + the chunk, so a question that names the
    /// item or its topic matches better (a piece from the middle of a long item doesn't repeat its heading otherwise). Only <see cref="TextChunk.Content"/> is stored and shown as the source.
    /// </summary>
    private static string EmbeddingInput(TextChunk chunk) =>
        chunk.Item is { } item ? $"Item {item} {chunk.Title}\n{chunk.Content}" : chunk.Content;
}
