namespace ButterKnife.Options;

/// <summary>Bound from the "Llm" section of configuration.</summary>
public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    public List<LlmBackendOptions> Backends { get; set; } = [];
}

public enum BackendKind
{
    /// <summary>Ollama native API. BaseUrl is the server root, e.g. http://ollama.local:11434</summary>
    Ollama,

    /// <summary>OpenAI-style API. BaseUrl includes the version prefix, e.g. http://lmstudio.local:1234/v1</summary>
    OpenAiCompatible,
}

public sealed class LlmBackendOptions
{
    public required string Name { get; set; }
    public BackendKind Kind { get; set; } = BackendKind.Ollama;
    public required string BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public string? DefaultModel { get; set; }
}
