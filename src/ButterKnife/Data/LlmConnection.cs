using ButterKnife.Options;

namespace ButterKnife.Data;

/// <summary>A configured LLM server. <see cref="ApiKey"/> is decrypted in memory; it is protected at rest.
/// <see cref="ContextWindow"/> overrides what the server reports (and is sent to Ollama as num_ctx).</summary>
public sealed record LlmConnection(
    Guid Id,
    string Name,
    BackendKind Kind,
    string BaseUrl,
    string? ApiKey,
    string? DefaultModel,
    int? ContextWindow,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public bool HasApiKey => !string.IsNullOrEmpty(ApiKey);
}
