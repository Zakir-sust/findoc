using System.Text;
using System.Text.RegularExpressions;

namespace FinDoc.Core.Chunking;

/// <summary>
/// Annual-report (10-K) chunker: one section per Item ("Item 1. Business", "Item 1A. Risk Factors", "Item 7. MD&amp;A" …).
/// An Item over the token cap is split at its lines, and each piece after the first repeats the last few lines of the
/// piece before (overlap), so a fact that sits on a cut is still whole in one of the two pieces.
/// </summary>
/// <remarks>
/// What the real PDFs look like (PdfPig <c>ContentOrderTextExtractor</c>, checked 13 Nov on the 4 reports in <c>sources/</c>):
/// <list type="bullet">
/// <item>The heading is one line: "Item 1A. Risk Factors" (Apple, Amazon) or "ITEM 1A. RISK FACTORS" (Alphabet, Tesla;
///       Tesla separates the words with tabs). Every 10-K uses the same items, 1 to 16, set by the SEC.</item>
/// <item>The table of contents lists all ~23 items on one page, each followed by a page number ("Item 1. Business 3").
///       Those lines look exactly like headings, so a page with 10+ item lines is treated as the table of contents
///       and skipped. A real page never has more than ~6 (Items 10–14 are one line each).</item>
/// <item>Cross-references ("see Item 8 of this Form 10-K", "Item 1A of Part I") have no full stop after the number,
///       so the heading pattern (number + ".") doesn't match them.</item>
/// <item>No blank lines and no numbered paragraphs (unlike the tax treaties this project started with), so a long item is split at its lines.</item>
/// <item>Before Item 1: the cover page and table of contents; Amazon's PDF also starts with the 12-page shareholder letter.</item>
/// </list>
/// Tokens are estimated (characters ÷ 4): close enough for a cap, no tokenizer package needed.
/// </remarks>
public sealed class ItemChunker(int maxTokens = 800, int overlapLines = 3) : IChunker
{
    public int MaxTokens { get; } = maxTokens;

    /// <summary>How many lines of the previous piece a new piece repeats.</summary>
    public int OverlapLines { get; } = overlapLines;

    /// <summary>A page with at least this many item headings is the table of contents.</summary>
    private const int TableOfContentsThreshold = 10;

    // "Item 7. Management's Discussion …" as a whole line. Group 1 = the item ("1A", "7"), group 2 = the title.
    // [ \t] instead of \s: \s also matches "\n" and would let a match run into the next line.
    private static readonly Regex Heading = new(
        @"^[ \t]*ITEM[ \t]+(\d{1,2}[A-C]?)\.[ \t]*(.*)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);

    public IReadOnlyList<TextChunk> Chunk(IReadOnlyList<string> pages, string report)
    {
        var (text, pageStarts) = JoinPages(pages);

        var chunks = new List<TextChunk>();
        foreach (var section in FindSections(text, pageStarts))
        {
            foreach (var piece in Pack(SplitIntoLines(section)))
            {
                var content = piece.Text.Trim();
                if (content.Length == 0) continue;
                // Each piece gets the page where *it* starts: Item 8 runs over ~40 pages, its heading page wouldn't help.
                var page = PageAt(pageStarts, piece.Start);
                chunks.Add(new TextChunk(chunks.Count, content, report, section.Item, section.Title, page, EstimateTokens(content)));
            }
        }
        return chunks;
    }

    /// <summary>A heading-to-heading slice of the joined text. <c>Start</c> is its offset in the joined text.</summary>
    private sealed record Section(int Start, string? Item, string? Title, string Text);

    /// <summary>A line (or a piece made of lines) and the offset where it starts in the joined text.</summary>
    private sealed record Span(int Start, string Text);

    /// <summary>Cuts the text at the real item headings: [text before Item 1] + one section per item.</summary>
    private static List<Section> FindSections(string text, List<int> pageStarts)
    {
        var headings = RealHeadings(text, pageStarts);
        var sections = new List<Section>();

        var firstHeading = headings.Count > 0 ? headings[0].Index : text.Length;
        if (!string.IsNullOrWhiteSpace(text[..firstHeading]))
        {
            sections.Add(new Section(0, null, null, text[..firstHeading]));
        }

        foreach (var (i, match) in headings.Index())
        {
            var end = i + 1 == headings.Count ? text.Length : headings[i + 1].Index;
            var item = match.Groups[1].Value.ToUpperInvariant();
            var title = Regex.Replace(match.Groups[2].Value, @"\s+", " ").Trim(); // Tesla: tabs between words
            sections.Add(new Section(match.Index, item, title.Length > 0 ? title : null, text[match.Index..end]));
        }
        return sections;
    }

    /// <summary>
    /// Every "Item N." line, minus the table of contents (pages with 10+ of them), keeping only the first
    /// occurrence of each item, in document order.
    /// </summary>
    private static List<Match> RealHeadings(string text, List<int> pageStarts)
    {
        var all = Heading.Matches(text).ToList();
        var tocPages = all
            .GroupBy(m => PageAt(pageStarts, m.Index))
            .Where(g => g.Count() >= TableOfContentsThreshold)
            .Select(g => g.Key)
            .ToHashSet();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return all
            .Where(m => !tocPages.Contains(PageAt(pageStarts, m.Index)))
            .Where(m => seen.Add(m.Groups[1].Value))
            .ToList();
    }

    /// <summary>A section's lines, each keeping its "\n" and its offset in the joined text.</summary>
    private static List<Span> SplitIntoLines(Section section)
    {
        var lines = new List<Span>();
        var offset = section.Start;
        foreach (var line in Regex.Split(section.Text, @"(?<=\n)")) // split after each "\n": every line keeps its break
        {
            if (line.Length > 0) lines.Add(new Span(offset, line));
            offset += line.Length;
        }
        return lines;
    }

    /// <summary>
    /// Greedy packing: add lines while the piece stays ≤ MaxTokens; on overflow, close the piece and start the next one
    /// with the closed piece's last <see cref="OverlapLines"/> lines (fewer if they'd leave no room for the next line).
    /// </summary>
    private List<Span> Pack(List<Span> lines)
    {
        var pieces = new List<Span>();
        var current = new List<Span>();

        foreach (var line in lines)
        {
            if (current.Count > 0 && EstimateTokens(Join(current) + line.Text) > MaxTokens)
            {
                pieces.Add(new Span(current[0].Start, Join(current)));

                var overlap = current.TakeLast(OverlapLines).ToList();
                while (overlap.Count > 0 && EstimateTokens(Join(overlap) + line.Text) > MaxTokens)
                {
                    overlap.RemoveAt(0); // drop the oldest overlap line until the next line fits
                }
                current = overlap;
            }
            current.Add(line);
        }

        if (current.Count > 0)
        {
            pieces.Add(new Span(current[0].Start, Join(current))); // the last piece
        }
        return pieces;
    }

    private static string Join(List<Span> spans) => string.Concat(spans.Select(s => s.Text));

    /// <summary>~4 characters per token for English text. A cap, not a bill.</summary>
    public static int EstimateTokens(string text) => (text.Length + 3) / 4;

    /// <summary>Joins the pages into one string and remembers where each page starts in it.</summary>
    internal static (string Text, List<int> PageStarts) JoinPages(IReadOnlyList<string> pages)
    {
        var sb = new StringBuilder();
        var starts = new List<int>(pages.Count);
        foreach (var page in pages)
        {
            starts.Add(sb.Length);
            sb.Append(page);
            if (page.Length > 0 && page[^1] != '\n') sb.Append('\n'); // a heading at the top of the next page must start a line
        }
        return (sb.ToString(), starts);
    }

    /// <summary>The 1-based page that contains <paramref name="offset"/>: the last page whose start is ≤ offset (binary search).</summary>
    internal static int PageAt(List<int> pageStarts, int offset)
    {
        int lo = 0, hi = pageStarts.Count - 1, found = 0;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (pageStarts[mid] <= offset) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found + 1;
    }
}
