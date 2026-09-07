using System.Text;

namespace ButterKnife.Services;

/// <summary>
/// Client-side compaction that works on every backend: older turns are summarised by the model and the
/// summary rides along in the system prompt while only the recent turns are sent verbatim.
/// The stored transcript is never changed; only what goes on the wire.
/// </summary>
public sealed class ConversationCompactor
{
    public const string SummarizerSystemPrompt =
        "You write faithful, compact summaries of conversations so they can be continued later with less context. " +
        "Preserve facts, decisions, requirements, names, numbers, file paths, code snippets that are still relevant, " +
        "the user's preferences and tone, and any open questions or unfinished tasks. " +
        "Write in the third person (\"the user asked…\", \"the assistant suggested…\"). Do not add commentary. Use plain markdown.";

    /// <summary>Prepended to the summary inside the system prompt of later requests.</summary>
    public const string SummaryPreamble = "## Earlier in this conversation (summary)";

    /// <summary>System prompt for a request: the persona (if any) followed by the running summary (if any).</summary>
    public static string? ComposeSystemPrompt(string? personaPrompt, string? summary)
    {
        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(personaPrompt))
        {
            parts.Add(personaPrompt.Trim());
        }
        if (!string.IsNullOrWhiteSpace(summary))
        {
            parts.Add($"{SummaryPreamble}\n\n{summary.Trim()}\n\nThe messages that follow continue this conversation.");
        }
        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }

    /// <summary>Builds the summarisation request. Images cannot be summarised, so they become a short note.</summary>
    public static IReadOnlyList<ChatMessage> BuildSummaryRequest(string? previousSummary, IReadOnlyList<ChatMessage> olderMessages)
    {
        var transcript = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(previousSummary))
        {
            transcript.AppendLine("A summary of the conversation before these messages:");
            transcript.AppendLine();
            transcript.AppendLine(previousSummary.Trim());
            transcript.AppendLine();
            transcript.AppendLine("The conversation then continued:");
            transcript.AppendLine();
        }

        foreach (var message in olderMessages.Where(m => m.Role != ChatRole.System))
        {
            transcript.Append(message.Role == ChatRole.User ? "User: " : "Assistant: ");
            if (message.HasImages)
            {
                transcript.Append($"[{message.Images.Count} image{(message.Images.Count == 1 ? "" : "s")} attached] ");
            }
            transcript.AppendLine(message.Content);
            transcript.AppendLine();
        }

        transcript.AppendLine("---");
        transcript.Append("Summarise everything above into a single up-to-date summary, as instructed. Output only the summary.");

        return
        [
            new ChatMessage(ChatRole.System, SummarizerSystemPrompt),
            new ChatMessage(ChatRole.User, transcript.ToString()),
        ];
    }

    /// <summary>Runs the summarisation through the given model and returns the summary text.</summary>
    public async Task<string> SummarizeAsync(
        ILlmClient client,
        string model,
        string? previousSummary,
        IReadOnlyList<ChatMessage> olderMessages,
        CancellationToken cancellationToken = default)
    {
        var request = BuildSummaryRequest(previousSummary, olderMessages);
        var summary = new StringBuilder();

        await foreach (var delta in client.StreamChatAsync(model, request, cancellationToken))
        {
            if (delta.Text is { } text)
            {
                summary.Append(text);
            }
        }

        var result = summary.ToString().Trim();
        if (result.Length == 0)
        {
            throw new InvalidOperationException("The model returned an empty summary.");
        }
        return result;
    }
}
