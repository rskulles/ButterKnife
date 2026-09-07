using ButterKnife.Options;
using ButterKnife.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ButterKnife.Tests;

public class LlmClientRegistryTests
{
    [Fact]
    public void BuildsOneClientPerBackendByKind()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new LlmOptions
        {
            Backends =
            [
                new LlmBackendOptions { Name = "Ollama", Kind = BackendKind.Ollama, BaseUrl = "http://a/", DefaultModel = "x" },
                new LlmBackendOptions { Name = "LM Studio", Kind = BackendKind.OpenAiCompatible, BaseUrl = "http://b/v1" },
            ],
        });
        var registry = new LlmClientRegistry(options, new StubClientFactory(StubHandler.Text(""), "http://unused/"));

        Assert.Equal(2, registry.Clients.Count);
        Assert.IsType<OllamaClient>(registry.Get("ollama"));
        Assert.IsType<OpenAiCompatibleClient>(registry.Get("LM Studio"));
        Assert.Equal("x", registry.Get("Ollama").DefaultModel);
        Assert.Throws<KeyNotFoundException>(() => registry.Get("nope"));
    }

    [Fact]
    public void RejectsDuplicateBackendNames()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new LlmOptions
        {
            Backends =
            [
                new LlmBackendOptions { Name = "A", BaseUrl = "http://a/" },
                new LlmBackendOptions { Name = "a", BaseUrl = "http://b/" },
            ],
        });

        Assert.Throws<InvalidOperationException>(
            () => new LlmClientRegistry(options, new StubClientFactory(StubHandler.Text(""), "http://unused/")));
    }

    [Fact]
    public void AddLlmBackends_ConfiguresNamedHttpClientsFromConfiguration()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Llm:Backends:0:Name"] = "Ollama",
            ["Llm:Backends:0:Kind"] = "Ollama",
            ["Llm:Backends:0:BaseUrl"] = "http://ollama.test:11434",
            ["Llm:Backends:1:Name"] = "LM Studio",
            ["Llm:Backends:1:Kind"] = "OpenAiCompatible",
            ["Llm:Backends:1:BaseUrl"] = "http://lm.test:1234/v1",
            ["Llm:Backends:1:ApiKey"] = "secret",
        }).Build();

        var services = new ServiceCollection().AddLogging().AddLlmBackends(config).BuildServiceProvider();
        var factory = services.GetRequiredService<IHttpClientFactory>();

        var ollama = factory.CreateClient("Ollama");
        Assert.Equal("http://ollama.test:11434/", ollama.BaseAddress!.ToString());
        Assert.Equal(Timeout.InfiniteTimeSpan, ollama.Timeout);
        Assert.Null(ollama.DefaultRequestHeaders.Authorization);

        var lm = factory.CreateClient("LM Studio");
        Assert.Equal("http://lm.test:1234/v1/", lm.BaseAddress!.ToString());
        Assert.Equal("Bearer", lm.DefaultRequestHeaders.Authorization!.Scheme);
        Assert.Equal("secret", lm.DefaultRequestHeaders.Authorization.Parameter);

        Assert.Equal(2, services.GetRequiredService<ILlmClientRegistry>().Clients.Count);
        services.GetRequiredService<IOptions<LlmOptions>>();
    }

    [Fact]
    public void AddLlmBackends_RejectsRelativeBaseUrl()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Llm:Backends:0:Name"] = "Bad",
            ["Llm:Backends:0:BaseUrl"] = "not-a-url",
        }).Build();

        var services = new ServiceCollection().AddLogging().AddLlmBackends(config).BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptions<LlmOptions>>().Value);
    }
}
