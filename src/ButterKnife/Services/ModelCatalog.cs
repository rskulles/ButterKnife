namespace ButterKnife.Services;

public sealed record ModelDescriptor(Guid ConnectionId, string ConnectionName, string Model, bool IsDefault)
{
    public string Key => MakeKey(ConnectionId, Model);

    public static string MakeKey(Guid connectionId, string model) => $"{connectionId:D}::{model}";

    public override string ToString() => $"{Model}  ({ConnectionName})";
}

public sealed record ModelCatalogResult(
    IReadOnlyList<ModelDescriptor> Models,
    IReadOnlyDictionary<string, string> BackendErrors);

/// <summary>Asks every connection what it can serve. A connection that is down is reported, not fatal.</summary>
public sealed class ModelCatalog(ILlmClientRegistry registry, ILogger<ModelCatalog> logger)
{
    public async Task<ModelCatalogResult> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        var clients = await registry.GetClientsAsync(cancellationToken);

        var tasks = clients.Select(async client =>
        {
            try
            {
                var models = await client.ListModelsAsync(cancellationToken);
                return (Client: client, Models: models, Error: (string?)null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not list models from connection {Backend}", client.BackendName);
                return (Client: client, Models: Array.Empty<string>(), Error: ex.Message);
            }
        });

        var results = await Task.WhenAll(tasks);

        var models = results
            .SelectMany(r => r.Models.Select(m => new ModelDescriptor(
                r.Client.ConnectionId,
                r.Client.BackendName,
                m,
                IsDefault: string.Equals(m, r.Client.DefaultModel, StringComparison.OrdinalIgnoreCase))))
            .ToArray();

        var errors = results
            .Where(r => r.Error is not null)
            .ToDictionary(r => r.Client.BackendName, r => r.Error!, StringComparer.OrdinalIgnoreCase);

        return new ModelCatalogResult(models, errors);
    }
}
