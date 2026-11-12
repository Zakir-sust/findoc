using System.Text;
using System.Text.RegularExpressions;
using TaxDoc.Core.Chunking;

namespace TaxDoc.Tests.Chunking;

/// <summary>
/// The chunker's spec (C08). The fake treaty text is shaped like the real PdfPig output (checked 12 Nov on sources/):
/// "ARTICLE N" alone on a line, the title on the next line, paragraphs start with "1. ", "2. ", no blank lines,
/// and the printed page number on its own line at the start of a page.
/// </summary>
public class ArticleChunkerTests
{
    private const string Treaty = "us-testland-2004";

    // Three PDF pages: preamble + Article 1 on page 1; Articles 2 and 3 on page 2; Article 3 continues on page 3, then Article 4.
    private static readonly string[] ShortTreaty =
    [
        "CONVENTION BETWEEN THE UNITED STATES OF AMERICA AND TESTLAND\n" +
        "FOR THE AVOIDANCE OF DOUBLE TAXATION\n" +
        "The Government of the United States of America and the Government of Testland,\n" +
        "desiring to conclude a Convention, have agreed as follows:\n" +
        "ARTICLE 1\n" +
        "General Scope\n" +
        "1. This Convention shall apply only to persons who are residents of one or\n" +
        "both of the Contracting States.\n",

        "2 \n" +
        "ARTICLE 2\n" +
        "Taxes Covered\n" +
        "1. The existing taxes to which this Convention shall apply are the Federal\n" +
        "income taxes imposed by the Internal Revenue Code.\n" +
        "ARTICLE 3\n" +
        "General Definitions\n" +
        "1. For the purposes of this Convention, the term \"person\" includes an\n" +
        "individual, an estate, a trust, a partnership and a company.\n",

        "3 \n" +
        "2. Any term not defined in this Convention shall have the meaning which it\n" +
        "has under the law of the State applying the Convention.\n" +
        "ARTICLE 4\n" +
        "Residence\n" +
        "1. The term \"resident of a Contracting State\" means any person who is liable\n" +
        "to tax therein by reason of his domicile or residence.\n",
    ];

    // One article of 8 paragraphs, ~48 tokens each (3 lines, ~190 characters).
    private static string LongArticle()
    {
        var sb = new StringBuilder("ARTICLE 10\nDividends\n");
        for (var n = 1; n <= 8; n++)
        {
            sb.Append($"{n}. Paragraph {n}: dividends paid by a company which is a resident\n")
              .Append("of a Contracting State to a resident of the other Contracting State\n")
              .Append($"may be taxed in that other State, subject to paragraph {n}.\n");
        }
        return sb.ToString();
    }

    [Fact]
    public void Splits_at_article_headings_and_keeps_number_and_title()
    {
        var chunks = new ArticleChunker().Chunk(ShortTreaty, Treaty);

        var articles = chunks.Where(c => c.Article is not null).ToList();
        Assert.Equal(new[] { 1, 2, 3, 4 }, articles.Select(c => c.Article!.Value));
        Assert.Equal(new[] { "General Scope", "Taxes Covered", "General Definitions", "Residence" }, articles.Select(c => c.Title));

        Assert.StartsWith("ARTICLE 2", articles[1].Content);
        Assert.DoesNotContain("ARTICLE 3", articles[1].Content);              // stops at the next heading
        Assert.Contains("2. Any term not defined", articles[2].Content);       // Article 3 runs across the page break

        Assert.All(chunks, c => Assert.Equal(Treaty, c.Treaty));
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.Ordinal));
    }

    [Fact]
    public void Text_before_article_1_is_its_own_chunk_without_an_article()
    {
        var chunks = new ArticleChunker().Chunk(ShortTreaty, Treaty);

        var first = chunks[0];
        Assert.Null(first.Article);
        Assert.Null(first.Title);
        Assert.StartsWith("CONVENTION BETWEEN", first.Content);
        Assert.DoesNotContain("ARTICLE 1", first.Content);
    }

    [Fact]
    public void Page_is_the_pdf_page_where_the_article_heading_is()
    {
        var chunks = new ArticleChunker().Chunk(ShortTreaty, Treaty);

        int PageOf(int article) => chunks.Single(c => c.Article == article).Page;
        Assert.Equal(1, chunks[0].Page); // preamble
        Assert.Equal(1, PageOf(1));
        Assert.Equal(2, PageOf(2));      // heading at the top of page 2
        Assert.Equal(2, PageOf(3));      // heading on page 2, text continues on page 3
        Assert.Equal(3, PageOf(4));      // heading in the middle of page 3
    }

    [Fact]
    public void Long_article_is_split_at_numbered_paragraphs_under_the_cap()
    {
        var chunks = new ArticleChunker(maxTokens: 120).Chunk([LongArticle()], Treaty);

        Assert.True(chunks.Count > 1, "an article of ~390 tokens should not fit in one 120-token chunk");
        Assert.All(chunks, c =>
        {
            Assert.Equal(10, c.Article);
            Assert.Equal("Dividends", c.Title);
            Assert.True(c.TokenCount <= 120, $"chunk {c.Ordinal} has {c.TokenCount} tokens");
        });
        Assert.StartsWith("ARTICLE 10", chunks[0].Content);
        Assert.All(chunks.Skip(1), c => Assert.Matches(@"^\d+\. ", c.Content)); // later pieces start at a paragraph

        for (var n = 1; n <= 8; n++)
        {
            Assert.Contains(chunks, c => c.Content.Contains($"{n}. Paragraph {n}:")); // nothing lost
        }
    }

    [Fact]
    public void Each_piece_starts_with_the_last_paragraph_of_the_piece_before()
    {
        var chunks = new ArticleChunker(maxTokens: 120).Chunk([LongArticle()], Treaty);

        for (var i = 1; i < chunks.Count; i++)
        {
            var previous = chunks[i - 1].Content;
            var lastParagraph = previous[Regex.Matches(previous, @"^\d+\. ", RegexOptions.Multiline)[^1].Index..];
            Assert.StartsWith(lastParagraph, chunks[i].Content);
        }
    }

    [Fact]
    public void A_paragraph_longer_than_the_cap_is_split_at_its_lines()
    {
        // Paragraph 1 = 13 numbered lines of ~55 characters ≈ 180 tokens: 3× the cap on its own,
        // so the chunker has to fall back to splitting it at its lines.
        var sb = new StringBuilder("ARTICLE 10\nDividends\n1. ");
        foreach (var i in Enumerable.Range(1, 13))
        {
            sb.Append($"Line {i:00} of one very long paragraph about dividend tax.\n");
        }

        var chunks = new ArticleChunker(maxTokens: 60).Chunk([sb.ToString()], Treaty);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.TokenCount <= 60, $"chunk {c.Ordinal} has {c.TokenCount} tokens"));
        foreach (var i in Enumerable.Range(1, 13))
        {
            Assert.Contains(chunks, c => c.Content.Contains($"Line {i:00} ")); // nothing lost
        }
    }
}
