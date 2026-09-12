using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using ButterKnife.Data;

namespace ButterKnife.Services;

/// <summary>
/// OpenAI-style API: POST chat/completions (SSE stream), GET models.
/// Covers LM Studio, vLLM, llama.cpp server, and Ollama's /v1 shim. BaseUrl must include the version prefix (…/v1).
/// </summary>
public sealed class OpenAiCompatibleClient(IHttpClientFactory httpClientFactory, LlmConnection connection)
    : LlmClientBase(httpClientFactory, connection)
{
    private const string DataPrefix = "data:";
    private const string DoneSentinel = "[DONE]";

    public override async IAsyncEnumerable<ChatDelta> StreamChatAsync(
        string model,
        IReadOnlyList<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var body = new ChatRequest(
            model,
            messages.Select(m => new WireMessage(RoleName(m.Role), BuildContent(m))).ToArray(),
            Stream: true,
            StreamOptions: new StreamOptions(IncludeUsage: true));

        var response = await PostStreamingAsync("chat/completions", body, cancellationToken);

        await foreach (var line in ReadLinesAsync(response, cancellationToken))
        {
            if (!line.StartsWith(DataPrefix, StringComparison.Ordinal))
            {
                continue; // comments, event: lines, blank separators
            }

            var payload = line[DataPrefix.Length..].Trim();
            if (payload.Length == 0)
            {
                continue;
            }

            if (payload == DoneSentinel)
            {
                yield break;
            }

            var chunk = JsonSerializer.Deserialize<ChatChunk>(payload, JsonOptions);
            if (chunk?.Error is { } error)
            {
                throw new InvalidOperationException($"{BackendName}: {error.Message}");
            }

            var delta = chunk?.Choices is { Length: > 0 } choices ? choices[0].Delta?.Content : null;
            if (!string.IsNullOrEmpty(delta))
            {
                yield return ChatDelta.FromText(delta);
            }

            // With stream_options.include_usage the final chunk carries usage (and usually no choices).
            if (chunk?.Usage is { } usage && (usage.PromptTokens is not null || usage.CompletionTokens is not null))
            {
                yield return ChatDelta.FromUsage(new TokenUsage(usage.PromptTokens, usage.CompletionTokens));
            }
        }
    }

    public override async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var models = await GetJsonAsync<ModelsResponse>("models", cancellationToken);
        return models.Data.Select(m => m.Id).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// The OpenAI chat API has no standard way to report a model's window. LM Studio exposes it on its own REST API
    /// at the server root (/api/v0/models/{id} → max_context_length); other servers return null unless configured.
    /// </summary>
    public override async Task<int?> GetContextWindowAsync(string model, CancellationToken cancellationToken = default)
    {
        if (Connection.ContextWindow is { } configured)
        {
            return configured;
        }

        var info = await GetLmStudioModelAsync(model, cancellationToken);
        return info?.MaxContextLength is > 0 ? info.MaxContextLength : null;
    }

    /// <summary>LM Studio types its models "vlm" (vision) or "llm"; there is no OpenAI-standard signal, so other servers are "unknown".</summary>
    public override async Task<bool?> SupportsImagesAsync(string model, CancellationToken cancellationToken = default)
    {
        var info = await GetLmStudioModelAsync(model, cancellationToken);
        return info?.Type?.ToLowerInvariant() switch
        {
            "vlm" => true,
            "llm" => false,
            _ => null,
        };
    }

    /// <summary>LM Studio's native REST API lives at the server root, i.e. BaseUrl without its /v1.</summary>
    private Task<LmStudioModel?> GetLmStudioModelAsync(string model, CancellationToken cancellationToken)
    {
        var root = Connection.BaseUrl.TrimEnd('/');
        if (root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            root = root[..^3];
        }

        return TryGetJsonAsync<LmStudioModel>($"{root}/api/v0/models/{Uri.EscapeDataString(model)}", cancellationToken);
    }

    /// <summary>Plain string when text-only (widest compatibility); otherwise the multimodal parts array.</summary>
    private static object BuildContent(ChatMessage message)
    {
        if (!message.HasImages)
        {
            return message.Content;
        }

        var parts = new List<object>();
        if (message.Content.Length > 0)
        {
            parts.Add(new TextPart(message.Content));
        }
        parts.AddRange(message.Images.Select(i => new ImagePart(new ImageUrl(i.DataUrl))));
        return parts;
    }

    private static string RoleName(ChatRole role) => role switch
    {
        ChatRole.System => "system",
        ChatRole.User => "user",
        ChatRole.Assistant => "assistant",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    private sealed record ChatRequest(string Model, WireMessage[] Messages, bool Stream, [property: JsonPropertyName("stream_options")] StreamOptions? StreamOptions = null);

    private sealed record StreamOptions([property: JsonPropertyName("include_usage")] bool IncludeUsage);

    private sealed record WireMessage(string Role, object Content);

    private sealed record TextPart(string Text)
    {
        public string Type => "text";
    }

    private sealed record ImagePart([property: JsonPropertyName("image_url")] ImageUrl ImageUrl)
    {
        public string Type => "image_url";
    }

    private sealed record ImageUrl(string Url);

    private sealed record ChatChunk(Choice[]? Choices, ErrorBody? Error, Usage? Usage);

    private sealed record Usage(
        [property: JsonPropertyName("prompt_tokens")] int? PromptTokens,
        [property: JsonPropertyName("completion_tokens")] int? CompletionTokens);

    private sealed record LmStudioModel([property: JsonPropertyName("max_context_length")] int? MaxContextLength, string? Type);

    private sealed record Choice(Delta? Delta);

    private sealed record Delta(string? Content);

    private sealed record ErrorBody(string? Message);

    private sealed record ModelsResponse(ModelInfo[] Data);

    private sealed record ModelInfo(string Id);
}
