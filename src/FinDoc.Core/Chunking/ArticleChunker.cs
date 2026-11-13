using System.Text;
using System.Text.RegularExpressions;

namespace FinDoc.Core.Chunking;

/// <summary>
/// Treaty-aware chunker (C08): one chunk per article. An article over the token cap is split at its numbered
/// paragraphs ("1. ", "2. "), and each piece after the first repeats the previous piece's last paragraph (overlap).
/// </summary>
/// <remarks>
/// What the real PDFs look like (PdfPig <c>ContentOrderTextExtractor</c>, checked 12 Nov on <c>sources/</c>):
/// <list type="bullet">
/// <item>"ARTICLE 10" alone on a line, the title ("Dividends") on the next line. One such line per article in the
///       bilateral treaties and the US Model (US–BD 29, US–UK 30, US–India 31, US–Japan 31, US–Germany 33, US Model 30).</item>
/// <item>The two protocols (DE 2006, JP 2013) have none, so the whole document is one "no article" section.
///       The UN Model 2017 has 88 (its commentary repeats article numbers).</item>
/// <item>No blank lines anywhere: every visual line ends in "\n", and paragraphs start with "1. ", "2. " at the start of a line.</item>
/// <item>The printed page number sits on its own line ("14 ") at the start of a page.</item>
/// </list>
/// Tokens are estimated (characters ÷ 4): close enough for a cap, no tokenizer package needed.
/// </remarks>
public sealed class ArticleChunker(int maxTokens = 800) : IChunker
{
    public int MaxTokens { get; } = maxTokens;

    // "ARTICLE 10" alone on its line. ^ … $ (Multiline) = the whole line, so "… under Article 10" at the end of a
    // sentence line isn't a heading. [ \t] instead of \s, because \s also matches "\n" and would let a match start
    // on the line before (even on the previous page → wrong page number).
    private static readonly Regex Heading = new(@"^[ \t]*ARTICLE[ \t]+(\d+)[ \t]*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);

    public IReadOnlyList<TextChunk> Chunk(IReadOnlyList<string> pages, string treaty)
    {
        var (text, pageStarts) = JoinPages(pages);

        var chunks = new List<TextChunk>();
        foreach (var section in FindSections(text))
        {
            // Every piece of a long article gets the page of the article's heading (the citation points there).
            var page = PageAt(pageStarts, section.Start);

            foreach (var piece in Pack(SplitIntoUnits(section.Text)))
            {
                var content = piece.Trim();
                if (content.Length == 0) continue;
                chunks.Add(new TextChunk(chunks.Count, content, treaty, section.Article, section.Title, page, EstimateTokens(content)));
            }
        }
        return chunks;
    }

    /// <summary>A heading-to-heading slice of the joined text. <c>Start</c> is its offset (for the page lookup).</summary>
    private sealed record Section(int Start, int? Article, string? Title, string Text);

    /// <summary>Cuts the text at the headings: [text before Article 1] + one section per article.</summary>
    private static List<Section> FindSections(string text)
    {
        var matches = Heading.Matches(text);
        var sections = new List<Section>();

        // Title + preamble before Article 1. A protocol has no headings, so this is its whole text.
        var firstHeading = matches.Count > 0 ? matches[0].Index : text.Length;
        if (!string.IsNullOrWhiteSpace(text[..firstHeading]))
        {
            sections.Add(new Section(0, null, null, text[..firstHeading]));
        }

        foreach (var (i, match) in matches.Index())
        {
            var start = match.Index;
            var end = i + 1 == matches.Count ? text.Length : matches[i + 1].Index; // a section ends where the next heading starts
            var article = int.Parse(match.Groups[1].Value);
            var body = text[start..end];
            var title = body.Split('\n')
                .Skip(1)                                    // the heading line itself
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.Length > 0);   // "Dividends"
            sections.Add(new Section(start, article, title, body));
        }

        return sections;
    }

    /// <summary>
    /// A section's "units" = its numbered paragraphs. PdfPig gives no blank lines, so the split is where a line
    /// starts with "1. ", "2. " …; the (?=…) lookahead splits before the number, so "1. " stays with its paragraph,
    /// and the heading + title come out as the first unit. A paragraph bigger than a whole chunk falls back to its lines.
    /// </summary>
    private List<string> SplitIntoUnits(string sectionText)
    {
        var units = new List<string>();
        foreach (var paragraph in Regex.Split(sectionText, @"(?=^[ \t]*\d+\.\s)", RegexOptions.Multiline))
        {
            if (paragraph.Length == 0) continue;

            if (EstimateTokens(paragraph) <= MaxTokens)
            {
                units.Add(paragraph);
                continue;
            }

            // (?<=\n) splits after each "\n", so every line keeps its line break.
            units.AddRange(Regex.Split(paragraph, @"(?<=\n)").Where(line => line.Length > 0));
        }
        return units;
    }

    /// <summary>
    /// Greedy packing: add units while the piece stays ≤ MaxTokens; on overflow, close the piece and start the next one
    /// with the closed piece's last unit (the overlap), if that still leaves room for the next unit.
    /// </summary>
    private List<string> Pack(List<string> units)
    {
        var pieces = new List<string>();
        var current = new List<string>();

        foreach (var unit in units)
        {
            if (current.Count > 0 && EstimateTokens(string.Concat(current) + unit) > MaxTokens)
            {
                pieces.Add(string.Concat(current));

                var overlap = current[^1];
                current = [];
                if (EstimateTokens(overlap + unit) <= MaxTokens)
                {
                    current.Add(overlap);
                }
            }
            current.Add(unit);
        }

        if (current.Count > 0)
        {
            pieces.Add(string.Concat(current)); // the last piece
        }
        return pieces;
    }

    // ---- Done (plumbing, by Claude) ------------------------------------------------------

    /// <summary>~4 characters per token for English text. A cap, not a bill.</summary>
    public static int EstimateTokens(string text) => (text.Length + 3) / 4;

    /// <summary>Joins the pages into one string and remembers where each page starts in it.</summary>
    internal static (string Text, List<int> PageStarts) JoinPages(IReadOnlyList<string> pages)
    {
        var sb = new StringBuilder();
        var starts = new List<int>(pages.Count);
        foreach (var page in pages)
        {
            // TODO 7 (stretch, Jakir): drop the lines that are only a printed page number ("14 ") from `page`
            //   before appending it. They end up inside chunks otherwise. Do it here, per page, so the offsets stay right.
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
