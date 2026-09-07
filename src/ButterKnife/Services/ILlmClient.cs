namespace ButterKnife.Services;

/// <summary>
/// A single LLM backend. One instance per configured backend; implementations are per wire protocol.
/// </summary>
public interface ILlmClient
{
    string BackendName { get; }

    string? DefaultModel { get; }

    /// <summary>Streams assistant token deltas. Honour <paramref name="cancellationToken"/> on every await.</summary>
    IAsyncEnumerable<string> StreamChatAsync(
        string model,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default);
}
