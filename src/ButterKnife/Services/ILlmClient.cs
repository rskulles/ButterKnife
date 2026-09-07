namespace ButterKnife.Services;

/// <summary>
/// A single LLM connection. Implementations are per wire protocol; instances are cheap and built per use from the connection store.
/// </summary>
public interface ILlmClient
{
    Guid ConnectionId { get; }

    string BackendName { get; }

    string? DefaultModel { get; }

    /// <summary>Streams assistant token deltas. Honour <paramref name="cancellationToken"/> on every await.</summary>
    IAsyncEnumerable<string> StreamChatAsync(
        string model,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default);
}
