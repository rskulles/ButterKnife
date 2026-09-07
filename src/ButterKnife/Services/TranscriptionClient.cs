using System.Net.Http.Headers;
using System.Text.Json;
using ButterKnife.Data;
using ButterKnife.Options;

namespace ButterKnife.Services;

/// <summary>
/// Speech-to-text through an OpenAI-style <c>audio/transcriptions</c> endpoint. Works with local Whisper servers
/// (faster-whisper-server / Speaches, whisper.cpp server, LocalAI) and with OpenAI itself. BaseUrl includes /v1.
/// </summary>
public sealed class TranscriptionClient(IHttpClientFactory httpClientFactory)
{
    public const string DefaultModel = "whisper-1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<string> TranscribeAsync(
        LlmConnection connection,
        Stream audio,
        string fileName,
        string contentType,
        string? language,
        CancellationToken cancellationToken = default)
    {
        if (connection.Kind != BackendKind.Transcription)
        {
            throw new InvalidOperationException($"{connection.Name} is not a transcription connection.");
        }

        using var form = new MultipartFormDataContent();
        var file = new StreamContent(audio);
        // Browsers report types with parameters ("audio/webm;codecs=opus"); the constructor rejects those, Parse accepts them.
        file.Headers.ContentType = MediaTypeHeaderValue.TryParse(contentType, out var mediaType)
            ? mediaType
            : new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        form.Add(new StringContent(connection.DefaultModel ?? DefaultModel), "model");
        form.Add(new StringContent("json"), "response_format");
        if (!string.IsNullOrWhiteSpace(language))
        {
            form.Add(new StringContent(language), "language");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, Resolve(connection, "audio/transcriptions")) { Content = form };
        if (connection.HasApiKey)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.ApiKey);
        }

        var client = httpClientFactory.CreateClient(LlmClientBase.HttpClientName);
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new LlmException(connection.Name, response.StatusCode, body);
        }

        var result = JsonSerializer.Deserialize<TranscriptionResponse>(body, JsonOptions);
        return result?.Text?.Trim() ?? "";
    }

    /// <summary>Connection test: GET models where supported; whisper.cpp's server has no such endpoint, so any HTTP answer counts as reachable.</summary>
    public async Task<IReadOnlyList<string>> ProbeAsync(LlmConnection connection, CancellationToken cancellationToken = default)
    {
        var client = httpClientFactory.CreateClient(LlmClientBase.HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, Resolve(connection, "models"));
        if (connection.HasApiKey)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.ApiKey);
        }

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            var models = JsonSerializer.Deserialize<ModelsResponse>(body, JsonOptions);
            return models?.Data?.Select(m => m.Id).Where(id => !string.IsNullOrEmpty(id)).ToArray() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static Uri Resolve(LlmConnection connection, string relative)
    {
        var baseUrl = connection.BaseUrl.EndsWith('/') ? connection.BaseUrl : connection.BaseUrl + "/";
        return new Uri(new Uri(baseUrl, UriKind.Absolute), relative);
    }

    private sealed record TranscriptionResponse(string? Text);

    private sealed record ModelsResponse(ModelInfo[]? Data);

    private sealed record ModelInfo(string Id);
}

/// <summary>Finds the configured transcription connection (the first one of that kind) and runs dictation through it.</summary>
public sealed class TranscriptionService(IConnectionStore connections, TranscriptionClient client)
{
    public async Task<LlmConnection?> GetConnectionAsync(CancellationToken cancellationToken = default) =>
        (await connections.ListAsync(cancellationToken)).FirstOrDefault(c => c.Kind == BackendKind.Transcription);

    public async Task<string> TranscribeAsync(Stream audio, string contentType, CancellationToken cancellationToken = default)
    {
        var connection = await GetConnectionAsync(cancellationToken)
            ?? throw new InvalidOperationException("No transcription connection is configured.");

        var extension = contentType switch
        {
            var t when t.Contains("webm", StringComparison.OrdinalIgnoreCase) => "webm",
            var t when t.Contains("ogg", StringComparison.OrdinalIgnoreCase) => "ogg",
            var t when t.Contains("mp4", StringComparison.OrdinalIgnoreCase) => "mp4",
            var t when t.Contains("wav", StringComparison.OrdinalIgnoreCase) => "wav",
            _ => "webm",
        };

        return await client.TranscribeAsync(connection, audio, $"recording.{extension}", contentType, language: null, cancellationToken);
    }
}
