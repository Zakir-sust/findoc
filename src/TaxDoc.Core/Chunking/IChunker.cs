namespace TaxDoc.Core.Chunking;

// =====================================================================================
// TODO(C08 — Jakir, by hand): design and implement the chunker.
//   Plan (job-search/projects/C-taxdoc-assistant.md): chunk by treaty article, ~800 tokens, with overlap.
//   The signature below is only a starting point — change it freely (e.g. take pages so chunks
//   can carry page numbers for citations). Add the unit tests in tests/TaxDoc.Tests.
//   The worker calls nothing here yet; wire it in DocumentIngestor once it exists.
// =====================================================================================

/// <summary>Splits a document's extracted text into retrievable chunks.</summary>
public interface IChunker
{
    IReadOnlyList<TextChunk> Chunk(string text);
}

/// <summary>A chunk before it is embedded and saved as a <see cref="Documents.Chunk"/>.</summary>
public sealed record TextChunk(int Ordinal, string Content, string? Article);
