using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ButterKnife.Data;
using ButterKnife.Options;

namespace ButterKnife.Services;

/// <summary>What the "/image" command asks for: a size in pixels and, optionally, steps and a seed.</summary>
public sealed record ImageRequest(string Prompt, int Width = 1024, int Height = 1024, int? Steps = null, int? Seed = null);

/// <summary>A picture back from the server: PNG bytes plus what it reports about the render.</summary>
public sealed record GeneratedImage(byte[] Png, int? Seed, double? Seconds, string? Model);

/// <summary>
/// Talks to an image generation connection: the OpenAI images API (`POST {BaseUrl}/images/generations`, `b64_json`),
/// which Crayon Cloud and LocalAI serve. `steps` and `seed` are Crayon Cloud extras that other servers ignore.
/// </summary>
public sealed class ImageGenerationClient(IHttpClientFactory httpClientFactory)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Model ids the server offers, for the Test button; an empty list is still "reachable".</summary>
    public async Task<IReadOnlyList<string>> ProbeAsync(LlmConnection connection, CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient(LlmClientBase.HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, Resolve(connection, "models"));
        Authorize(request, connection);
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new LlmException(connection.Name, response.StatusCode, body);
        }
        using var json = JsonDocument.Parse(body);
        return json.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray().Select(m => m.TryGetProperty("id", out var id) ? id.GetString() : null).OfType<string>().ToArray()
            : [];
    }

    public async Task<GeneratedImage> GenerateAsync(LlmConnection connection, ImageRequest request, CancellationToken cancellationToken)
    {
        if (connection.Kind != BackendKind.ImageGeneration)
        {
            throw new InvalidOperationException($"'{connection.Name}' is not an image generation connection.");
        }

        var body = new WireRequest(request.Prompt, $"{request.Width}x{request.Height}", request.Steps, request.Seed, string.IsNullOrWhiteSpace(connection.DefaultModel) ? null : connection.DefaultModel);
        using var client = httpClientFactory.CreateClient(LlmClientBase.HttpClientName);
        using var http = new HttpRequestMessage(HttpMethod.Post, Resolve(connection, "images/generations"))
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json"),
        };
        Authorize(http, connection);

        using var response = await client.SendAsync(http, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new LlmException(connection.Name, response.StatusCode, text);
        }

        var reply = JsonSerializer.Deserialize<WireResponse>(text, JsonOptions);
        var first = reply?.Data?.FirstOrDefault();
        if (first?.B64Json is not { Length: > 0 } b64)
        {
            throw new InvalidOperationException($"{connection.Name} returned no image data.");
        }

        return new GeneratedImage(Convert.FromBase64String(b64), first.Seed, reply?.CrayonCloud?.Seconds, reply?.CrayonCloud?.Model);
    }

    private static Uri Resolve(LlmConnection connection, string path)
    {
        var baseUrl = connection.BaseUrl.EndsWith('/') ? connection.BaseUrl : connection.BaseUrl + "/";
        return new Uri(new Uri(baseUrl), path);
    }

    private static void Authorize(HttpRequestMessage request, LlmConnection connection)
    {
        if (connection.HasApiKey)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.ApiKey);
        }
    }

    private sealed record WireRequest(
        string Prompt,
        string Size,
        int? Steps,
        int? Seed,
        string? Model)
    {
        [JsonPropertyName("response_format")] public string ResponseFormat => "b64_json";
        public int N => 1;
    }

    private sealed record WireResponse(WireImage[]? Data, [property: JsonPropertyName("crayoncloud")] WireExtras? CrayonCloud);

    private sealed record WireImage([property: JsonPropertyName("b64_json")] string? B64Json, int? Seed);

    private sealed record WireExtras(string? Model, double? Seconds);
}

/// <summary>Finds the configured image generation connection (the first one of that kind) and renders through it.</summary>
public sealed class ImageGenerationService(IConnectionStore connections, ImageGenerationClient client)
{
    public async Task<LlmConnection?> GetConnectionAsync(CancellationToken cancellationToken = default) =>
        (await connections.ListAsync(cancellationToken)).FirstOrDefault(c => c.Kind == BackendKind.ImageGeneration);

    public async Task<GeneratedImage> GenerateAsync(LlmConnection connection, ImageRequest request, CancellationToken cancellationToken = default) =>
        await client.GenerateAsync(connection, request, cancellationToken);
}

/// <summary>"/image a red bicycle" or "/img …" typed into the composer.</summary>
public static class ImageCommand
{
    private static readonly string[] Prefixes = ["/image", "/img", "/imagine"];

    public static bool TryParse(string input, out string prompt)
    {
        var text = input.TrimStart();
        foreach (var prefix in Prefixes)
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && (text.Length == prefix.Length || char.IsWhiteSpace(text[prefix.Length])))
            {
                prompt = text[prefix.Length..].Trim();
                return true;
            }
        }
        prompt = "";
        return false;
    }
}
