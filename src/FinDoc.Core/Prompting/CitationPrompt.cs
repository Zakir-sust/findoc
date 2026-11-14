using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using FinDoc.Core.Retrieval;

namespace FinDoc.Core.Prompting;

/// <summary>
/// Turns a question and its retrieved chunks into the messages for the model (C12 spec):
/// the rules go in the system message; the labelled sources and then the question go in the user message.
/// </summary>
/// <remarks>
/// Each source's label is the exact citation the model must copy (G9: a model follows the labels it can see,
/// not a rule that describes them). So checking a citation is a string match: it must be one of the labels sent.
/// </remarks>
public static partial class CitationPrompt
{
    /// <summary>The whole answer when the sources don't contain it.</summary>
    public const string Refusal = "Not in Source";

    // The rules from the C12 spec, in order. Changing them changes every answer: rerun the integration tests after.
    // Run 1 had "Don't answer if it can't be built from the sources" next to "you may calculate": the model read them
    // as opposite rules and did both (refused, then answered). Run 2: one refusal condition, calculation stated plainly.
    private static readonly string SystemRules = string.Join("\n",
        "Answer only from the sources and cite them.",
        "Calculating from the numbers in the sources is permitted to build the answer.",
        "Give decimal answers to 2 decimal places.",
        "Cite with the label at the top of each source, exactly as written, for example sources-[apple-fy2026]-[Item 8]. For several sources, separate the labels with commas.",
        $"If the answer can't be derived from the sources in any way, say \"{Refusal}\".");

    /// <summary>
    /// The citation for a chunk: <c>sources-[apple-fy2026]-[Item 8]</c>, or <c>sources-[amazon-fy2025]</c>
    /// for a chunk with no item (cover pages and the like: 27 of the 610 chunks).
    /// </summary>
    public static string LabelFor(RetrievedChunk chunk) =>
        chunk.Item is null
            ? $"sources-[{chunk.Report}]"
            : $"sources-[{chunk.Report}]-[{chunk.Item}]";

    public static List<ChatMessage> Build(string question, IReadOnlyList<RetrievedChunk> chunks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        // Each source = its label on one line, its text right under it, a blank line before the next one.
        // Two chunks from the same item get the same label: the citation points at the item, not the chunk.
        var user = new StringBuilder("Sources:\n\n");
        foreach (var chunk in chunks)
            user.Append(LabelFor(chunk)).Append('\n').Append(chunk.Content.Trim()).Append("\n\n");

        // The question last, so it is the final thing the model reads before answering.
        user.Append("Question: ").Append(question.Trim());

        return
        [
            new(ChatRole.System, SystemRules),
            new(ChatRole.User, user.ToString()),
        ];
    }

    /// <summary>
    /// Every citation in an answer, in order, without repeats. Both forms match:
    /// <c>sources-[report]-[Item N]</c> and <c>sources-[report]</c>. Compare them with the labels sent:
    /// a citation that is not one of them points at no source.
    /// </summary>
    public static IReadOnlyList<string> FindCitations(string answer) =>
        CitationPattern().Matches(answer).Select(m => m.Value).Distinct().ToList();

    // "sources-[" + anything up to "]", then optionally "-[" + anything up to "]". No line breaks inside the brackets.
    [GeneratedRegex(@"sources-\[[^\]\r\n]+\](?:-\[[^\]\r\n]+\])?")]
    private static partial Regex CitationPattern();
}
