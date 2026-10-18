using System.Text;

namespace ButterKnife.Services;

/// <summary>A compaction checkpoint: <see cref="Summary"/> stands in for the first <see cref="Through"/> messages.</summary>
public sealed record CodeCompaction(string Summary, int Through);

/// <summary>
/// Keeps a Code session inside the model's window, the way ConversationCompactor does for chats but shaped for tool
/// transcripts. Two measures, both on the wire only (the stored transcript never changes):
/// <list type="bullet">
/// <item>Old tool results are cleared: once the results after the checkpoint add up to more than a share of the window,
/// older long ones become a one-line note (the call stays, so the model knows what it did and can ask again).</item>
/// <item>Compaction: everything before the last few user prompts is summarised by the model (requests, files read and
/// changed, command outcomes) and the summary rides in the system prompt. The checkpoint is always a user message, so no
/// tool call is ever separated from its result.</item>
/// </list>
/// </summary>
public static class CodeCompactor
{
    public const string SummarizerSystemPrompt = """
        You summarise sessions between a user and a coding agent that works in the user's project folder through tools,
        so the agent can carry on with less context. Write a compact, faithful summary in markdown covering:
        - What the user asked for, in order, their preferences, and whether each request was completed.
        - Files read or searched, and what was learned from them that still matters (names, paths, where things are).
        - Files created or changed, and what changed in each.
        - Commands run and their outcome (passed, or failed with which error).
        - Decisions made, and open problems or next steps.
        Keep exact file paths, identifiers, error messages and numbers. Do not invent anything. Output only the summary.
        """;

    /// <summary>Heads the summary inside the agent's system prompt.</summary>
    public const string SummaryPreamble = "## Earlier in this session (summary)";

    /// <summary>Results this short are never cleared: edit confirmations and short errors cost little and matter.</summary>
    public const int ClipMinChars = 400;

    /// <summary>Share of the window the tool results after the checkpoint may take before older ones are cleared.</summary>
    public const double ResultShare = 0.5;

    /// <summary>Window assumed for clearing old results when the server does not say (compaction needs a known window).</summary>
    public const int FallbackWindow = 32_768;

    /// <summary>How much of a tool result (head and tail) and of a call's arguments the summariser sees.</summary>
    private static readonly int[] ExcerptSteps = [800, 300, 100, 0];

    private const int MaxArgumentChars = 300;

    /// <summary>The system prompt section that carries the summary, appended by CodePrompt.Compose.</summary>
    public static string SummarySection(string summary) =>
        $"{SummaryPreamble}\n\n{summary.Trim()}\n\nThe messages that follow continue this session.";

    /// <summary>
    /// Where the messages sent to the model start: <paramref name="through"/>, moved back to the user message at or
    /// before it if it does not point at one (a transcript repaired after it was stored), so a call never loses its result.
    /// </summary>
    public static int StartOf(IReadOnlyList<ChatMessage> history, int through)
    {
        var start = Math.Clamp(through, 0, history.Count);
        while (start > 0 && start < history.Count && history[start].Role != ChatRole.User)
        {
            start--;
        }
        return start;
    }

    /// <summary>
    /// The next checkpoint: the user message that starts the last <paramref name="keepPrompts"/> prompts, or, when there
    /// are not that many after the current checkpoint, the last prompt (so the turn in progress is all that stays verbatim).
    /// Null when the checkpoint cannot move forward, i.e. at most one prompt follows it.
    /// </summary>
    public static int? FindCheckpoint(IReadOnlyList<ChatMessage> history, int through, int keepPrompts)
    {
        var start = StartOf(history, through);
        var prompts = new List<int>();
        for (var i = start + 1; i < history.Count; i++)
        {
            if (history[i].Role == ChatRole.User)
            {
                prompts.Add(i);
            }
        }
        if (prompts.Count == 0)
        {
            return null;
        }
        return prompts[prompts.Count - Math.Clamp(keepPrompts, 1, prompts.Count)];
    }

    /// <summary>Characters of tool results kept verbatim for a window (unknown: <see cref="FallbackWindow"/>).</summary>
    public static int ResultBudget(int? contextWindow) =>
        (int)((contextWindow is > 0 ? contextWindow.Value : FallbackWindow) * TokenEstimator.CharsPerToken * ResultShare);

    /// <summary>
    /// A copy of <paramref name="messages"/> in which, counting from the newest, every long tool result past
    /// <paramref name="budgetChars"/> is replaced by a note. The results of the latest step are always kept: the model
    /// asked for them and has not seen them yet.
    /// </summary>
    public static List<ChatMessage> ClipOldResults(IReadOnlyList<ChatMessage> messages, int budgetChars)
    {
        var result = messages.ToList();
        var lastStep = -1;
        for (var i = result.Count - 1; i >= 0; i--)
        {
            if (result[i].Role == ChatRole.Assistant && result[i].HasToolCalls)
            {
                lastStep = i;
                break;
            }
        }

        var used = 0;
        for (var i = result.Count - 1; i >= 0; i--)
        {
            var message = result[i];
            if (message.Role != ChatRole.Tool || message.Content.Length <= ClipMinChars)
            {
                continue;
            }
            used += message.Content.Length;
            if (i > lastStep || used <= budgetChars)
            {
                continue;
            }
            result[i] = message with { Content = ClearedNote(message) };
        }
        return result;
    }

    public static string ClearedNote(ChatMessage result)
    {
        var lines = result.Content.Count(c => c == '\n') + 1;
        return $"[This {result.ToolName ?? "tool"} result ({lines} line{(lines == 1 ? "" : "s")}) was cleared from the context to save space. Call the tool again if you still need it.]";
    }

    /// <summary>
    /// The summarisation request: the previous summary, then the older messages as a plain transcript in which calls
    /// show their arguments and results their head and tail. When <paramref name="maxChars"/> is given the excerpts
    /// shrink until the transcript fits, and as a last resort its middle is left out.
    /// </summary>
    public static IReadOnlyList<ChatMessage> BuildSummaryRequest(string? previousSummary, IReadOnlyList<ChatMessage> older, int? maxChars = null)
    {
        var transcript = "";
        foreach (var excerpt in ExcerptSteps)
        {
            transcript = Transcript(previousSummary, older, excerpt);
            if (maxChars is not { } max || transcript.Length <= max)
            {
                break;
            }
        }
        if (maxChars is { } limit && transcript.Length > limit)
        {
            var half = Math.Max(0, limit / 2 - 40);
            transcript = transcript[..half] + "\n\n[… part of the session was left out to fit …]\n\n" + transcript[^half..];
        }

        return
        [
            new ChatMessage(ChatRole.System, SummarizerSystemPrompt),
            new ChatMessage(ChatRole.User, transcript + "\n---\nSummarise the session above into a single up-to-date summary, as instructed. Output only the summary."),
        ];
    }

    private static string Transcript(string? previousSummary, IReadOnlyList<ChatMessage> older, int excerptChars)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(previousSummary))
        {
            sb.Append("A summary of the session before these messages:\n\n").Append(previousSummary.Trim()).Append("\n\nThe session then continued:\n\n");
        }

        foreach (var message in older)
        {
            switch (message.Role)
            {
                case ChatRole.User:
                    sb.Append("User: ").Append(message.Content.Trim()).Append("\n\n");
                    break;
                case ChatRole.Assistant:
                    if (message.Content.Trim() is { Length: > 0 } text)
                    {
                        sb.Append("Assistant: ").Append(text).Append('\n');
                    }
                    foreach (var call in message.ToolCalls)
                    {
                        sb.Append("Assistant called ").Append(call.Name).Append(' ').Append(Clip(call.Arguments, MaxArgumentChars)).Append('\n');
                    }
                    sb.Append('\n');
                    break;
                case ChatRole.Tool:
                    sb.Append("Result of ").Append(message.ToolName ?? "tool").Append(message.IsError ? " (error)" : "").Append(':');
                    sb.Append(excerptChars == 0 ? $" [{message.Content.Length} characters]" : "\n" + HeadAndTail(message.Content.Trim(), excerptChars)).Append("\n\n");
                    break;
            }
        }
        return sb.ToString();
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    /// <summary>Command output ends with what matters (errors, the exit code), so a long result keeps both ends.</summary>
    private static string HeadAndTail(string text, int max) =>
        text.Length <= max ? text : $"{text[..(max / 2)]}\n[… {text.Length - max} characters left out …]\n{text[^(max / 2)..]}";

    /// <summary>Runs the summarisation through the session's model (no tools) and returns the summary.</summary>
    public static async Task<string> SummarizeAsync(
        ILlmClient client,
        string model,
        string? previousSummary,
        IReadOnlyList<ChatMessage> older,
        int? contextWindow,
        CancellationToken cancellationToken)
    {
        // Leave room for the instructions and the summary itself.
        int? maxChars = contextWindow is > 0 ? (int)(contextWindow.Value * TokenEstimator.CharsPerToken * 0.6) : null;
        var request = BuildSummaryRequest(previousSummary, older, maxChars);
        var summary = new StringBuilder();
        var splitter = new ThinkTagSplitter(); // Qwen-style models think inline; that is not part of the summary

        await foreach (var delta in client.StreamChatAsync(model, request, null, cancellationToken))
        {
            if (delta.Text is { Length: > 0 } text)
            {
                foreach (var piece in splitter.Feed(text))
                {
                    summary.Append(piece.Text);
                }
            }
        }
        if (splitter.Flush() is { } rest)
        {
            summary.Append(rest.Text);
        }

        var result = summary.ToString().Trim();
        if (result.Length == 0)
        {
            throw new InvalidOperationException("The model returned an empty summary.");
        }
        return result;
    }

    /// <summary>
    /// Summarises everything from the current checkpoint up to the next one (see <see cref="FindCheckpoint"/>).
    /// Null when there is nothing to compact.
    /// </summary>
    public static async Task<CodeCompaction?> CompactAsync(
        ILlmClient client,
        string model,
        IReadOnlyList<ChatMessage> history,
        string? summary,
        int through,
        int keepPrompts,
        int? contextWindow,
        CancellationToken cancellationToken)
    {
        if (FindCheckpoint(history, through, keepPrompts) is not { } checkpoint)
        {
            return null;
        }
        var start = StartOf(history, through);
        var older = history.Skip(start).Take(checkpoint - start).ToList();
        var text = await SummarizeAsync(client, model, start == 0 ? null : summary, older, contextWindow, cancellationToken);
        return new CodeCompaction(text, checkpoint);
    }
}
