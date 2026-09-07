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

    /// <summary>Compact automatically when the last request used at least this fraction of the context window. 0 disables.</summary>
    public double AutoCompactThreshold { get; set; } = 0.8;

    /// <summary>Turns kept verbatim after the summary checkpoint when compacting.</summary>
    public int CompactKeepRecentTurns { get; set; } = 4;
}

public enum BackendKind
{
    /// <summary>Ollama native API. BaseUrl is the server root, e.g. http://ollama.local:11434</summary>
    Ollama,

    /// <summary>OpenAI-style API. BaseUrl includes the version prefix, e.g. http://lmstudio.local:1234/v1</summary>
    OpenAiCompatible,

    /// <summary>Anthropic Messages API via the official SDK. BaseUrl is normally https://api.anthropic.com</summary>
    Anthropic,

    /// <summary>Speech-to-text only: an OpenAI-style audio/transcriptions endpoint (Whisper servers). Not a chat backend.</summary>
    Transcription,
}

public sealed class LlmBackendOptions
{
    public required string Name { get; set; }
    public BackendKind Kind { get; set; } = BackendKind.Ollama;
    public required string BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public string? DefaultModel { get; set; }

    /// <summary>Context window in tokens; overrides what the server reports. Sent to Ollama as num_ctx.</summary>
    public int? ContextWindow { get; set; }
}
