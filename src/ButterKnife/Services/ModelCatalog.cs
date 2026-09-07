namespace ButterKnife.Services;

public sealed record ModelDescriptor(string Backend, string Model)
{
    public string Key => $"{Backend}::{Model}";

    public override string ToString() => $"{Model}  ({Backend})";
}

public sealed record ModelCatalogResult(
    IReadOnlyList<ModelDescriptor> Models,
    IReadOnlyDictionary<string, string> BackendErrors);

/// <summary>Asks every configured backend what it can serve. A backend that is down is reported, not fatal.</summary>
public sealed class ModelCatalog(ILlmClientRegistry registry, ILogger<ModelCatalog> logger)
{
    public async Task<ModelCatalogResult> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        var tasks = registry.Clients.Select(async client =>
        {
            try
            {
                var models = await client.ListModelsAsync(cancellationToken);
                return (client.BackendName, Models: models, Error: (string?)null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not list models from backend {Backend}", client.BackendName);
                return (client.BackendName, Models: Array.Empty<string>(), Error: ex.Message);
            }
        });

        var results = await Task.WhenAll(tasks);

        var models = results
            .SelectMany(r => r.Models.Select(m => new ModelDescriptor(r.BackendName, m)))
            .ToArray();

        var errors = results
            .Where(r => r.Error is not null)
            .ToDictionary(r => r.BackendName, r => r.Error!, StringComparer.OrdinalIgnoreCase);

        return new ModelCatalogResult(models, errors);
    }
}
