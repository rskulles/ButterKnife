using ButterKnife.Data;
using ButterKnife.Options;
using ButterKnife.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ButterKnife.Tests;

public class LlmClientRegistryTests
{
    [Fact]
    public async Task BuildsClientPerConnectionByKind()
    {
        var ollama = TestConnections.Make("Ollama", BackendKind.Ollama, "http://a/", defaultModel: "x");
        var lm = TestConnections.Make("LM Studio", BackendKind.OpenAiCompatible, "http://b/v1");
        var claude = TestConnections.Make("Anthropic", BackendKind.Anthropic, "https://api.anthropic.com", apiKey: "k");
        var registry = new LlmClientRegistry(new FakeStore(ollama, lm, claude), new StubClientFactory(StubHandler.Text(""), "http://unused/"));

        var clients = await registry.GetClientsAsync(CancellationToken.None);

        Assert.Equal(3, clients.Count);
        Assert.IsType<OllamaClient>(await registry.GetAsync(ollama.Id, CancellationToken.None));
        Assert.IsType<OpenAiCompatibleClient>(await registry.GetAsync(lm.Id, CancellationToken.None));
        Assert.IsType<AnthropicLlmClient>(await registry.GetAsync(claude.Id, CancellationToken.None));
        Assert.Equal("x", (await registry.GetAsync(ollama.Id, CancellationToken.None)).DefaultModel);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => registry.GetAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public void AddLlmBackends_RegistersSharedClientWithInfiniteTimeout()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Llm:Backends:0:Name"] = "Ollama",
            ["Llm:Backends:0:BaseUrl"] = "http://ollama.test:11434",
        }).Build();

        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConnectionStore>(new FakeStore())
            .AddLlmBackends(config)
            .BuildServiceProvider();

        var client = services.GetRequiredService<IHttpClientFactory>().CreateClient(LlmClientBase.HttpClientName);
        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
        Assert.NotNull(services.GetRequiredService<ILlmClientRegistry>());
        Assert.NotNull(services.GetRequiredService<ModelCatalog>());
    }

    [Fact]
    public async Task Seeder_CopiesConfigOnlyWhenStoreIsEmpty()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Llm:Backends:0:Name"] = "Ollama",
            ["Llm:Backends:0:Kind"] = "Ollama",
            ["Llm:Backends:0:BaseUrl"] = "http://ollama.test:11434",
            ["Llm:Backends:0:DefaultModel"] = "llama3",
        }).Build();
        var store = new FakeStore();
        var services = new ServiceCollection().AddLogging().AddSingleton<IConnectionStore>(store).AddLlmBackends(config).BuildServiceProvider();
        var seeder = services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<ConnectionSeeder>().Single();

        await seeder.StartAsync(CancellationToken.None);
        await seeder.StartAsync(CancellationToken.None); // second start must not duplicate

        var all = await store.ListAsync(CancellationToken.None);
        Assert.Single(all);
        Assert.Equal(("Ollama", BackendKind.Ollama, "http://ollama.test:11434", "llama3"), (all[0].Name, all[0].Kind, all[0].BaseUrl, all[0].DefaultModel));
    }

    private sealed class FakeStore(params LlmConnection[] seed) : IConnectionStore
    {
        private readonly List<LlmConnection> _items = [.. seed];

        public Task<IReadOnlyList<LlmConnection>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LlmConnection>>(_items.ToArray());

        public Task<LlmConnection?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_items.FirstOrDefault(c => c.Id == id));

        public Task<LlmConnection> CreateAsync(string name, BackendKind kind, string baseUrl, string? apiKey, string? defaultModel, int? contextWindow, CancellationToken cancellationToken = default)
        {
            var c = TestConnections.Make(name, kind, baseUrl, defaultModel, apiKey, contextWindow);
            _items.Add(c);
            return Task.FromResult(c);
        }

        public Task UpdateAsync(Guid id, string name, BackendKind kind, string baseUrl, string? apiKey, string? defaultModel, int? contextWindow, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            _items.RemoveAll(c => c.Id == id);
            return Task.CompletedTask;
        }
    }
}
