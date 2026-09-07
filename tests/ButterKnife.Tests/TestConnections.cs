using ButterKnife.Data;
using ButterKnife.Options;
using ButterKnife.Services;

namespace ButterKnife.Tests;

internal static class TestConnections
{
    public static LlmConnection Make(string name, BackendKind kind, string baseUrl, string? defaultModel = null, string? apiKey = null, int? contextWindow = null) =>
        new(Guid.NewGuid(), name, kind, baseUrl, apiKey, defaultModel, contextWindow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}

internal static class StreamTestExtensions
{
    /// <summary>Text deltas only, in order.</summary>
    public static async Task<List<string>> ToTextListAsync(this IAsyncEnumerable<ChatDelta> stream)
    {
        var list = new List<string>();
        await foreach (var delta in stream)
        {
            if (delta.Text is { } text)
            {
                list.Add(text);
            }
        }
        return list;
    }

    public static async Task<TokenUsage?> LastUsageAsync(this IAsyncEnumerable<ChatDelta> stream)
    {
        TokenUsage? usage = null;
        await foreach (var delta in stream)
        {
            usage = delta.Usage ?? usage;
        }
        return usage;
    }
}
