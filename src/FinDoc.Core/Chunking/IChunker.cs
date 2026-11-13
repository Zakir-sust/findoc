namespace FinDoc.Core.Chunking;

/// <summary>Splits a document's extracted text into retrievable chunks.</summary>
public interface IChunker
{
    /// <param name="pages">The PDF's text, one string per page (PdfPig, in order). Page numbers come from this list.</param>
    /// <param name="treaty">The treaty this document is, e.g. "us-bangladesh-2004". Copied onto every chunk.</param>
    IReadOnlyList<TextChunk> Chunk(IReadOnlyList<string> pages, string treaty);
}

/// <summary>A chunk before it is embedded and saved as a <see cref="Documents.Chunk"/> (C09 maps it).</summary>
/// <param name="Ordinal">Position in the document, 0-based, no gaps.</param>
/// <param name="Content">The chunk text that gets embedded and shown as the source.</param>
/// <param name="Treaty">The treaty name passed to <see cref="IChunker.Chunk"/>.</param>
/// <param name="Article">The article number (10 for "ARTICLE 10"); null for text before Article 1 (title, preamble) or documents without article headings (protocols).</param>
/// <param name="Title">The article's title line, e.g. "Dividends"; null when there is no article.</param>
/// <param name="Page">The 1-based PDF page where the chunk's article heading is (the page a viewer opens).</param>
/// <param name="TokenCount">Estimated tokens of <paramref name="Content"/> (see <see cref="ArticleChunker.EstimateTokens"/>).</param>
public sealed record TextChunk(
    int Ordinal,
    string Content,
    string Treaty,
    int? Article,
    string? Title,
    int Page,
    int TokenCount);
