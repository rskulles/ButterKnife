using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using ButterKnife.Data;

namespace ButterKnife.Services;

/// <summary>
/// What a page shows of a running Code turn. A run calls these in order, from its own thread (and, when a page
/// attaches, once more for everything that already happened); implementations only update their view state and
/// marshal to their renderer. Nothing here saves anything: the run has already done that.
/// </summary>
public interface ICodeRunView
{
    void StepStarted(string model, long startedAt);
    void Delta(ChatDelta delta);
    void AssistantMessage(ChatMessage message, GenerationStats stats);
    void PermissionRequested(ToolInvocation invocation);
    void PermissionAnswered(string callId, PermissionChoice choice);
    void ToolStarted(ToolInvocation invocation, CodePermissionMode mode);
    void ToolOutput(string callId, string chunk);
    void ToolFinished(ToolInvocation invocation, ToolResult result, ChatMessage message);
    void Compacting();
    void Compacted(CodeCompaction compaction);
    void CompactionFailed(string error);

    /// <summary>Stop or a failure mid-step: the partial reply that was kept (if any) and the results given to calls that never ran.</summary>
    void Interrupted(ChatMessage? partial, IReadOnlyList<ChatMessage> closed);

    void Error(string message);
    void Ended(CodeRunResult result);
}

/// <summary>How a turn ended and the session state it leaves behind (the run has stored all of it).</summary>
public sealed record CodeRunResult(
    CodeTurnEnd? End,
    string? Error,
    CodePermissionMode Mode,
    string? Summary,
    int SummaryThrough,
    int? ContextTokens,
    IReadOnlyList<ChatMessage> History);

/// <summary>Everything a run needs: the agent the page configured, the history it works on, and what to store where.</summary>
public sealed record CodeRunStart(
    Guid SessionId,
    Guid UserId,
    long PromptMessageId,
    CodeAgent Agent,
    List<ChatMessage> History,
    Guid ConnectionId,
    string Model,
    string Prompt,
    bool SuggestTitle,
    int? ContextWindow,
    int? ContextTokens);

/// <summary>
/// One Code turn, running on the server independently of any page: the agent loop, the saving of every message, the
/// permission prompts (answered by whichever page is showing the session), and a record of what happened so a page
/// that opens the session mid-turn can catch up. Leaving or reloading the page detaches it; only <see cref="Stop"/>
/// cancels. Owned by <see cref="CodeRunRegistry"/>, which drops it when it ends.
/// </summary>
public sealed class CodeRun : ICodeAgentObserver
{
    /// <summary>Live command output kept for replay per call (the tail); the full output is in the stored result.</summary>
    private const int MaxReplayOutputChars = 60_000;

    private readonly CodeRunStart _start;
    private readonly CodeRunRegistry _registry;
    private readonly ICodeSessionStore _store;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly List<object> _log = [];
    private readonly List<ICodeRunView> _views = [];
    private readonly Dictionary<string, TaskCompletionSource<PermissionDecision>> _pending = new(StringComparer.Ordinal);
    private readonly StringBuilder _text = new();
    private readonly StringBuilder _reasoning = new();
    private readonly CodePermissionMode _initialMode;
    private bool _streaming;
    private int? _contextTokens;
    private Task _completion = Task.CompletedTask;

    internal CodeRun(CodeRunStart start, CodeRunRegistry registry, ICodeSessionStore store)
    {
        _start = start;
        _registry = registry;
        _store = store;
        _initialMode = start.Agent.Mode;
        _contextTokens = start.ContextTokens;
    }

    public Guid SessionId => _start.SessionId;
    public Guid UserId => _start.UserId;

    /// <summary>The stored id of the prompt that started this turn: a page shows stored messages up to it, then replays the run.</summary>
    public long PromptMessageId => _start.PromptMessageId;

    public bool IsRunning => !_completion.IsCompleted;

    public Task Completion => _completion;

    internal void Begin() => _completion = Task.Run(async () =>
    {
        try
        {
            await RunAsync();
        }
        catch (Exception ex)
        {
            _registry.Failed(this, ex);
            _registry.Finished(this);
        }
    });

    /// <summary>Cancels the turn; the partial reply is kept and unanswered calls get a result (see <see cref="Interrupted"/>).</summary>
    public void Stop() => _cts.Cancel();

    /// <summary>A mode change in the page applies to the calls still to come in this turn.</summary>
    public void SetMode(CodePermissionMode mode) => _start.Agent.Mode = mode;

    /// <summary>Answers a waiting permission prompt (from any page showing the session). False when nothing was waiting.</summary>
    public bool Answer(string callId, PermissionDecision decision)
    {
        TaskCompletionSource<PermissionDecision>? pending;
        lock (_gate)
        {
            if (!_pending.Remove(callId, out pending))
            {
                return false;
            }
        }
        pending.TrySetResult(decision);
        Emit(v => v.PermissionAnswered(callId, decision.Choice));
        return true;
    }

    /// <summary>
    /// Shows the turn in a page: everything so far is replayed into <paramref name="view"/> at once (coalesced), then it
    /// follows live. Dispose the result to detach; the run carries on.
    /// </summary>
    public IDisposable Attach(ICodeRunView view)
    {
        lock (_gate)
        {
            foreach (var entry in _log.ToArray())
            {
                Replay(entry, view);
            }
            _views.Add(view);
        }
        return new Detacher(this, view);
    }

    private sealed class Detacher(CodeRun run, ICodeRunView view) : IDisposable
    {
        public void Dispose()
        {
            lock (run._gate)
            {
                run._views.Remove(view);
            }
        }
    }

    // ---------------------------------------------------------------- the turn

    private async Task RunAsync()
    {
        CodeTurnEnd? end = null;
        string? error = null;
        try
        {
            end = await _start.Agent.RunAsync(_start.History, this, _cts.Token);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            await SaveInterruptedAsync();
        }
        catch (Exception ex)
        {
            error = ex is KeyNotFoundException ? "That model's connection no longer exists; pick another model." : ex.Message;
            await SaveInterruptedAsync();
        }

        var agent = _start.Agent;
        if (agent.Mode != _initialMode)
        {
            await SaveQuietlyAsync(() => _store.SetModeAsync(SessionId, agent.Mode, CancellationToken.None));
        }
        await SaveQuietlyAsync(() => _store.SetContextAsync(SessionId, _contextTokens, _start.ContextWindow, CancellationToken.None));

        CodeRunResult result;
        lock (_gate)
        {
            result = new CodeRunResult(end, error, agent.Mode, agent.Summary, agent.SummaryThrough, _contextTokens, _start.History.ToArray());
        }
        Emit(v => v.Ended(result));
        _registry.Finished(this);

        if (end == CodeTurnEnd.Done && _start.SuggestTitle)
        {
            var reply = result.History.LastOrDefault(m => m.Role == ChatRole.Assistant)?.Content ?? "";
            await _registry.SuggestTitleAsync(SessionId, UserId, _start.ConnectionId, _start.Model, _start.Prompt, reply);
        }
    }

    /// <summary>Keeps the partial reply and answers every call left without a result, so the next request is valid.</summary>
    private async Task SaveInterruptedAsync()
    {
        ChatMessage? partial = null;
        if (_streaming && _text.Length > 0)
        {
            partial = new ChatMessage(ChatRole.Assistant, _text.ToString().Trim())
            {
                Reasoning = _reasoning.Length > 0 ? _reasoning.ToString() : null,
                Model = _start.Model,
            };
            lock (_gate)
            {
                _start.History.Add(partial);
            }
            await SaveQuietlyAsync(() => _store.AppendAsync(SessionId, partial, cancellationToken: CancellationToken.None));
        }
        _streaming = false;

        List<ChatMessage> closed;
        lock (_gate)
        {
            var repaired = CodeAgent.Repair(_start.History);
            closed = repaired.Where(m => !_start.History.Any(h => ReferenceEquals(h, m))).ToList();
            _start.History.Clear();
            _start.History.AddRange(repaired);
        }
        foreach (var message in closed)
        {
            await SaveQuietlyAsync(() => _store.AppendAsync(SessionId, message, cancellationToken: CancellationToken.None));
        }
        Emit(v => v.Interrupted(partial, closed));
    }

    private async Task SaveQuietlyAsync(Func<Task> save)
    {
        try
        {
            await save();
        }
        catch (Exception ex)
        {
            Emit(v => v.Error($"Could not save: {ex.Message}"));
        }
    }

    // ---------------------------------------------------------------- the agent's callbacks

    public void OnStepStarted()
    {
        _text.Clear();
        _reasoning.Clear();
        _streaming = true;
        var startedAt = Stopwatch.GetTimestamp();
        Emit(v => v.StepStarted(_start.Model, startedAt));
    }

    public void OnDelta(ChatDelta delta)
    {
        _text.Append(delta.Text);
        _reasoning.Append(delta.Reasoning);
        if (delta.Usage is { } usage && (usage.PromptTokens is not null || usage.CompletionTokens is not null))
        {
            _contextTokens = (usage.PromptTokens ?? 0) + (usage.CompletionTokens ?? 0);
        }

        lock (_gate)
        {
            if (_log.Count == 0 || _log[^1] is not DeltaEntry entry)
            {
                entry = new DeltaEntry();
                _log.Add(entry);
            }
            entry.Text.Append(delta.Text);
            entry.Reasoning.Append(delta.Reasoning);
            entry.Usage = delta.Usage ?? entry.Usage;
            Dispatch(v => v.Delta(delta));
        }
    }

    public async Task OnAssistantMessageAsync(ChatMessage message, GenerationStats stats)
    {
        _streaming = false;
        await SaveQuietlyAsync(() => _store.AppendAsync(SessionId, message with { Stats = stats }, cancellationToken: CancellationToken.None));
        Emit(v => v.AssistantMessage(message, stats));
    }

    public async Task<PermissionDecision> RequestPermissionAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var pending = new TaskCompletionSource<PermissionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _pending[invocation.Call.Id] = pending;
        }
        Emit(v => v.PermissionRequested(invocation));
        try
        {
            return await pending.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            lock (_gate)
            {
                _pending.Remove(invocation.Call.Id);
            }
        }
    }

    public void OnToolStarted(ToolInvocation invocation)
    {
        var mode = _start.Agent.Mode; // "Allow all edits" may have just switched it
        Emit(v => v.ToolStarted(invocation, mode));
    }

    public void OnToolOutput(ToolInvocation invocation, string chunk)
    {
        var callId = invocation.Call.Id;
        lock (_gate)
        {
            if (_log.Count == 0 || _log[^1] is not OutputEntry entry || entry.CallId != callId)
            {
                entry = new OutputEntry(callId);
                _log.Add(entry);
            }
            entry.Output.Append(chunk);
            if (entry.Output.Length > MaxReplayOutputChars)
            {
                entry.Output.Remove(0, entry.Output.Length - MaxReplayOutputChars);
            }
            Dispatch(v => v.ToolOutput(callId, chunk));
        }
    }

    public async Task OnToolFinishedAsync(ToolInvocation invocation, ToolResult result, ChatMessage resultMessage)
    {
        await SaveQuietlyAsync(() => _store.AppendAsync(SessionId, resultMessage, result.Diff, cancellationToken: CancellationToken.None));
        Emit(v => v.ToolFinished(invocation, result, resultMessage));
    }

    public void OnCompacting() => Emit(v => v.Compacting());

    public async Task OnCompactedAsync(CodeCompaction compaction)
    {
        _contextTokens = null; // it described the uncompacted history
        await SaveQuietlyAsync(() => _store.SetSummaryAsync(SessionId, compaction.Summary, compaction.Through, CancellationToken.None));
        await SaveQuietlyAsync(() => _store.SetContextAsync(SessionId, null, _start.ContextWindow, CancellationToken.None));
        Emit(v => v.Compacted(compaction));
    }

    public void OnCompactionFailed(string error) => Emit(v => v.CompactionFailed(error));

    // ---------------------------------------------------------------- the record and the views

    private sealed class DeltaEntry
    {
        public StringBuilder Text { get; } = new();
        public StringBuilder Reasoning { get; } = new();
        public TokenUsage? Usage { get; set; }
    }

    private sealed class OutputEntry(string callId)
    {
        public string CallId { get; } = callId;
        public StringBuilder Output { get; } = new();
    }

    private void Emit(Action<ICodeRunView> action)
    {
        lock (_gate)
        {
            _log.Add(action);
            Dispatch(action);
        }
    }

    /// <summary>Called under the lock, so a page attaching at the same moment sees each event exactly once.</summary>
    private void Dispatch(Action<ICodeRunView> action)
    {
        // A copy: Blazor may run a page's handler right here, and a page that detaches in it changes the list.
        foreach (var view in _views.ToArray())
        {
            try
            {
                action(view);
            }
            catch (Exception)
            {
                // A page that has gone away must not stop the turn.
            }
        }
    }

    private static void Replay(object entry, ICodeRunView view)
    {
        try
        {
            switch (entry)
            {
                case DeltaEntry delta:
                    if (delta.Reasoning.Length > 0)
                    {
                        view.Delta(ChatDelta.FromReasoning(delta.Reasoning.ToString()));
                    }
                    if (delta.Text.Length > 0)
                    {
                        view.Delta(ChatDelta.FromText(delta.Text.ToString()));
                    }
                    if (delta.Usage is { } usage)
                    {
                        view.Delta(ChatDelta.FromUsage(usage));
                    }
                    break;
                case OutputEntry output:
                    view.ToolOutput(output.CallId, output.Output.ToString());
                    break;
                case Action<ICodeRunView> action:
                    action(view);
                    break;
            }
        }
        catch (Exception)
        {
        }
    }
}

/// <summary>
/// The Code turns running on this server, one per session. A page starts one, attaches to whatever is running when it
/// opens a session (only its owner's), and the run removes itself when it ends. Host shutdown disposes this singleton,
/// which stops every run (each keeps what it had, as with Stop).
/// </summary>
public sealed class CodeRunRegistry(ICodeSessionStore store, CodeSessionEvents events, ChatTitler titler, ILogger<CodeRunRegistry> logger) : IDisposable
{
    private readonly ConcurrentDictionary<Guid, CodeRun> _runs = new();

    /// <summary>The session's running turn, for its owner only.</summary>
    public CodeRun? Find(Guid sessionId, Guid userId) =>
        _runs.TryGetValue(sessionId, out var run) && run.UserId == userId ? run : null;

    /// <summary>Starts a turn. Throws when the session already has one running.</summary>
    public CodeRun Start(CodeRunStart start)
    {
        var run = new CodeRun(start, this, store);
        if (!_runs.TryAdd(start.SessionId, run))
        {
            throw new InvalidOperationException("This session is already working on something; wait for it or stop it first.");
        }
        run.Begin();
        return run;
    }

    /// <summary>Stops the session's turn, if one is running, and waits briefly for it to save what it had (before deleting the session).</summary>
    public async Task StopAsync(Guid sessionId)
    {
        if (_runs.TryGetValue(sessionId, out var run))
        {
            run.Stop();
            await Task.WhenAny(run.Completion, Task.Delay(TimeSpan.FromSeconds(5)));
        }
    }

    internal void Finished(CodeRun run)
    {
        _runs.TryRemove(new KeyValuePair<Guid, CodeRun>(run.SessionId, run));
        Notify(); // the sidebar orders sessions by activity
    }

    /// <summary>Change notifications run other circuits' handlers on this thread; one of them failing must not end the run early.</summary>
    private void Notify()
    {
        try
        {
            events.NotifyChanged();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "A Code session change handler failed");
        }
    }

    internal void Failed(CodeRun run, Exception ex) => logger.LogError(ex, "The turn in Code session {Session} failed", run.SessionId);

    /// <summary>After the first finished turn: the model's title replaces the placeholder, unless the user renamed the session meanwhile.</summary>
    internal async Task SuggestTitleAsync(Guid sessionId, Guid userId, Guid connectionId, string model, string prompt, string reply)
    {
        try
        {
            var placeholder = (await store.ListAsync(userId)).FirstOrDefault(s => s.Id == sessionId)?.Title;
            var title = await titler.SuggestAsync(connectionId, model, prompt, reply, CancellationToken.None);
            if (title is null || placeholder is null)
            {
                return;
            }
            var current = (await store.ListAsync(userId)).FirstOrDefault(s => s.Id == sessionId);
            if (current is null || current.Title != placeholder)
            {
                return;
            }
            await store.SetTitleAsync(sessionId, title);
            Notify();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not title Code session {Session}", sessionId);
            // A title is cosmetic; the placeholder stays.
        }
    }

    public void Dispose()
    {
        foreach (var run in _runs.Values)
        {
            run.Stop();
        }
        Task.WaitAll(_runs.Values.Select(r => r.Completion).ToArray(), TimeSpan.FromSeconds(5));
    }
}
