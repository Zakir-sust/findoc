using TaxDoc.Core.Data;
using TaxDoc.Core.Documents;

namespace TaxDoc.Ingestion;

/// <summary>
/// The ingestion pipeline for one document: PDF → text → (chunks → embeddings) → Postgres.
/// Returns only after the database commit, so the consumer can ack safely afterwards.
/// </summary>
public sealed class DocumentIngestor(
    TaxDocDbContext db,
    PdfTextExtractor extractor,
    ILogger<DocumentIngestor> logger)
{
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

            // ---------------------------------------------------------------------------------
            // 2. TODO(C09 — Jakir): chunk the pages with the IChunker (ArticleChunker, built in C08).
            //      var textChunks = chunker.Chunk(text.Pages, treaty);   // treaty: e.g. the file name without ".pdf"
            //
            // 3. TODO(C09 — Jakir, by hand): embed each chunk (IEmbeddingGenerator, Ollama
            //    qwen3-embedding:0.6b → 1024 dims) and add Chunk rows with Embedding set:
            //      document.Chunks.Add(new Chunk { Ordinal = ..., Content = ..., Article = ..., Embedding = new Vector(...) });
            //    Then set Status = Ingested instead of Extracted below.
            // ---------------------------------------------------------------------------------

            document.PageCount = text.Pages.Count;
            document.TextLength = fullText.Length;
            document.Status = DocumentStatus.Extracted;
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
}
