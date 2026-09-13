using ButterKnife.Data;
using ButterKnife.Options;

namespace ButterKnife.Services;

public static class LlmClientFactory
{
    public static bool IsChatBackend(BackendKind kind) => kind is not (BackendKind.Transcription or BackendKind.ImageGeneration);

    public static ILlmClient Create(LlmConnection connection, IHttpClientFactory httpClientFactory) => connection.Kind switch
    {
        BackendKind.Ollama => new OllamaClient(httpClientFactory, connection),
        BackendKind.OpenAiCompatible => new OpenAiCompatibleClient(httpClientFactory, connection),
        BackendKind.Anthropic => new AnthropicLlmClient(connection),
        BackendKind.Transcription => throw new InvalidOperationException($"'{connection.Name}' is a transcription connection, not a chat backend."),
        BackendKind.ImageGeneration => throw new InvalidOperationException($"'{connection.Name}' is an image generation connection, not a chat backend."),
        _ => throw new InvalidOperationException($"Unknown backend kind '{connection.Kind}' for '{connection.Name}'."),
    };
}
