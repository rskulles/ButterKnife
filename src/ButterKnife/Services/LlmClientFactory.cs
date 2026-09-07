using ButterKnife.Data;
using ButterKnife.Options;

namespace ButterKnife.Services;

public static class LlmClientFactory
{
    public static ILlmClient Create(LlmConnection connection, IHttpClientFactory httpClientFactory) => connection.Kind switch
    {
        BackendKind.Ollama => new OllamaClient(httpClientFactory, connection),
        BackendKind.OpenAiCompatible => new OpenAiCompatibleClient(httpClientFactory, connection),
        BackendKind.Anthropic => new AnthropicLlmClient(connection),
        _ => throw new InvalidOperationException($"Unknown backend kind '{connection.Kind}' for '{connection.Name}'."),
    };
}
