using Microsoft.Extensions.AI;
using FinDoc.Core.Prompting;
using FinDoc.Core.Retrieval;

namespace FinDoc.Tests.Prompting;

public class CitationPromptTests
{
    private static readonly RetrievedChunk NetIncome =
        new("apple-fy2026", "Item 8", "Net income $ 132,175 $ 112,010 $ 93,736", 0.61);

    private static readonly RetrievedChunk CoverPage =
        new("amazon-fy2025", null, "UNITED STATES SECURITIES AND EXCHANGE COMMISSION", 0.40);

    [Fact]
    public void Rules_go_in_the_system_message_and_the_question_comes_last_in_the_user_message()
    {
        var messages = CitationPrompt.Build("What was Apple's net income in 2026?", [NetIncome]);

        Assert.Equal(2, messages.Count);
        Assert.Equal(ChatRole.System, messages[0].Role);
        Assert.Contains($"\"{CitationPrompt.Refusal}\"", messages[0].Text);
        Assert.Equal(ChatRole.User, messages[1].Role);
        Assert.EndsWith("Question: What was Apple's net income in 2026?", messages[1].Text);
    }

    [Fact]
    public void A_chunk_with_an_item_is_labelled_with_report_and_item()
    {
        Assert.Equal("sources-[apple-fy2026]-[Item 8]", CitationPrompt.LabelFor(NetIncome));
    }

    [Fact]
    public void A_chunk_without_an_item_is_labelled_with_the_report_only()
    {
        Assert.Equal("sources-[amazon-fy2025]", CitationPrompt.LabelFor(CoverPage));
    }

    [Fact]
    public void Each_label_sits_on_the_line_right_above_its_text()
    {
        var user = CitationPrompt.Build("Any question?", [NetIncome, CoverPage])[1].Text;

        Assert.Contains("sources-[apple-fy2026]-[Item 8]\nNet income $ 132,175", user);
        Assert.Contains("sources-[amazon-fy2025]\nUNITED STATES SECURITIES", user);
    }

    [Fact]
    public void FindCitations_reads_both_forms_comma_separated_without_repeats()
    {
        var answer = "Net income rose 41.01% sources-[apple-fy2026]-[Item 8], sources-[amazon-fy2025]. " +
                     "Again: sources-[apple-fy2026]-[Item 8]";

        Assert.Equal(
            ["sources-[apple-fy2026]-[Item 8]", "sources-[amazon-fy2025]"],
            CitationPrompt.FindCitations(answer));
    }
}
