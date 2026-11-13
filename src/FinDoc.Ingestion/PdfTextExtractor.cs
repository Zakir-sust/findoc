using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace FinDoc.Ingestion;

/// <summary>Text of a PDF, one string per page (page numbers are useful for citations later).</summary>
public sealed record PdfText(IReadOnlyList<string> Pages)
{
    public string FullText => string.Join("\n\n", Pages);
}

/// <summary>Extracts text with PdfPig. Works on text PDFs only; scanned PDFs (OCR) are out of scope.</summary>
public sealed class PdfTextExtractor
{
    public PdfText Extract(string path)
    {
        using var pdf = PdfDocument.Open(path);

        // ContentOrderTextExtractor keeps reading order and inserts spaces/newlines,
        // unlike page.Text, which glues words together on many PDFs.
        var pages = pdf.GetPages()
            .Select(page => ContentOrderTextExtractor.GetText(page))
            .ToList();

        return new PdfText(pages);
    }
}
