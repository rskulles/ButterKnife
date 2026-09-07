using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ButterKnife.Data;

namespace ButterKnife.Services;

/// <summary>Shared plumbing for the hand-rolled HTTP clients: absolute URIs from the connection's BaseUrl, auth, error surfacing, line streaming.</summary>
public abstract class LlmClientBase(IHttpClientFactory httpClientFactory, LlmConnection connection) : ILlmClient
{
    /// <summary>The single named HttpClient every HTTP-based connection shares; configured with an infinite timeout.</summary>
    public const string HttpClientName = "llm";

    protected static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    protected LlmConnection Connection { get; } = connection;

    public Guid ConnectionId => Connection.Id;

    public string BackendName => Connection.Name;

    public string? DefaultModel => Connection.DefaultModel;

    public abstract IAsyncEnumerable<string> StreamChatAsync(
        string model,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default);

    public abstract Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default);

    /// <summary>Relative request paths ("api/chat") resolve against BaseUrl; the base always ends with "/" so its last segment is kept.</summary>
    protected Uri Resolve(string relativeUrl)
    {
        var baseUrl = Connection.BaseUrl.EndsWith('/') ? Connection.BaseUrl : Connection.BaseUrl + "/";
        return new Uri(new Uri(baseUrl, UriKind.Absolute), relativeUrl);
    }

    protected HttpRequestMessage NewRequest(HttpMethod method, string relativeUrl)
    {
        var request = new HttpRequestMessage(method, Resolve(relativeUrl));
        if (Connection.HasApiKey)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Connection.ApiKey);
        }
        return request;
    }

    /// <summary>Create a fresh client from the factory each call so handler rotation (DNS, etc.) keeps working.</summary>
    protected HttpClient CreateClient() => httpClientFactory.CreateClient(HttpClientName);

    protected async Task<HttpResponseMessage> PostStreamingAsync<TBody>(
        string relativeUrl,
        TBody body,
        CancellationToken cancellationToken)
    {
        var client = CreateClient();
        using var request = NewRequest(HttpMethod.Post, relativeUrl);
        // Serialise up front so the request carries a Content-Length instead of a chunked body;
        // some thin proxies and minimal servers cannot read chunked request bodies.
        request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");

        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return response;
    }

    protected async Task<T> GetJsonAsync<T>(string relativeUrl, CancellationToken cancellationToken)
    {
        var client = CreateClient();
        using var request = NewRequest(HttpMethod.Get, relativeUrl);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
               ?? throw new InvalidOperationException($"{BackendName}: empty response from {relativeUrl}");
    }

    protected static async IAsyncEnumerable<string> ReadLinesAsync(
        HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using (response)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                yield return line;
            }
        }
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.Dispose();
        throw new LlmException(BackendName, response.StatusCode, body);
    }
}
