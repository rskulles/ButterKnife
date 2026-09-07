using ButterKnife.Options;

namespace ButterKnife.Data;

public interface IConnectionStore
{
    /// <summary>Ordered by name.</summary>
    Task<IReadOnlyList<LlmConnection>> ListAsync(CancellationToken cancellationToken = default);

    Task<LlmConnection?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<LlmConnection> CreateAsync(string name, BackendKind kind, string baseUrl, string? apiKey, string? defaultModel, CancellationToken cancellationToken = default);

    Task UpdateAsync(Guid id, string name, BackendKind kind, string baseUrl, string? apiKey, string? defaultModel, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
