namespace FinDoc.Core.Retrieval;

/// <summary>
/// Finds the chunks closest to a question. <see cref="Retriever"/> is the pgvector one; the interface lets the
/// /chat unit tests swap in a fake, since a real search needs Postgres and the embedding model.
/// </summary>
public interface IRetriever
{
    /// <param name="k">How many chunks to return, nearest first. Always k when the reports have that many,
    /// whether or not any of them fits the question: nearest isn't the same as relevant.</param>
    /// <param name="report">Optional filter, e.g. "apple-fy2026". A report with no chunks returns an empty list.</param>
    Task<List<RetrievedChunk>> SearchAsync(string question, int k = 5, string? report = null, CancellationToken ct = default);
}
