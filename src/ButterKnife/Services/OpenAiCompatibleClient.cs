using System.Runtime.CompilerServices;
using System.Text.Json;

namespace ButterKnife.Services;

/// <summary>
/// OpenAI-style API: POST chat/completions (SSE stream), GET models.
/// Covers LM Studio, vLLM, llama.cpp server, and Ollama's /v1 shim. BaseUrl must include the version prefix (…/v1).
/// </summary>
public sealed class OpenAiCompatibleClient(IHttpClientFactory httpClientFactory, string backendName, string? defaultModel)
    : LlmClientBase(httpClientFactory, backendName, defaultModel)
{
    private const string DataPrefix = "data:";
    private const string DoneSentinel = "[DONE]";

    public override async IAsyncEnumerable<string> StreamChatAsync(
        string model,
        IReadOnlyList<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var body = new ChatRequest(
            model,
            messages.Select(m => new WireMessage(RoleName(m.Role), m.Content)).ToArray(),
            Stream: true);

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
                yield return delta;
            }
        }
    }

    public override async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var models = await GetJsonAsync<ModelsResponse>("models", cancellationToken);
        return models.Data.Select(m => m.Id).Order(StringComparer.OrdinalIgnoreCase).ToArray();
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

    private sealed record ChatChunk(Choice[]? Choices, ErrorBody? Error);

    private sealed record Choice(Delta? Delta);

    private sealed record Delta(string? Content);

    private sealed record ErrorBody(string? Message);

    private sealed record ModelsResponse(ModelInfo[] Data);

    private sealed record ModelInfo(string Id);
}
