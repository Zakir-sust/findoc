namespace FinDoc.Core.Chunking;

/// <summary>Splits a document's extracted text into retrievable chunks.</summary>
public interface IChunker
{
    /// <param name="pages">The PDF's text, one string per page (PdfPig, in order). Page numbers come from this list.</param>
    /// <param name="report">The report this document is, e.g. "apple-fy2026". Copied onto every chunk.</param>
    IReadOnlyList<TextChunk> Chunk(IReadOnlyList<string> pages, string report);
}

/// <summary>A chunk before it is embedded and saved as a <see cref="Documents.Chunk"/> (the ingestor maps it).</summary>
/// <param name="Ordinal">Position in the document, 0-based, no gaps.</param>
/// <param name="Content">The chunk text that gets embedded and shown as the source.</param>
/// <param name="Report">The report name passed to <see cref="IChunker.Chunk"/>.</param>
/// <param name="Item">The 10-K item ("1A" for "Item 1A. Risk Factors", "7" for MD&amp;A); null for the text before Item 1
/// (cover page, table of contents, a shareholder letter).</param>
/// <param name="Title">The item's title, e.g. "Risk Factors"; null when there is no item.</param>
/// <param name="Page">The 1-based PDF page where this chunk's text starts (the page a reader opens to check it).</param>
/// <param name="TokenCount">Estimated tokens of <paramref name="Content"/> (see <see cref="ItemChunker.EstimateTokens"/>).</param>
public sealed record TextChunk(
    int Ordinal,
    string Content,
    string Report,
    string? Item,
    string? Title,
    int Page,
    int TokenCount);
