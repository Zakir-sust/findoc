using Pgvector;

namespace TaxDoc.Core.Documents;

/// <summary>One retrievable piece of a document (planned: one treaty article, ~800 tokens, with overlap).</summary>
public class Chunk
{
    public Guid Id { get; set; }

    public Guid DocumentId { get; set; }

    public Document Document { get; set; } = null!;

    /// <summary>Position of the chunk inside its document (0-based).</summary>
    public int Ordinal { get; set; }

    /// <summary>Article reference for citations, e.g. "Article 10". Null if the chunker can't tell.</summary>
    public string? Article { get; set; }

    public required string Content { get; set; }

    public int? TokenCount { get; set; }

    /// <summary>
    /// vector(1024): qwen3-embedding:0.6b outputs 1024 dimensions (checked against the Ollama embed API).
    /// Nullable until the embedding step (C09) fills it.
    /// </summary>
    public Vector? Embedding { get; set; }
}
