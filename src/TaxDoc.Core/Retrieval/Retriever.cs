using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using TaxDoc.Core.Data;

namespace TaxDoc.Core.Retrieval;

/// <summary>One search hit: which treaty and article it comes from, its text, and how close it is to the question.</summary>
/// <param name="Treaty">The file name without ".pdf", e.g. "us-bangladesh-2004".</param>
/// <param name="Score">Cosine similarity = 1 − cosine distance: 1 = same direction, 0 = unrelated.</param>
public record RetrievedChunk(string Treaty, string? Article, string Content, double Score);

/// <summary>
/// Finds the chunks closest to a question: embed the question with the same model as the chunks,
/// then let pgvector order the chunks by cosine distance (the <c>&lt;=&gt;</c> operator) and take the top k.
/// </summary>
public class Retriever
{
    private readonly TaxDocDbContext _taxDocDbContext;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;

    public Retriever(TaxDocDbContext taxDocDbContext, IEmbeddingGenerator<string, Embedding<float>> generator)
    {
        _taxDocDbContext = taxDocDbContext;
        _generator = generator;
    }

    public async Task<List<RetrievedChunk>> SearchAsync(string question, int k = 5, string? treaty = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        ArgumentOutOfRangeException.ThrowIfLessThan(k, 1);

        // 1. The question becomes a vector in the same 1024-dimension space as the stored chunks.
        //    It must be the same model as at ingestion, or the distances mean nothing.
        var embedding = await _generator.GenerateAsync(question, cancellationToken: ct);
        var queryVector = new Vector(embedding.Vector);

        // 2. Only chunks that have an embedding.
        var chunks = _taxDocDbContext.Chunks.Where(x => x.Embedding != null);

        // 3. Optional treaty filter. The treaty isn't a column: the worker takes it from the file name.
        //    Built outside the lambda for readability: EF would turn treaty + ".pdf" into one SQL parameter either way.
        if (treaty is not null)
        {
            var fileName = treaty + ".pdf";
            chunks = chunks.Where(x => x.Document.FileName == fileName);
        }

        // 4. Nearest first. CosineDistance becomes "<=>" in SQL: 0 = same direction, up to 2 = opposite.
        var rows = await chunks
            .OrderBy(x => x.Embedding!.CosineDistance(queryVector))
            .Take(k)
            .Select(x => new
            {
                x.Document.FileName,
                x.Article,
                x.Content,
                Distance = x.Embedding!.CosineDistance(queryVector),
            })
            .ToListAsync(ct);

        // 5. Shape the result in memory (Path.GetFileNameWithoutExtension has no SQL translation).
        return rows
            .Select(r => new RetrievedChunk(
                Path.GetFileNameWithoutExtension(r.FileName), r.Article, r.Content, 1 - r.Distance))
            .ToList();
    }
}
