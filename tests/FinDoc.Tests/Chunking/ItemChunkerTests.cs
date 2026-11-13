using System.Text;
using FinDoc.Core.Chunking;

namespace FinDoc.Tests.Chunking;

/// <summary>
/// The chunker's spec. The fake report is shaped like the real PdfPig output of the 4 annual reports (checked 13 Nov):
/// the heading is one line "Item 1A. Risk Factors" (or "ITEM\t7.\tMANAGEMENT’S …" in Tesla's), the table of contents
/// lists every item with a page number on one page, no blank lines, no numbered paragraphs.
/// </summary>
public class ItemChunkerTests
{
    private const string Report = "testco-fy2026";

    private static string TableOfContents()
    {
        var sb = new StringBuilder("TABLE OF CONTENTS\nPart I\n");
        string[] items = ["1. Business 3", "1A. Risk Factors 5", "1B. Unresolved Staff Comments 9", "1C. Cybersecurity 9",
            "2. Properties 10", "3. Legal Proceedings 10", "4. Mine Safety Disclosures 10", "5. Market for Registrant’s Common Equity 11",
            "6. [Reserved] 12", "7. Management’s Discussion and Analysis 12", "7A. Quantitative and Qualitative Disclosures 20",
            "8. Financial Statements and Supplementary Data 21"];
        foreach (var item in items) sb.Append($"Item {item}\n");
        return sb.ToString();
    }

    // Four PDF pages: cover + table of contents; Items 1 and 1A; Item 1A continues, then Tesla-style Item 7; Item 8.
    private static string[] ShortReport() =>
    [
        "UNITED STATES SECURITIES AND EXCHANGE COMMISSION\nFORM 10-K\nTestCo, Inc.\n" + TableOfContents(),

        "Item 1. Business\n" +
        "TestCo designs and sells garden robots to customers in 40 countries.\n" +
        "As of the end of fiscal 2026, the Company had about 1,200 full-time employees.\n" +
        "Item 1A. Risk Factors\n" +
        "The Company depends on a single supplier for its battery cells.\n",

        "A shortage of battery cells would reduce net sales, as discussed in Item 8 of this Form 10-K.\n" +
        "ITEM\t7.\tMANAGEMENT’S\tDISCUSSION\tAND\tANALYSIS\n" +
        "Total net sales were $310 million in fiscal 2026, up 12% from fiscal 2025.\n",

        "Item 8. Financial Statements and Supplementary Data\n" +
        "Net income was $41 million in fiscal 2026.\n",
    ];

    // One item of 24 lines, ~20 tokens each (~80 characters): ~480 tokens in all.
    private static string LongItem()
    {
        var sb = new StringBuilder("Item 1A. Risk Factors\n");
        for (var n = 1; n <= 24; n++)
        {
            sb.Append($"Risk line {n:00}: supply, demand, regulation and currency could all affect results.\n");
        }
        return sb.ToString();
    }

    [Fact]
    public void Splits_at_item_headings_and_keeps_item_and_title()
    {
        var chunks = new ItemChunker().Chunk(ShortReport(), Report);

        var items = chunks.Where(c => c.Item is not null).ToList();
        Assert.Equal(new[] { "1", "1A", "7", "8" }, items.Select(c => c.Item));
        Assert.Equal(new[] { "Business", "Risk Factors", "MANAGEMENT’S DISCUSSION AND ANALYSIS", "Financial Statements and Supplementary Data" },
            items.Select(c => c.Title)); // Tesla's tabs become single spaces

        Assert.StartsWith("Item 1A. Risk Factors", items[1].Content);
        Assert.Contains("A shortage of battery cells", items[1].Content); // Item 1A runs across the page break
        Assert.DoesNotContain("Total net sales", items[1].Content);       // stops at the next heading

        Assert.All(chunks, c => Assert.Equal(Report, c.Report));
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.Ordinal));
    }

    [Fact]
    public void Table_of_contents_lines_are_not_headings()
    {
        var chunks = new ItemChunker().Chunk(ShortReport(), Report);

        // The table of contents lists Item 1 … Item 8 on page 1; none of them may start a section there.
        Assert.DoesNotContain(chunks, c => c.Item is not null && c.Page == 1);
        Assert.Single(chunks, c => c.Item == "1");
    }

    [Fact]
    public void Text_before_item_1_is_its_own_chunk_without_an_item()
    {
        var chunks = new ItemChunker().Chunk(ShortReport(), Report);

        var first = chunks[0];
        Assert.Null(first.Item);
        Assert.Null(first.Title);
        Assert.StartsWith("UNITED STATES SECURITIES", first.Content);
        Assert.Contains("TABLE OF CONTENTS", first.Content); // the cover + table of contents live here
        Assert.DoesNotContain("garden robots", first.Content);
    }

    [Fact]
    public void A_cross_reference_without_a_full_stop_is_not_a_heading()
    {
        var chunks = new ItemChunker().Chunk(ShortReport(), Report);

        // "… as discussed in Item 8 of this Form 10-K." is body text of Item 1A, not the start of Item 8.
        Assert.Single(chunks, c => c.Item == "8");
        Assert.StartsWith("Item 8. Financial Statements", chunks.Single(c => c.Item == "8").Content);
    }

    [Fact]
    public void Page_is_the_pdf_page_where_the_chunk_starts()
    {
        var chunks = new ItemChunker().Chunk(ShortReport(), Report);

        int PageOf(string item) => chunks.Single(c => c.Item == item).Page;
        Assert.Equal(1, chunks[0].Page); // cover + table of contents
        Assert.Equal(2, PageOf("1"));
        Assert.Equal(2, PageOf("1A"));   // starts on page 2, continues on page 3
        Assert.Equal(3, PageOf("7"));    // heading in the middle of page 3
        Assert.Equal(4, PageOf("8"));
    }

    [Fact]
    public void Long_item_is_split_at_lines_under_the_cap_and_nothing_is_lost()
    {
        var chunks = new ItemChunker(maxTokens: 120).Chunk([LongItem()], Report);

        Assert.True(chunks.Count > 1, "an item of ~480 tokens should not fit in one 120-token chunk");
        Assert.All(chunks, c =>
        {
            Assert.Equal("1A", c.Item);
            Assert.Equal("Risk Factors", c.Title);
            Assert.True(c.TokenCount <= 120, $"chunk {c.Ordinal} has {c.TokenCount} tokens");
        });
        Assert.StartsWith("Item 1A. Risk Factors", chunks[0].Content);
        for (var n = 1; n <= 24; n++)
        {
            Assert.Contains(chunks, c => c.Content.Contains($"Risk line {n:00}:"));
        }
    }

    [Fact]
    public void Each_piece_starts_with_the_last_lines_of_the_piece_before()
    {
        var chunks = new ItemChunker(maxTokens: 120, overlapLines: 2).Chunk([LongItem()], Report);

        for (var i = 1; i < chunks.Count; i++)
        {
            var lastTwoLines = string.Join('\n', chunks[i - 1].Content.Split('\n').TakeLast(2));
            Assert.StartsWith(lastTwoLines, chunks[i].Content);
        }
    }
}
