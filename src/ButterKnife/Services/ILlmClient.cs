namespace ButterKnife.Services;

/// <summary>
/// A single LLM connection. Implementations are per wire protocol; instances are cheap and built per use from the connection store.
/// </summary>
public interface ILlmClient
{
    Guid ConnectionId { get; }

    string BackendName { get; }

    string? DefaultModel { get; }

    /// <summary>Streams text deltas and, when the backend reports it, a final usage item. Honour <paramref name="cancellationToken"/> on every await.</summary>
    IAsyncEnumerable<ChatDelta> StreamChatAsync(
        string model,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default);

    /// <summary>The model's context window in tokens, or null when the backend cannot tell us. Never throws for "unknown".</summary>
    Task<int?> GetContextWindowAsync(string model, CancellationToken cancellationToken = default);

    /// <summary>Whether the model accepts image input: true/false when the backend reports it, null when it cannot tell us. Never throws for "unknown".</summary>
    Task<bool?> SupportsImagesAsync(string model, CancellationToken cancellationToken = default);
}
