using ButterKnife.Data;

namespace ButterKnife.Services;

public sealed class LlmClientRegistry(IConnectionStore connections, IHttpClientFactory httpClientFactory) : ILlmClientRegistry
{
    public async Task<IReadOnlyList<ILlmClient>> GetClientsAsync(CancellationToken cancellationToken = default)
    {
        var list = await connections.ListAsync(cancellationToken);
        return list.Where(c => LlmClientFactory.IsChatBackend(c.Kind)).Select(c => LlmClientFactory.Create(c, httpClientFactory)).ToArray();
    }

    public async Task<ILlmClient> GetAsync(Guid connectionId, CancellationToken cancellationToken = default)
    {
        var connection = await connections.GetAsync(connectionId, cancellationToken)
            ?? throw new KeyNotFoundException("That connection no longer exists. Pick another model.");
        return LlmClientFactory.Create(connection, httpClientFactory);
    }
}
