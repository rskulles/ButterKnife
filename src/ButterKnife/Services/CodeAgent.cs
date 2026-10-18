using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace ButterKnife.Services;

/// <summary>How much a Code session may do without asking.</summary>
public enum CodePermissionMode
{
    /// <summary>Ask before every file change and every command.</summary>
    Ask,

    /// <summary>File changes go ahead; commands still ask.</summary>
    AcceptEdits,

    /// <summary>Read and plan only: the edit tools are not offered; commands still ask.</summary>
    Plan,

    /// <summary>Nothing asks. For a folder the user trusts the model with completely.</summary>
    Auto,
}

/// <summary>The user's answer to a permission prompt.</summary>
public enum PermissionChoice
{
    Allow,

    /// <summary>Allow this and, for the rest of the session, everything like it (all edits, or commands with the same prefix).</summary>
    AllowAlways,

    Deny,
}

public sealed record PermissionDecision(PermissionChoice Choice, string? Feedback = null);

/// <summary>
/// What the agent loop tells the page as it works. Everything is called from the loop's own flow, one call at a time;
/// the page marshals to its renderer.
/// </summary>
public interface ICodeAgentObserver
{
    /// <summary>A request to the model is about to go out (the start of each step).</summary>
    void OnStepStarted();

    /// <summary>A streamed item from the model: text, reasoning, usage or finish.</summary>
    void OnDelta(ChatDelta delta);

    /// <summary>The step's reply is complete (with any tool calls); persist it.</summary>
    Task OnAssistantMessageAsync(ChatMessage message, GenerationStats stats);

    /// <summary>A call needs the user's approval; awaited until they choose.</summary>
    Task<PermissionDecision> RequestPermissionAsync(ToolInvocation invocation, CancellationToken cancellationToken);

    void OnToolStarted(ToolInvocation invocation);

    /// <summary>Live output while a command runs (may be called from another thread).</summary>
    void OnToolOutput(ToolInvocation invocation, string chunk);

    /// <summary>The call finished (or was refused); persist the result message.</summary>
    Task OnToolFinishedAsync(ToolInvocation invocation, ToolResult result, ChatMessage resultMessage);

    /// <summary>The context is nearly full: earlier prompts or steps are being summarised before the next request.</summary>
    void OnCompacting();

    /// <summary>The summary is ready and already in use; persist the checkpoint.</summary>
    Task OnCompactedAsync(CodeCompaction compaction);

    /// <summary>Summarising failed; the turn carries on without it (old results are still cleared).</summary>
    void OnCompactionFailed(string error);
}

/// <summary>How a turn of the agent loop ended.</summary>
public enum CodeTurnEnd
{
    /// <summary>The model answered without calling tools.</summary>
    Done,

    /// <summary>The user refused a call; the model is not asked again until the user says something.</summary>
    Denied,

    /// <summary>The step limit was reached.</summary>
    StepLimit,
}

/// <summary>
/// The agent loop behind the Code tab: send the conversation with the tools, run the calls the model asks for
/// (asking the user first where the mode says so), append the results, and go again until the model answers without
/// calling a tool. The history list is updated in place, so the caller's copy is always what the model has seen.
/// </summary>
public sealed class CodeAgent(ILlmClient client, string model, CodeWorkspace workspace)
{
    /// <summary>A model stuck in a loop stops here; the user can say "continue".</summary>
    public const int MaxSteps = 40;

    public CodePermissionMode Mode { get; set; } = CodePermissionMode.Ask;

    /// <summary>Command prefixes the user allowed for this session ("always allow").</summary>
    public HashSet<string> AllowedCommands { get; } = new(StringComparer.Ordinal);

    public ChatOptions Options { get; set; } = ChatOptions.Default;

    /// <summary>The compaction checkpoint: a summary of the first <see cref="SummaryThrough"/> history messages, sent in their place.</summary>
    public string? Summary { get; set; }

    public int SummaryThrough { get; set; }

    /// <summary>The model's window in tokens, when known; auto-compaction needs it.</summary>
    public int? ContextWindow { get; set; }

    /// <summary>Compact before a request expected to use this share of the window (Llm:AutoCompactThreshold; 0 disables).</summary>
    public double AutoCompactThreshold { get; set; } = 0.8;

    /// <summary>User prompts kept verbatim after a compaction (only the last turn when there are not that many; see CodeCompactor.FindCheckpoint).</summary>
    public int KeepRecentPrompts { get; set; } = 2;

    /// <summary>
    /// The tokens the last request used (prompt + reply, as the server reported them) and how many history messages it
    /// covered; the next request's size is that plus an estimate of what was added since.
    /// </summary>
    public (int Tokens, int Messages)? LastUsage { get; set; }

    private bool _compactionFailed;

    public async Task<CodeTurnEnd> RunAsync(List<ChatMessage> history, ICodeAgentObserver observer, CancellationToken cancellationToken)
    {
        for (var step = 0; step < MaxSteps; step++)
        {
            var readOnly = Mode == CodePermissionMode.Plan;
            var tools = CodeTools.Definitions(readOnly);
            var request = await PrepareRequestAsync(history, readOnly, tools, observer, cancellationToken);

            observer.OnStepStarted();
            var text = new StringBuilder();
            var reasoning = new StringBuilder();
            var calls = new List<ToolCall>();
            var splitter = new ThinkTagSplitter();
            TokenUsage? usage = null;
            var clock = Stopwatch.StartNew();
            TimeSpan? firstToken = null;
            var chunks = 0;

            await foreach (var delta in client.StreamChatAsync(model, request, Options with { Tools = tools }, cancellationToken))
            {
                if (delta.Text is { Length: > 0 } || delta.Reasoning is { Length: > 0 } || delta.ToolCall is not null)
                {
                    firstToken ??= clock.Elapsed;
                    chunks++;
                }

                if (delta.Text is { Length: > 0 } raw)
                {
                    // Qwen-style models think inline in <think> tags; route that to reasoning like the chat page does.
                    foreach (var piece in splitter.Feed(raw))
                    {
                        Emit(piece, observer, text, reasoning);
                    }
                }
                else
                {
                    if (delta.Reasoning is { Length: > 0 } r)
                    {
                        reasoning.Append(r);
                    }
                    observer.OnDelta(delta);
                }

                if (delta.ToolCall is { } call)
                {
                    calls.Add(call);
                }
                usage = delta.Usage ?? usage;
            }
            if (splitter.Flush() is { } rest)
            {
                Emit(rest, observer, text, reasoning);
            }

            var reply = new ChatMessage(ChatRole.Assistant, text.ToString().Trim())
            {
                Reasoning = reasoning.Length > 0 ? reasoning.ToString() : null,
                ToolCalls = calls,
                Model = model,
            };
            history.Add(reply);
            if (usage is { PromptTokens: not null } or { CompletionTokens: not null })
            {
                LastUsage = ((usage.PromptTokens ?? 0) + (usage.CompletionTokens ?? 0), history.Count);
            }
            await observer.OnAssistantMessageAsync(reply, new GenerationStats(clock.Elapsed, firstToken, chunks, usage));

            if (calls.Count == 0)
            {
                return CodeTurnEnd.Done;
            }

            for (var i = 0; i < calls.Count; i++)
            {
                var invocation = CodeTools.Prepare(calls[i], workspace, readOnly);
                ToolResult result;
                if (invocation.Error is not null)
                {
                    result = ToolResult.Fail(invocation.Error);
                }
                else if (NeedsPermission(invocation))
                {
                    var decision = await observer.RequestPermissionAsync(invocation, cancellationToken);
                    if (decision.Choice == PermissionChoice.Deny)
                    {
                        var refusal = string.IsNullOrWhiteSpace(decision.Feedback)
                            ? "The user declined this action. Stop and wait for their next message."
                            : $"The user declined this action and said: {decision.Feedback.Trim()}";
                        await FinishAsync(invocation, ToolResult.Fail(refusal), history, observer);
                        // Every call needs an answer before the model is asked anything again.
                        for (var j = i + 1; j < calls.Count; j++)
                        {
                            await FinishAsync(CodeTools.Prepare(calls[j], workspace, readOnly), ToolResult.Fail("Not run: the user declined an earlier action."), history, observer);
                        }
                        return CodeTurnEnd.Denied;
                    }
                    if (decision.Choice == PermissionChoice.AllowAlways)
                    {
                        Remember(invocation);
                    }
                    result = await ExecuteAsync(invocation, observer, cancellationToken);
                }
                else
                {
                    result = await ExecuteAsync(invocation, observer, cancellationToken);
                }
                await FinishAsync(invocation, result, history, observer);
            }
        }
        return CodeTurnEnd.StepLimit;
    }

    /// <summary>
    /// The messages for the next step: the system prompt (with the summary, if any), then the history after the
    /// checkpoint with old tool results cleared and old edits shrunk. When that would pass the threshold, earlier prompts
    /// (or, in one long turn, its earlier steps) are compacted first, and if it still would, every result but the latest
    /// step's is cleared.
    /// </summary>
    private async Task<List<ChatMessage>> PrepareRequestAsync(List<ChatMessage> history, bool readOnly, IReadOnlyList<ToolDefinition> tools, ICodeAgentObserver observer, CancellationToken cancellationToken)
    {
        var request = BuildRequest(history, readOnly, CodeCompactor.ResultBudget(ContextWindow));
        if (!OverThreshold(request, tools, history))
        {
            return request;
        }

        if (!_compactionFailed && CodeCompactor.FindCheckpoint(history, SummaryThrough, KeepRecentPrompts) is not null)
        {
            observer.OnCompacting();
            try
            {
                if (await CodeCompactor.CompactAsync(client, model, history, Summary, SummaryThrough, KeepRecentPrompts, ContextWindow, cancellationToken) is { } compaction)
                {
                    Summary = compaction.Summary;
                    SummaryThrough = compaction.Through;
                    LastUsage = null; // it described the uncompacted history
                    await observer.OnCompactedAsync(compaction);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Not worth failing the turn over, nor retrying every step; clearing old results still helps.
                _compactionFailed = true;
                observer.OnCompactionFailed(ex.Message);
            }
            request = BuildRequest(history, readOnly, CodeCompactor.ResultBudget(ContextWindow));
        }

        return OverThreshold(request, tools, history) ? BuildRequest(history, readOnly, 0) : request;
    }

    private List<ChatMessage> BuildRequest(List<ChatMessage> history, bool readOnly, int resultBudget)
    {
        var tail = CodeCompactor.RequestTail(history, SummaryThrough);
        return [new(ChatRole.System, CodePrompt.Compose(workspace, readOnly, Summary)), .. CodeCompactor.ShrinkOldEdits(CodeCompactor.ClipOldResults(tail, resultBudget))];
    }

    /// <summary>Whether a request is expected to pass the threshold: the larger of the character estimate and the last reported count plus what was added since.</summary>
    private bool OverThreshold(List<ChatMessage> request, IReadOnlyList<ToolDefinition> tools, List<ChatMessage> history)
    {
        if (AutoCompactThreshold <= 0 || ContextWindow is not { } window || window <= 0)
        {
            return false;
        }
        var estimate = TokenEstimator.Estimate(request) + tools.Sum(t => TokenEstimator.Estimate(t.Name + t.Description + t.Parameters.GetRawText()));
        if (LastUsage is { } last && last.Messages <= history.Count)
        {
            estimate = Math.Max(estimate, last.Tokens + TokenEstimator.Estimate(history.Skip(last.Messages)));
        }
        return estimate >= window * AutoCompactThreshold;
    }

    private static void Emit(ThinkTagSplitter.Piece piece, ICodeAgentObserver observer, StringBuilder text, StringBuilder reasoning)
    {
        if (piece.Reasoning is { Length: > 0 } r)
        {
            reasoning.Append(r);
            observer.OnDelta(ChatDelta.FromReasoning(r));
        }
        if (piece.Text is { Length: > 0 } t)
        {
            text.Append(t);
            observer.OnDelta(ChatDelta.FromText(t));
        }
    }

    public bool NeedsPermission(ToolInvocation invocation) => invocation.Permission switch
    {
        ToolPermission.None => false,
        _ when Mode == CodePermissionMode.Auto => false,
        ToolPermission.Edit => Mode != CodePermissionMode.AcceptEdits,
        ToolPermission.Command => !CodeTools.IsCoveredBy(invocation.Command ?? "", AllowedCommands),
        _ => true,
    };

    /// <summary>"Always" for an edit switches the session to accepting edits; for a command it remembers its prefix.</summary>
    private void Remember(ToolInvocation invocation)
    {
        if (invocation.Permission == ToolPermission.Edit && Mode == CodePermissionMode.Ask)
        {
            Mode = CodePermissionMode.AcceptEdits;
        }
        else if (invocation.Permission == ToolPermission.Command && invocation.Command is { } command)
        {
            AllowedCommands.Add(CodeTools.CommandPrefix(command));
        }
    }

    private static async Task<ToolResult> ExecuteAsync(ToolInvocation invocation, ICodeAgentObserver observer, CancellationToken cancellationToken)
    {
        observer.OnToolStarted(invocation);
        try
        {
            return await invocation.RunAsync(chunk => observer.OnToolOutput(invocation, chunk), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CodeToolException ex)
        {
            return ToolResult.Fail(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return ToolResult.Fail($"{invocation.Call.Name} failed: {ex.Message}");
        }
    }

    private static async Task FinishAsync(ToolInvocation invocation, ToolResult result, List<ChatMessage> history, ICodeAgentObserver observer)
    {
        var message = ResultMessage(invocation.Call, result);
        history.Add(message);
        await observer.OnToolFinishedAsync(invocation, result, message);
    }

    public static ChatMessage ResultMessage(ToolCall call, ToolResult result) =>
        new(ChatRole.Tool, result.Output) { ToolCallId = call.Id, ToolName = call.Name, IsError = result.IsError };

    /// <summary>
    /// Makes a stored history safe to send: every tool call gets an answer (a turn that was stopped mid-way leaves calls
    /// without results, which OpenAI-style servers and Anthropic reject), and stray results without a call are dropped.
    /// </summary>
    public static List<ChatMessage> Repair(IEnumerable<ChatMessage> messages)
    {
        var result = new List<ChatMessage>();
        var open = new List<ToolCall>();
        foreach (var message in messages)
        {
            if (message.Role == ChatRole.Tool)
            {
                var call = open.FirstOrDefault(c => c.Id == message.ToolCallId);
                if (call is null)
                {
                    continue;
                }
                open.Remove(call);
                result.Add(message);
                continue;
            }

            CloseOpen(result, open);
            result.Add(message);
            if (message.HasToolCalls)
            {
                open.AddRange(message.ToolCalls);
            }
        }
        CloseOpen(result, open);
        return result;
    }

    private static void CloseOpen(List<ChatMessage> result, List<ToolCall> open)
    {
        foreach (var call in open)
        {
            result.Add(ResultMessage(call, ToolResult.Fail("Not run: the user stopped the turn before this call.")));
        }
        open.Clear();
    }
}

/// <summary>The Code tab's system prompt: how to work, the environment, and the project's own instructions if it has any.</summary>
public static class CodePrompt
{
    /// <summary>Instruction files read from the project folder, first found wins (AGENTS.md is the cross-tool convention).</summary>
    public static readonly string[] InstructionFiles = ["AGENTS.md", "CLAUDE.md", ".github/copilot-instructions.md"];

    public const int MaxInstructionChars = 20_000;

    public static string Compose(CodeWorkspace workspace, bool readOnly, string? summary = null)
    {
        var sb = new StringBuilder();
        sb.Append("""
            You are ButterKnife Code, a coding agent working in the user's project folder through tools. You read code,
            search it, edit files and run commands to complete the user's request.

            How to work:
            - Explore before changing anything: use list_files and grep to find the relevant code, then read_file it.
            - Make small, targeted changes with edit_file. old_string must be copied exactly from read_file output
              (without the line-number prefix), with enough context to be unique. Use write_file only for new files or
              complete rewrites.
            - After changing code, build or run the tests with run_command when the project has them, and fix what fails.
            - Paths are relative to the project folder. Do not try to work outside it.
            - Call tools rather than describing what you would do. When several independent calls are needed, make them together.
            - Keep replies short. When you are done, say briefly what you changed and anything the user should check.
            - If a tool reports an error, read it and correct the call; do not repeat the same failing call.
            - If the user declines an action, stop and wait for their next message.

            """);

        if (readOnly)
        {
            sb.Append("""
                Plan mode is on: you cannot change files. Investigate with the read-only tools (commands need the user's
                approval), then reply with a concrete step-by-step plan: which files change and how. Do not claim to have
                made changes.

                """);
        }

        sb.Append("Environment:\n");
        sb.Append("- Project folder: ").Append(workspace.Root).Append('\n');
        sb.Append("- Git repository: ").Append(workspace.IsGitRepository ? "yes" : "no").Append('\n');
        sb.Append("- Operating system: ").Append(RuntimeInformation.OSDescription).Append('\n');
        sb.Append("- Shell for run_command: ").Append(ShellCommand.ShellName).Append('\n');
        sb.Append("- Today's date: ").Append(DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('\n');

        if (ProjectInstructions(workspace) is { } instructions)
        {
            sb.Append("\nThe project's own instructions (from ").Append(instructions.File).Append("), which take precedence over the guidance above:\n\n")
                .Append(instructions.Text).Append('\n');
        }

        if (CodeCommands.SkillsSection(CodeCommands.DiscoverSkills(workspace)) is { } skills)
        {
            sb.Append('\n').Append(skills);
        }

        if (!string.IsNullOrWhiteSpace(summary))
        {
            sb.Append('\n').Append(CodeCompactor.SummarySection(summary)).Append('\n');
        }
        return sb.ToString();
    }

    public static (string File, string Text)? ProjectInstructions(CodeWorkspace workspace)
    {
        foreach (var name in InstructionFiles)
        {
            var path = Path.Combine(workspace.Root, name);
            if (!File.Exists(path))
            {
                continue;
            }
            try
            {
                var text = File.ReadAllText(path).Trim();
                if (text.Length == 0)
                {
                    continue;
                }
                if (text.Length > MaxInstructionChars)
                {
                    text = text[..MaxInstructionChars] + "\n[... the rest of the file was left out]";
                }
                return (name, text);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
        }
        return null;
    }
}
