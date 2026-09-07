using ButterKnife.Services;

namespace ButterKnife.Components.Pages;

/// <summary>
/// Context meter and compaction. The meter shows the last reported prompt size (exact) or an estimate; compaction asks
/// the model to summarise older turns (see <see cref="ConversationCompactor"/>) so requests stay inside the window.
/// </summary>
public partial class Chat
{
    // Context meter + compaction (see ConversationCompactor). _contextTokens is exact from the last reply, else estimated.
    private string? _summary;
    private int _summaryThrough;
    private int? _contextTokens;
    private int? _contextWindow;
    private bool _compacting;
    private bool _showSummary;
    private CancellationTokenSource? _compaction;
    private readonly Dictionary<string, int?> _windowCache = new(StringComparer.Ordinal);

    private int KeepRecentTurns => Math.Max(1, LlmOptions.Value.CompactKeepRecentTurns);

    private bool CanCompact =>
        !_isStreaming && !_compacting && Selected is not null && _conversationId is not null &&
        _turns.Count - _summaryThrough > KeepRecentTurns + 1;

    /// <summary>Request history as sent on the wire: persona + running summary as the system prompt, then the turns after the checkpoint.</summary>
    private List<ChatMessage> BuildHistory()
    {
        var history = new List<ChatMessage>();
        if (ConversationCompactor.ComposeSystemPrompt(SelectedPersona?.SystemPrompt, _summary) is { } system)
        {
            history.Add(new ChatMessage(ChatRole.System, system));
        }
        history.AddRange(_turns.Skip(_summaryThrough).Select(t => new ChatMessage(t.Role, t.Text, t.Images)));
        return history;
    }

    /// <summary>Tokens the next request would carry: exact (from the last reply) plus whatever is being typed, else an estimate.</summary>
    private (int Tokens, bool Exact) CurrentContext()
    {
        var pending = TokenEstimator.Estimate(_input) + _pendingImages.Count * TokenEstimator.TokensPerImage;
        if (_contextTokens is { } exact)
        {
            return (exact + pending, pending == 0);
        }
        return (TokenEstimator.Estimate(BuildHistory()) + pending, false);
    }

    private string ContextTooltip(int used, bool exact) =>
        (exact ? "Exact, as reported by the backend after the last reply." : "Estimated (~4 characters per token, ~1000 per image); exact after the next reply.")
        + (_contextWindow is null ? " The model's context window is unknown; set it on the connection to see a percentage." : "")
        + (_summary is not null ? $" {_summaryThrough} older messages are compacted into a summary." : "");

    private static string FormatTokens(int tokens) => tokens switch
    {
        >= 1_000_000 => $"{tokens / 1_000_000.0:0.#}M",
        >= 10_000 => $"{tokens / 1000.0:0}k",
        >= 1_000 => $"{tokens / 1000.0:0.#}k",
        _ => tokens.ToString(),
    };

    private async Task RefreshContextWindowAsync()
    {
        if (Selected is not { } model)
        {
            _contextWindow = null;
            return;
        }

        if (_windowCache.TryGetValue(model.Key, out var cached))
        {
            _contextWindow = cached;
            return;
        }

        try
        {
            var client = await Registry.GetAsync(model.ConnectionId, _disposed.Token);
            var window = await client.GetContextWindowAsync(model.Model, _disposed.Token);
            _windowCache[model.Key] = window;
            if (Selected?.Key == model.Key)
            {
                _contextWindow = window;
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (OperationCanceledException) when (_disposed.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // Unknown window is a valid state; the meter shows "?".
        }
    }

    /// <summary>After a reply: make sure the window is known, persist the meter values, and auto-compact if over the threshold.</summary>
    private async Task AfterReplyContextAsync(Guid conversationId, ModelDescriptor model)
    {
        if (_contextWindow is null)
        {
            await RefreshContextWindowAsync();
        }

        try
        {
            await Store.SetContextUsageAsync(conversationId, _contextTokens, _contextWindow, CancellationToken.None);
        }
        catch (Exception)
        {
            // Meter values are advisory; never block the chat on them.
        }

        var threshold = LlmOptions.Value.AutoCompactThreshold;
        if (threshold > 0 && _contextWindow is { } window && window > 0 && _contextTokens is { } used
            && used >= window * threshold && CanCompact)
        {
            await CompactAsync();
            await RenderAndScrollAsync();
        }
    }

    /// <summary>Summarises everything older than the last few turns and stores it as the checkpoint. The transcript is untouched.</summary>
    private async Task CompactAsync()
    {
        if (!CanCompact || Selected is not { } model || _conversationId is not { } conversationId)
        {
            return;
        }

        var through = _turns.Count - KeepRecentTurns;
        if (through <= _summaryThrough)
        {
            return;
        }

        var older = _turns.Skip(_summaryThrough).Take(through - _summaryThrough)
            .Select(t => new ChatMessage(t.Role, t.Text, t.Images)).ToList();

        _compacting = true;
        _error = null;
        _compaction = CancellationTokenSource.CreateLinkedTokenSource(_disposed.Token);
        var token = _compaction.Token;
        await InvokeAsync(StateHasChanged);

        try
        {
            var client = await Registry.GetAsync(model.ConnectionId, token);
            var summary = await Compactor.SummarizeAsync(client, model.Model, _summary, older, token);
            await Store.SetSummaryAsync(conversationId, summary, through, CancellationToken.None);
            _summary = summary;
            _summaryThrough = through;
            _contextTokens = null; // the exact count described the uncompacted history
            await Store.SetContextUsageAsync(conversationId, null, _contextWindow, CancellationToken.None);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (!_disposed.IsCancellationRequested)
            {
                _error = "Compaction cancelled.";
            }
        }
        catch (Exception ex)
        {
            _error = $"Compaction failed: {ex.Message}";
        }
        finally
        {
            _compacting = false;
            _compaction?.Dispose();
            _compaction = null;
        }
    }

    private void CancelCompaction() => _compaction?.Cancel();
}
