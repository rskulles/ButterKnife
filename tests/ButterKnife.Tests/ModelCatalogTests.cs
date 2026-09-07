using ButterKnife.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ButterKnife.Tests;

public class ModelCatalogTests
{
    [Fact]
    public async Task ReportsUnreachableConnectionWithoutFailingOthersAndMarksDefault()
    {
        var healthy = new FakeClient("Healthy", ["b", "a"], defaultModel: "a");
        var broken = new FakeClient("Broken", null);
        var catalog = new ModelCatalog(new FakeRegistry(healthy, broken), NullLogger<ModelCatalog>.Instance);

        var result = await catalog.GetModelsAsync(CancellationToken.None);

        Assert.Equal(
            [new ModelDescriptor(healthy.ConnectionId, "Healthy", "b", false), new ModelDescriptor(healthy.ConnectionId, "Healthy", "a", true)],
            result.Models);
        Assert.Single(result.BackendErrors);
        Assert.Contains("connection refused", result.BackendErrors["Broken"]);
        Assert.Equal($"{healthy.ConnectionId:D}::a", result.Models[1].Key);
    }

    private sealed class FakeRegistry(params ILlmClient[] clients) : ILlmClientRegistry
    {
        public Task<IReadOnlyList<ILlmClient>> GetClientsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ILlmClient>>(clients);

        public Task<ILlmClient> GetAsync(Guid connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(clients.Single(c => c.ConnectionId == connectionId));
    }

    private sealed class FakeClient(string name, string[]? models, string? defaultModel = null) : ILlmClient
    {
        public Guid ConnectionId { get; } = Guid.NewGuid();
        public string BackendName => name;
        public string? DefaultModel => defaultModel;

        public IAsyncEnumerable<string> StreamChatAsync(string model, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
            => models is null
                ? Task.FromException<IReadOnlyList<string>>(new HttpRequestException("connection refused"))
                : Task.FromResult<IReadOnlyList<string>>(models);
    }
}
