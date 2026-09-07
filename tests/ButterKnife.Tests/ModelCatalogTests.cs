using ButterKnife.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ButterKnife.Tests;

public class ModelCatalogTests
{
    [Fact]
    public async Task ReportsUnreachableBackendWithoutFailingOthers()
    {
        var healthy = new FakeClient("Healthy", ["b", "a"]);
        var broken = new FakeClient("Broken", null);
        var catalog = new ModelCatalog(new FakeRegistry(healthy, broken), NullLogger<ModelCatalog>.Instance);

        var result = await catalog.GetModelsAsync(CancellationToken.None);

        Assert.Equal([new ModelDescriptor("Healthy", "b"), new ModelDescriptor("Healthy", "a")], result.Models);
        Assert.Single(result.BackendErrors);
        Assert.Contains("connection refused", result.BackendErrors["Broken"]);
    }

    private sealed class FakeRegistry(params ILlmClient[] clients) : ILlmClientRegistry
    {
        public IReadOnlyList<ILlmClient> Clients => clients;
        public ILlmClient Get(string backendName) => clients.Single(c => c.BackendName == backendName);
    }

    private sealed class FakeClient(string name, string[]? models) : ILlmClient
    {
        public string BackendName => name;
        public string? DefaultModel => null;

        public IAsyncEnumerable<string> StreamChatAsync(string model, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
            => models is null
                ? Task.FromException<IReadOnlyList<string>>(new HttpRequestException("connection refused"))
                : Task.FromResult<IReadOnlyList<string>>(models);
    }
}
