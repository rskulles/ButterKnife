namespace ButterKnife.Services;

/// <summary>Resolves clients for the connections currently in the store.</summary>
public interface ILlmClientRegistry
{
    Task<IReadOnlyList<ILlmClient>> GetClientsAsync(CancellationToken cancellationToken = default);

    /// <summary>Throws <see cref="KeyNotFoundException"/> if the connection no longer exists.</summary>
    Task<ILlmClient> GetAsync(Guid connectionId, CancellationToken cancellationToken = default);
}
