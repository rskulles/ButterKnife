using ButterKnife.Options;
using Microsoft.Extensions.Options;

namespace ButterKnife.Data;

/// <summary>On first run, copies the Llm:Backends configuration into the connections table. Never runs again once any connection exists.</summary>
public sealed class ConnectionSeeder(IConnectionStore store, IOptions<LlmOptions> options, ILogger<ConnectionSeeder> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if ((await store.ListAsync(cancellationToken)).Count > 0)
        {
            return;
        }

        foreach (var backend in options.Value.Backends)
        {
            await store.CreateAsync(backend.Name, backend.Kind, backend.BaseUrl, backend.ApiKey, backend.DefaultModel, backend.ContextWindow, cancellationToken);
            logger.LogInformation("Seeded LLM connection {Name} ({Kind}) at {BaseUrl}", backend.Name, backend.Kind, backend.BaseUrl);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
