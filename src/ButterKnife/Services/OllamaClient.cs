using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ButterKnife.Services;

/// <summary>Ollama native API: POST /api/chat (NDJSON stream), GET /api/tags.</summary>
public sealed class OllamaClient(IHttpClientFactory httpClientFactory, string backendName, string? defaultModel)
    : LlmClientBase(httpClientFactory, backendName, defaultModel)
{
    public override async IAsyncEnumerable<string> StreamChatAsync(
        string model,
        IReadOnlyList<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var body = new ChatRequest(
            model,
            messages.Select(m => new WireMessage(RoleName(m.Role), m.Content)).ToArray(),
            Stream: true);

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
                yield return chunk.Message.Content;
            }

            if (chunk.Done)
            {
                yield break;
            }
        }
    }

    public override async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var tags = await GetJsonAsync<TagsResponse>("api/tags", cancellationToken);
        return tags.Models.Select(m => m.Name).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string RoleName(ChatRole role) => role switch
    {
        ChatRole.System => "system",
        ChatRole.User => "user",
        ChatRole.Assistant => "assistant",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    private sealed record ChatRequest(string Model, WireMessage[] Messages, bool Stream);

    private sealed record WireMessage(string Role, string Content);

    private sealed record ChatChunk(WireMessage? Message, bool Done, string? Error);

    private sealed record TagsResponse(ModelTag[] Models);

    private sealed record ModelTag(string Name, [property: JsonPropertyName("modified_at")] DateTimeOffset? ModifiedAt);
}
