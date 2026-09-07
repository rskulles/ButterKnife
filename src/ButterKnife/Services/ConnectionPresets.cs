using ButterKnife.Options;

namespace ButterKnife.Services;

/// <summary>Stock endpoints. Selecting one fills the connection form; the user then adjusts the host.</summary>
public sealed record ConnectionPreset(
    string Name,
    BackendKind Kind,
    string BaseUrl,
    string? DefaultModel,
    bool RequiresApiKey,
    string Hint);

public static class ConnectionPresets
{
    public static readonly IReadOnlyList<ConnectionPreset> All =
    [
        new("Ollama", BackendKind.Ollama, "http://localhost:11434", null, RequiresApiKey: false,
            "Ollama's native API on its default port. Replace localhost with the machine running Ollama."),

        new("LM Studio", BackendKind.OpenAiCompatible, "http://localhost:1234/v1", null, RequiresApiKey: false,
            "LM Studio's OpenAI-compatible server (Developer tab → Start server). Keep the /v1 suffix."),

        new("OpenAI-compatible", BackendKind.OpenAiCompatible, "http://localhost:8000/v1", null, RequiresApiKey: false,
            "Any server that speaks the OpenAI chat API: vLLM, llama.cpp server, text-generation-webui, LocalAI. Keep the /v1 suffix; add an API key if the server requires one."),

        new("Anthropic", BackendKind.Anthropic, "https://api.anthropic.com", "claude-opus-5", RequiresApiKey: true,
            "Anthropic's hosted Claude models. Requires an API key from console.anthropic.com."),
    ];
}
