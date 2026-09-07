using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ButterKnife.Data;
using ButterKnife.Options;

namespace ButterKnife.Services;

/// <summary>
/// Speech-to-text. Speaks two dialects: OpenAI's <c>audio/transcriptions</c> (Speaches / faster-whisper-server,
/// LocalAI, OpenAI) and whisper.cpp's <c>/inference</c> at the server root, which stock whisper.cpp exposes instead.
/// The dialect is discovered on the first call (a 404 on one means try the other) and remembered per connection.
/// </summary>
public sealed class TranscriptionClient(IHttpClientFactory httpClientFactory)
{
    public const string DefaultModel = "whisper-1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<Guid, Dialect> _dialects = new();

    public enum Dialect
    {
        OpenAi,
        WhisperCpp,
    }

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

        // Buffer once so the request can be replayed against the other dialect.
        using var buffer = new MemoryStream();
        await audio.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();

        var first = _dialects.GetValueOrDefault(connection.Id, Dialect.OpenAi);
        var second = first == Dialect.OpenAi ? Dialect.WhisperCpp : Dialect.OpenAi;

        var (response, body) = await SendAsync(connection, first, bytes, fileName, contentType, language, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            var (retry, retryBody) = await SendAsync(connection, second, bytes, fileName, contentType, language, cancellationToken);
            if (retry.IsSuccessStatusCode)
            {
                _dialects[connection.Id] = second;
            }
            (response, body) = (retry, retryBody);
        }
        else if (response.IsSuccessStatusCode)
        {
            _dialects[connection.Id] = first;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new LlmException(connection.Name, response.StatusCode, body);
        }

        var result = JsonSerializer.Deserialize<TranscriptionResponse>(body, JsonOptions);
        return result?.Text?.Trim() ?? "";
    }

    private async Task<(HttpResponseMessage Response, string Body)> SendAsync(
        LlmConnection connection, Dialect dialect, byte[] audio, string fileName, string contentType, string? language, CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(audio);
        // Browsers report types with parameters ("audio/webm;codecs=opus"); the constructor rejects those, Parse accepts them.
        file.Headers.ContentType = MediaTypeHeaderValue.TryParse(contentType, out var mediaType)
            ? mediaType
            : new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        form.Add(new StringContent("json"), "response_format");
        if (!string.IsNullOrWhiteSpace(language))
        {
            form.Add(new StringContent(language), "language");
        }

        Uri url;
        if (dialect == Dialect.OpenAi)
        {
            form.Add(new StringContent(connection.DefaultModel ?? DefaultModel), "model");
            url = Resolve(connection.BaseUrl, "audio/transcriptions");
        }
        else
        {
            // whisper.cpp: model is fixed at server start; temperature fields are optional. Endpoint lives at the root, not under /v1.
            url = Resolve(ServerRoot(connection.BaseUrl), "inference");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        if (connection.HasApiKey)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.ApiKey);
        }

        var client = httpClientFactory.CreateClient(LlmClientBase.HttpClientName);
        var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return (response, body);
    }

    /// <summary>Connection test: GET models where supported; whisper.cpp's server has no such endpoint, so any HTTP answer counts as reachable.</summary>
    public async Task<IReadOnlyList<string>> ProbeAsync(LlmConnection connection, CancellationToken cancellationToken = default)
    {
        var client = httpClientFactory.CreateClient(LlmClientBase.HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, Resolve(connection.BaseUrl, "models"));
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

    /// <summary>BaseUrl without a trailing /v1, for servers whose non-OpenAI routes live at the root.</summary>
    public static string ServerRoot(string baseUrl)
    {
        var root = baseUrl.TrimEnd('/');
        return root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? root[..^3] : root;
    }

    private static Uri Resolve(string baseUrl, string relative)
    {
        var normalized = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
        return new Uri(new Uri(normalized, UriKind.Absolute), relative);
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
