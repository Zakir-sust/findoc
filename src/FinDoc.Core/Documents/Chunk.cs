using Pgvector;

namespace FinDoc.Core.Documents;

/// <summary>One retrievable piece of a document (one 10-K item, or a ~800-token piece of a long one, with overlap).</summary>
public class Chunk
{
    public Guid Id { get; set; }

    public Guid DocumentId { get; set; }

    public Document Document { get; set; } = null!;

    /// <summary>Position of the chunk inside its document (0-based).</summary>
    public int Ordinal { get; set; }

    /// <summary>Item reference for citations, e.g. "Item 7". Null for the text before Item 1 (cover, contents, letter).</summary>
    public string? Item { get; set; }

    public required string Content { get; set; }

    public int? TokenCount { get; set; }

    /// <summary>
    /// vector(1024): qwen3-embedding:0.6b outputs 1024 dimensions (checked against the Ollama embed API).
    /// Nullable until the embedding step (C09) fills it.
    /// </summary>
    public Vector? Embedding { get; set; }
}
