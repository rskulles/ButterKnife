namespace ButterKnife.Services;

/// <summary>
/// Token counts reported by the backend for one request, plus timings where the backend measures them
/// (Ollama reports prompt_eval_duration and eval_duration; others leave them null). Any field may be unknown.
/// </summary>
public sealed record TokenUsage(
    int? PromptTokens,
    int? CompletionTokens,
    TimeSpan? PromptDuration = null,
    TimeSpan? CompletionDuration = null);

/// <summary>What the stats readout shows for one reply. Client-side timings plus whatever the backend reported.</summary>
public sealed record GenerationStats(
    TimeSpan Elapsed,
    TimeSpan? TimeToFirstToken,
    int Chunks,
    TokenUsage? Usage)
{
    public int? PromptTokens => Usage?.PromptTokens;

    /// <summary>Prompt tokens per second: from the backend's own prompt timing when available, else prompt tokens over time-to-first-token.</summary>
    public double? PromptTokensPerSecond
    {
        get
        {
            if (Usage?.PromptTokens is not { } prompt || prompt <= 0)
            {
                return null;
            }
            var seconds = Usage.PromptDuration?.TotalSeconds ?? TimeToFirstToken?.TotalSeconds;
            return seconds is > 0.05 ? prompt / seconds : null;
        }
    }

    /// <summary>Output tokens per second over the generation phase only (after the first token), so prompt processing does not drag it down.</summary>
    public double? GenerationTokensPerSecond
    {
        get
        {
            if (Usage?.CompletionTokens is { } completion && completion > 0 && Usage.CompletionDuration?.TotalSeconds is > 0.05 and var backendSeconds)
            {
                return completion / backendSeconds;
            }

            var tokens = Usage?.CompletionTokens ?? Chunks;
            if (tokens <= 1)
            {
                return null;
            }
            var generation = Elapsed - (TimeToFirstToken ?? TimeSpan.Zero);
            return generation.TotalSeconds > 0.3 ? (tokens - 1) / generation.TotalSeconds : null;
        }
    }
}

/// <summary>One streamed item: a text delta, or a usage report (typically the last item).</summary>
public readonly record struct ChatDelta(string? Text, TokenUsage? Usage)
{
    public static ChatDelta FromText(string text) => new(text, null);

    public static ChatDelta FromUsage(TokenUsage usage) => new(null, usage);
}
