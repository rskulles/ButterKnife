using System.Runtime.CompilerServices;
using ButterKnife.Data;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ButterKnife.Services;

/// <summary>Ollama native API: POST /api/chat (NDJSON stream), GET /api/tags.</summary>
public sealed class OllamaClient(IHttpClientFactory httpClientFactory, LlmConnection connection)
    : LlmClientBase(httpClientFactory, connection)
{
    public override async IAsyncEnumerable<ChatDelta> StreamChatAsync(
        string model,
        IReadOnlyList<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var body = new ChatRequest(
            model,
            messages.Select(m => new WireMessage(
                RoleName(m.Role),
                m.Content,
                m.HasImages ? m.Images.Select(i => i.Base64).ToArray() : null)).ToArray(),
            Stream: true,
            // A configured window is also the num_ctx we ask Ollama to run with; otherwise the server default applies.
            Options: Connection.ContextWindow is { } numCtx ? new RequestOptions(numCtx) : null);

        var response = await PostStreamingAsync("api/chat", body, cancellationToken);

        await foreach (var line in ReadLinesAsync(response, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var chunk = JsonSerializer.Deserialize<ChatChunk>(line, JsonOptions);
            if (chunk is null)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(chunk.Error))
            {
                throw new InvalidOperationException($"{BackendName}: {chunk.Error}");
            }

            if (!string.IsNullOrEmpty(chunk.Message?.Content))
            {
                yield return ChatDelta.FromText(chunk.Message.Content);
            }

            if (chunk.Done)
            {
                if (chunk.PromptEvalCount is not null || chunk.EvalCount is not null)
                {
                    yield return ChatDelta.FromUsage(new TokenUsage(
                        chunk.PromptEvalCount,
                        chunk.EvalCount,
                        Nanos(chunk.PromptEvalDuration),
                        Nanos(chunk.EvalDuration)));
                }
                yield break;
            }
        }
    }

    public override async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var tags = await GetJsonAsync<TagsResponse>("api/tags", cancellationToken);
        return tags.Models.Select(m => m.Name).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// The configured override wins. Otherwise /api/ps reports the window a loaded model is actually running with,
    /// and /api/show reports the model's trained maximum (which may exceed the server's num_ctx).
    /// </summary>
    public override async Task<int?> GetContextWindowAsync(string model, CancellationToken cancellationToken = default)
    {
        if (Connection.ContextWindow is { } configured)
        {
            return configured;
        }

        var running = await TryGetJsonAsync<PsResponse>("api/ps", cancellationToken);
        var loaded = running?.Models?.FirstOrDefault(m => string.Equals(m.Name, model, StringComparison.OrdinalIgnoreCase) || string.Equals(m.Model, model, StringComparison.OrdinalIgnoreCase));
        if (loaded?.ContextLength is > 0)
        {
            return loaded.ContextLength;
        }

        var shown = await TryPostJsonAsync<ShowResponse, ShowRequest>("api/show", new ShowRequest(model), cancellationToken);
        if (shown?.ModelInfo is { } info)
        {
            foreach (var (key, value) in info)
            {
                if (key.EndsWith(".context_length", StringComparison.OrdinalIgnoreCase) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var length) && length > 0)
                {
                    return length;
                }
            }
        }
        return null;
    }

    private static TimeSpan? Nanos(long? nanoseconds) =>
        nanoseconds is > 0 ? TimeSpan.FromTicks(nanoseconds.Value / 100) : null;

    private static string RoleName(ChatRole role) => role switch
    {
        ChatRole.System => "system",
        ChatRole.User => "user",
        ChatRole.Assistant => "assistant",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    private sealed record ChatRequest(string Model, WireMessage[] Messages, bool Stream, RequestOptions? Options = null);

    private sealed record RequestOptions([property: JsonPropertyName("num_ctx")] int NumCtx);

    private sealed record WireMessage(string Role, string Content, string[]? Images = null);

    private sealed record ChatChunk(
        WireMessage? Message,
        bool Done,
        string? Error,
        [property: JsonPropertyName("prompt_eval_count")] int? PromptEvalCount,
        [property: JsonPropertyName("eval_count")] int? EvalCount,
        [property: JsonPropertyName("prompt_eval_duration")] long? PromptEvalDuration,
        [property: JsonPropertyName("eval_duration")] long? EvalDuration);

    private sealed record PsResponse(PsModel[]? Models);

    private sealed record PsModel(string? Name, string? Model, [property: JsonPropertyName("context_length")] int? ContextLength);

    private sealed record ShowRequest(string Model);

    private sealed record ShowResponse([property: JsonPropertyName("model_info")] Dictionary<string, JsonElement>? ModelInfo);

    private sealed record TagsResponse(ModelTag[] Models);

    private sealed record ModelTag(string Name, [property: JsonPropertyName("modified_at")] DateTimeOffset? ModifiedAt);
}
