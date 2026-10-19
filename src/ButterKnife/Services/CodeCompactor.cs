using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ButterKnife.Services;

/// <summary>A compaction checkpoint: <see cref="Summary"/> stands in for the first <see cref="Through"/> messages.</summary>
public sealed record CodeCompaction(string Summary, int Through);

/// <summary>
/// Keeps a Code session inside the model's window, the way ConversationCompactor does for chats but shaped for tool
/// transcripts. Three measures, all on the wire only (the stored transcript never changes):
/// <list type="bullet">
/// <item>Old tool results are cleared: once the results after the checkpoint add up to more than a share of the window,
/// older long ones become a one-line note (the call stays, so the model knows what it did and can ask again).</item>
/// <item>Old edits are shrunk: a write_file or edit_file call that succeeded before the latest step keeps its path but
/// not its text, since the change is made and its result says so.</item>
/// <item>Compaction: everything before the last few user prompts, or within one long turn before its last few steps, is
/// summarised by the model (requests, files read and changed, command outcomes) and the summary rides in the system
/// prompt. The checkpoint is a user message or the start of a step whose predecessor's calls are all answered, so no tool
/// call is ever separated from its result; after a checkpoint inside a turn the turn's prompt is sent again first.</item>
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
        The session may end in the middle of a request; then say what has been done for it so far and what remains.
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

    /// <summary>Steps of the turn in progress kept verbatim when a checkpoint goes inside it.</summary>
    public const int KeepRecentSteps = 2;

    /// <summary>Follows the turn's prompt when it is sent again after a checkpoint inside that turn.</summary>
    public const string ResentNote = "[Sent again: you have been working on this request. What you did for it before the steps below is summarised in the system prompt.]";

    /// <summary>A write_file or edit_file text this long is left out of an old call.</summary>
    private const int ShrinkMinChars = 80;

    private static readonly JsonSerializerOptions ArgumentsJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>How much of a tool result (head and tail) and of a call's arguments the summariser sees.</summary>
    private static readonly int[] ExcerptSteps = [800, 300, 100, 0];

    private const int MaxArgumentChars = 300;

    /// <summary>The system prompt section that carries the summary, appended by CodePrompt.Compose.</summary>
    public static string SummarySection(string summary) =>
        $"{SummaryPreamble}\n\n{summary.Trim()}\n\nThe messages that follow continue this session.";

    /// <summary>
    /// Whether the messages sent to the model may start at <paramref name="index"/>: a user message, or a reply that
    /// follows the results of a step whose calls are all answered (a step boundary inside a turn). Starting anywhere else
    /// would separate a call from its result. The ends of the history count too.
    /// </summary>
    public static bool IsBoundary(IReadOnlyList<ChatMessage> history, int index)
    {
        if (index <= 0 || index >= history.Count || history[index].Role == ChatRole.User)
        {
            return true;
        }
        if (history[index].Role != ChatRole.Assistant || history[index - 1].Role != ChatRole.Tool)
        {
            return false;
        }
        var answered = new HashSet<string?>(StringComparer.Ordinal);
        for (var i = index - 1; i >= 0; i--)
        {
            if (history[i].Role == ChatRole.Tool)
            {
                answered.Add(history[i].ToolCallId);
                continue;
            }
            return history[i].Role == ChatRole.Assistant && history[i].HasToolCalls && history[i].ToolCalls.All(c => answered.Contains(c.Id));
        }
        return false;
    }

    /// <summary>
    /// Where the messages sent to the model start: <paramref name="through"/>, moved back to the boundary (see
    /// <see cref="IsBoundary"/>) at or before it if it is not one (a transcript repaired after it was stored), so a call
    /// never loses its result.
    /// </summary>
    public static int StartOf(IReadOnlyList<ChatMessage> history, int through)
    {
        var start = Math.Clamp(through, 0, history.Count);
        while (!IsBoundary(history, start))
        {
            start--;
        }
        return start;
    }

    /// <summary>
    /// The next checkpoint: the user message that starts the last <paramref name="keepPrompts"/> prompts. When fewer
    /// prompts than that follow the current checkpoint, only the last turn stays verbatim: its prompt, or, when that turn
    /// has taken at least <paramref name="keepSteps"/> steps after its first, the step that starts its last
    /// <paramref name="keepSteps"/> (fewer when the checkpoint is already inside the turn and there are not that many).
    /// Null when the checkpoint cannot move forward.
    /// </summary>
    public static int? FindCheckpoint(IReadOnlyList<ChatMessage> history, int through, int keepPrompts, int keepSteps = KeepRecentSteps)
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
        keepPrompts = Math.Max(1, keepPrompts);
        if (prompts.Count >= keepPrompts)
        {
            return prompts[^keepPrompts];
        }

        var turn = prompts.Count > 0 ? prompts[^1] : start;
        var steps = new List<int>();
        for (var i = turn + 1; i < history.Count; i++)
        {
            if (history[i].Role == ChatRole.Assistant && IsBoundary(history, i))
            {
                steps.Add(i);
            }
        }
        keepSteps = Math.Max(1, keepSteps);
        if (steps.Count >= keepSteps)
        {
            return steps[^keepSteps];
        }
        if (prompts.Count > 0)
        {
            return prompts[^1];
        }
        return steps.Count > 0 ? steps[^1] : null;
    }

    /// <summary>
    /// The history messages sent after the system prompt: those from the checkpoint on. After a checkpoint inside a turn
    /// the turn's prompt goes first again, with <see cref="ResentNote"/>: Anthropic and many chat templates require the
    /// first message to be the user's, and the model should still see the request it is working on.
    /// </summary>
    public static List<ChatMessage> RequestTail(IReadOnlyList<ChatMessage> history, int through)
    {
        var start = StartOf(history, through);
        var tail = history.Skip(start).ToList();
        if (start > 0 && start < history.Count && history[start].Role != ChatRole.User)
        {
            var prompt = history.Take(start).LastOrDefault(m => m.Role == ChatRole.User);
            tail.Insert(0, prompt is null
                ? new ChatMessage(ChatRole.User, ResentNote)
                : prompt with { Content = $"{prompt.Content}\n\n{ResentNote}" });
        }
        return tail;
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
        var lastStep = LastStep(result);
        var used = 0;
        for (var i = result.Count - 1; i >= 0; i--)
        {
            var message = result[i];
            if (message.Role != ChatRole.Tool || (message.Content.Length <= ClipMinChars && !message.HasImages))
            {
                continue;
            }
            // A picture costs about TokensPerImage, counted in characters like the text.
            used += message.Content.Length + message.Images.Count * ImageChars;
            if (i > lastStep || used <= budgetChars)
            {
                continue;
            }
            result[i] = message with { Content = ClearedNote(message), Images = ChatMessage.NoImages };
        }
        return result;
    }

    /// <summary>
    /// A copy of <paramref name="messages"/> in which every write_file and edit_file call before the latest step whose
    /// result was a success keeps its path but not its text: the change is made, its result says so, and a few rewritten
    /// files would otherwise fill a small window on their own. The arguments stay valid JSON in the tool's own shape.
    /// </summary>
    public static List<ChatMessage> ShrinkOldEdits(IReadOnlyList<ChatMessage> messages)
    {
        var result = messages.ToList();
        var lastStep = LastStep(result);
        for (var i = 0; i < lastStep; i++)
        {
            var message = result[i];
            if (message.Role != ChatRole.Assistant || !message.HasToolCalls)
            {
                continue;
            }
            // Pair results with this step's calls only: some servers number calls per request, so ids repeat.
            var succeeded = new HashSet<string?>(StringComparer.Ordinal);
            for (var j = i + 1; j < result.Count && result[j].Role == ChatRole.Tool; j++)
            {
                if (!result[j].IsError)
                {
                    succeeded.Add(result[j].ToolCallId);
                }
            }
            var calls = message.ToolCalls.Select(c => succeeded.Contains(c.Id) ? ShrinkArguments(c) : c).ToList();
            if (!calls.SequenceEqual(message.ToolCalls))
            {
                result[i] = message with { ToolCalls = calls };
            }
        }
        return result;
    }

    /// <summary>The text arguments of a write_file or edit_file call replaced by their size; anything else unchanged.</summary>
    public static ToolCall ShrinkArguments(ToolCall call)
    {
        string[] fields = call.Name switch
        {
            CodeTools.WriteFile => ["content"],
            CodeTools.EditFile => ["old_string", "new_string"],
            _ => [],
        };
        if (fields.Length == 0 || call.Arguments.Length <= ClipMinChars)
        {
            return call;
        }
        JsonObject? args;
        try
        {
            args = JsonNode.Parse(call.Arguments) as JsonObject;
        }
        catch (JsonException)
        {
            return call;
        }
        if (args is null)
        {
            return call;
        }

        var changed = false;
        foreach (var field in fields)
        {
            if (args[field] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > ShrinkMinChars)
            {
                var lines = text.Count(c => c == '\n') + (text.EndsWith('\n') ? 0 : 1);
                args[field] = $"[{text.Length} characters, {lines} line{(lines == 1 ? "" : "s")}; left out of the context to save space, the change was made]";
                changed = true;
            }
        }
        return changed ? call with { Arguments = args.ToJsonString(ArgumentsJson) } : call;
    }

    /// <summary>The index of the last assistant message with tool calls (the latest step), or -1.</summary>
    private static int LastStep(IReadOnlyList<ChatMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == ChatRole.Assistant && messages[i].HasToolCalls)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>What one image counts for against the result budget: its token allowance in characters.</summary>
    private const int ImageChars = (int)(TokenEstimator.TokensPerImage * TokenEstimator.CharsPerToken);

    public static string ClearedNote(ChatMessage result)
    {
        if (result.HasImages)
        {
            return $"[This {result.ToolName ?? "tool"} result (a picture) was cleared from the context to save space. Call the tool again if you still need to see it.]";
        }
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
                    sb.Append("User: ").Append(message.Content.Trim()).Append(message.HasImages ? $" [{message.Images.Count} image(s) attached]" : "").Append("\n\n");
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
                    sb.Append(excerptChars == 0 ? $" [{message.Content.Length} characters]" : "\n" + HeadAndTail(message.Content.Trim(), excerptChars));
                    sb.Append(message.HasImages ? $" [{message.Images.Count} image(s)]" : "").Append("\n\n");
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
