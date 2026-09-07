namespace ButterKnife.Options;

/// <summary>Bound from the "Llm" section of configuration.</summary>
public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    /// <summary>
    /// Seed connections. Only used when the connections table is empty on startup; afterwards
    /// connections are managed in the UI and stored in SQLite.
    /// </summary>
    public List<LlmBackendOptions> Backends { get; set; } = [];

    /// <summary>Name of the persona preselected for new conversations. Empty or unknown means no persona.</summary>
    public string DefaultPersona { get; set; } = "";
}

public enum BackendKind
{
    /// <summary>Ollama native API. BaseUrl is the server root, e.g. http://ollama.local:11434</summary>
    Ollama,

    /// <summary>OpenAI-style API. BaseUrl includes the version prefix, e.g. http://lmstudio.local:1234/v1</summary>
    OpenAiCompatible,

    /// <summary>Anthropic Messages API via the official SDK. BaseUrl is normally https://api.anthropic.com</summary>
    Anthropic,
}

public sealed class LlmBackendOptions
{
    public required string Name { get; set; }
    public BackendKind Kind { get; set; } = BackendKind.Ollama;
    public required string BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public string? DefaultModel { get; set; }
}
