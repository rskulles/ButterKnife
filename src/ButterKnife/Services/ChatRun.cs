using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using ButterKnife.Data;

namespace ButterKnife.Services;

/// <summary>What a chat page shows of a streaming reply: deltas (inline thinking already split out) and the end.</summary>
public interface IChatRunView
{
    /// <summary>A piece of the reply, or of the compare-mode answer when <paramref name="alternative"/> is true.</summary>
    void Delta(bool alternative, ChatDelta delta);

    void Ended(ChatRunResult result);
}

/// <summary>How a reply ended. The reply is already stored (MessageId) unless nothing streamed.</summary>
public sealed record ChatRunResult(
    long? MessageId,
    bool Stopped,
    string? Error,
    string? AlternativeError,
    int? ContextTokens,
    GenerationStats Stats,
    GenerationStats? AlternativeStats);

/// <summary>
/// One reply to stream. For a continuation, <see cref="ContinueMessageId"/> is the stored reply being extended and
/// <see cref="ContinueText"/>/<see cref="ContinueReasoning"/> what it held, so the saved row is the whole reply.
/// </summary>
public sealed record ChatRunStart(
    Guid ConversationId,
    Guid UserId,
    ILlmClient Client,
    string Model,
    IReadOnlyList<ChatMessage> History,
    ChatOptions Options,
    int? ContextWindow,
    string Prompt,
    bool SuggestTitle,
    long? ContinueMessageId = null,
    string? ContinueText = null,
    string? ContinueReasoning = null,
    ILlmClient? AlternativeClient = null,
    string? AlternativeModel = null);

/// <summary>
/// A chat reply streaming on the server independently of any page (the counterpart of CodeRun): it saves the reply
/// when it ends, however it ends, and a page that opens the conversation meanwhile replays what has streamed so far and
/// follows the rest. Leaving or reloading the page detaches it; only <see cref="Stop"/> cancels.
/// </summary>
public sealed class ChatRun
{
    private readonly ChatRunStart _start;
    private readonly ChatRunRegistry _registry;
    private readonly IConversationStore _store;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly List<object> _log = [];
    private readonly List<IChatRunView> _views = [];
    private Task _completion = Task.CompletedTask;

    internal ChatRun(ChatRunStart start, ChatRunRegistry registry, IConversationStore store)
    {
        _start = start;
        _registry = registry;
        _store = store;
    }

    public Guid ConversationId => _start.ConversationId;
    public Guid UserId => _start.UserId;
    public string Model => _start.Model;
    public string? AlternativeModel => _start.AlternativeModel;
    public long? ContinueMessageId => _start.ContinueMessageId;
    public bool IsRunning => !_completion.IsCompleted;
    public Task Completion => _completion;

    /// <summary>When the reply started streaming (Stopwatch ticks), so a page that attaches late times it from the start.</summary>
    public long StartedAt { get; } = Stopwatch.GetTimestamp();

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

    public void Stop() => _cts.Cancel();

    public IDisposable Attach(IChatRunView view)
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

    private sealed class Detacher(ChatRun run, IChatRunView view) : IDisposable
    {
        public void Dispose()
        {
            lock (run._gate)
            {
                run._views.Remove(view);
            }
        }
    }

    // ---------------------------------------------------------------- streaming

    private sealed class Output(bool alternative)
    {
        public bool Alternative { get; } = alternative;
        public StringBuilder Text { get; } = new();
        public StringBuilder Reasoning { get; } = new();
        public TokenUsage? Usage { get; set; }
        public int Chunks { get; set; }
        public long? FirstTokenAt { get; set; }
        public long? EndedAt { get; set; }

        public GenerationStats Stats(long startedAt) => new(
            Stopwatch.GetElapsedTime(startedAt, EndedAt ?? Stopwatch.GetTimestamp()),
            FirstTokenAt is { } first ? Stopwatch.GetElapsedTime(startedAt, first) : null,
            Chunks,
            Usage);
    }

    private async Task RunAsync()
    {
        var reply = new Output(alternative: false);
        Output? shadow = null;
        Task? alternative = null;
        if (_start.AlternativeClient is { } altClient && _start.AlternativeModel is { } altModel)
        {
            shadow = new Output(alternative: true);
            alternative = Task.Run(() => StreamAsync(shadow, altClient, altModel));
        }

        var stopped = false;
        string? error = null;
        try
        {
            await StreamAsync(reply, _start.Client, _start.Model);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            stopped = true; // keep whatever streamed
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        reply.EndedAt = Stopwatch.GetTimestamp();

        string? alternativeError = null;
        if (alternative is not null)
        {
            try
            {
                await alternative; // Stop cancels it through the same token
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                alternativeError = ex.Message;
            }
            shadow!.EndedAt ??= Stopwatch.GetTimestamp();
        }

        var stats = reply.Stats(StartedAt);
        int? contextTokens = reply.Usage?.PromptTokens is { } prompt ? prompt + (reply.Usage.CompletionTokens ?? reply.Chunks) : null;
        long? messageId = null;
        var text = (_start.ContinueText ?? "") + reply.Text;
        if (text.Length > 0)
        {
            var reasoning = (_start.ContinueReasoning ?? "") + reply.Reasoning;
            try
            {
                if (_start.ContinueMessageId is { } existing)
                {
                    await _store.SetMessageContentAsync(ConversationId, existing, text, reasoning.Length > 0 ? reasoning : null, _start.Model, stats, CancellationToken.None);
                    messageId = existing;
                }
                else
                {
                    messageId = await _store.AppendMessageAsync(ConversationId, new ChatMessage(ChatRole.Assistant, text)
                    {
                        Reasoning = reasoning.Length > 0 ? reasoning : null,
                        Model = _start.Model,
                        Stats = stats,
                    }, CancellationToken.None);
                }
                if (contextTokens is not null)
                {
                    await _store.SetContextUsageAsync(ConversationId, contextTokens, _start.ContextWindow, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                error ??= $"Reply was not saved: {ex.Message}";
            }
        }

        var result = new ChatRunResult(messageId, stopped, error, alternativeError, contextTokens, stats, shadow?.Stats(StartedAt));
        Emit(v => v.Ended(result));
        _registry.Finished(this);

        if (_start.SuggestTitle && _start.ContinueMessageId is null && !stopped && error is null && reply.Text.Length > 0)
        {
            await _registry.SuggestTitleAsync(ConversationId, UserId, _start.Client.ConnectionId, _start.Model, _start.Prompt, reply.Text.ToString());
        }
    }

    private async Task StreamAsync(Output stream, ILlmClient client, string model)
    {
        var splitter = new ThinkTagSplitter(); // models that think inline as <think>…</think>
        await foreach (var delta in client.StreamChatAsync(model, _start.History, _start.Options, _cts.Token))
        {
            if (delta.Usage is { } usage)
            {
                stream.Usage = usage;
                Push(stream, ChatDelta.FromUsage(usage));
            }
            if (delta.Finish is { } finish)
            {
                Push(stream, ChatDelta.FromFinish(finish));
            }
            if (delta.Reasoning is { Length: > 0 } reasoning)
            {
                Push(stream, ChatDelta.FromReasoning(reasoning));
            }
            if (delta.Text is { } text)
            {
                foreach (var piece in splitter.Feed(text))
                {
                    if (piece.Reasoning is { Length: > 0 } r)
                    {
                        Push(stream, ChatDelta.FromReasoning(r));
                    }
                    if (piece.Text is { Length: > 0 } t)
                    {
                        Push(stream, ChatDelta.FromText(t));
                    }
                }
            }
        }
        if (splitter.Flush() is { } tail)
        {
            if (tail.Reasoning is { Length: > 0 } r)
            {
                Push(stream, ChatDelta.FromReasoning(r));
            }
            if (tail.Text is { Length: > 0 } t)
            {
                Push(stream, ChatDelta.FromText(t));
            }
        }
    }

    // ---------------------------------------------------------------- the record and the views

    private sealed class DeltaEntry(bool alternative)
    {
        public bool Alternative { get; } = alternative;
        public StringBuilder Text { get; } = new();
        public StringBuilder Reasoning { get; } = new();
        public TokenUsage? Usage { get; set; }
        public FinishReason? Finish { get; set; }
    }

    private void Push(Output stream, ChatDelta delta)
    {
        if (delta.Text is { Length: > 0 } || delta.Reasoning is { Length: > 0 })
        {
            stream.FirstTokenAt ??= Stopwatch.GetTimestamp();
            stream.Chunks++;
        }
        lock (_gate)
        {
            stream.Text.Append(delta.Text);
            stream.Reasoning.Append(delta.Reasoning);
            // Coalesce into the latest entry for this stream, so a late page replays a few large pieces.
            if (_log.Count == 0 || _log[^1] is not DeltaEntry entry || entry.Alternative != stream.Alternative)
            {
                entry = new DeltaEntry(stream.Alternative);
                _log.Add(entry);
            }
            entry.Text.Append(delta.Text);
            entry.Reasoning.Append(delta.Reasoning);
            entry.Usage = delta.Usage ?? entry.Usage;
            entry.Finish = delta.Finish ?? entry.Finish;
            Dispatch(v => v.Delta(stream.Alternative, delta));
        }
    }

    private void Emit(Action<IChatRunView> action)
    {
        lock (_gate)
        {
            _log.Add(action);
            Dispatch(action);
        }
    }

    private void Dispatch(Action<IChatRunView> action)
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
                // A page that has gone away must not stop the reply.
            }
        }
    }

    private static void Replay(object entry, IChatRunView view)
    {
        try
        {
            switch (entry)
            {
                case DeltaEntry delta:
                    if (delta.Reasoning.Length > 0)
                    {
                        view.Delta(delta.Alternative, ChatDelta.FromReasoning(delta.Reasoning.ToString()));
                    }
                    if (delta.Text.Length > 0)
                    {
                        view.Delta(delta.Alternative, ChatDelta.FromText(delta.Text.ToString()));
                    }
                    if (delta.Usage is { } usage)
                    {
                        view.Delta(delta.Alternative, ChatDelta.FromUsage(usage));
                    }
                    if (delta.Finish is { } finish)
                    {
                        view.Delta(delta.Alternative, ChatDelta.FromFinish(finish));
                    }
                    break;
                case Action<IChatRunView> action:
                    action(view);
                    break;
            }
        }
        catch (Exception)
        {
        }
    }
}

/// <summary>The chat replies streaming on this server, one per conversation (see ChatRun). Disposing stops them all.</summary>
public sealed class ChatRunRegistry(IConversationStore store, ConversationEvents events, ChatTitler titler, ILogger<ChatRunRegistry> logger) : IDisposable
{
    private readonly ConcurrentDictionary<Guid, ChatRun> _runs = new();

    public ChatRun? Find(Guid conversationId, Guid userId) =>
        _runs.TryGetValue(conversationId, out var run) && run.UserId == userId ? run : null;

    public ChatRun Start(ChatRunStart start)
    {
        var run = new ChatRun(start, this, store);
        if (!_runs.TryAdd(start.ConversationId, run))
        {
            throw new InvalidOperationException("A reply is already being written in this chat; wait for it or stop it first.");
        }
        run.Begin();
        return run;
    }

    /// <summary>Stops the conversation's reply, if any, and waits briefly for it to save (before deleting the chat).</summary>
    public async Task StopAsync(Guid conversationId)
    {
        if (_runs.TryGetValue(conversationId, out var run))
        {
            run.Stop();
            await Task.WhenAny(run.Completion, Task.Delay(TimeSpan.FromSeconds(5)));
        }
    }

    internal void Finished(ChatRun run)
    {
        _runs.TryRemove(new KeyValuePair<Guid, ChatRun>(run.ConversationId, run));
        Notify(); // the sidebar orders chats by activity
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
            logger.LogDebug(ex, "A conversation change handler failed");
        }
    }

    internal void Failed(ChatRun run, Exception ex) => logger.LogError(ex, "The reply in conversation {Conversation} failed", run.ConversationId);

    /// <summary>After the first reply: the model's title replaces the placeholder unless the user renamed the chat meanwhile.</summary>
    internal async Task SuggestTitleAsync(Guid conversationId, Guid userId, Guid connectionId, string model, string prompt, string reply)
    {
        try
        {
            var placeholder = (await store.GetAsync(conversationId, userId))?.Title;
            var title = await titler.SuggestAsync(connectionId, model, prompt, reply, CancellationToken.None);
            if (title is null || placeholder is null || title == placeholder)
            {
                return;
            }
            if ((await store.GetAsync(conversationId, userId))?.Title != placeholder)
            {
                return;
            }
            await store.SetTitleAsync(conversationId, title);
            Notify();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not title conversation {Conversation}", conversationId);
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
