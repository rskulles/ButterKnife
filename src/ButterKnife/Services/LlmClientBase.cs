using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace ButterKnife.Services;

/// <summary>Shared plumbing: named HttpClient per request, error surfacing, line-oriented streaming.</summary>
public abstract class LlmClientBase(IHttpClientFactory httpClientFactory, string backendName, string? defaultModel) : ILlmClient
{
    protected static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string BackendName { get; } = backendName;

    public string? DefaultModel { get; } = defaultModel;

    public abstract IAsyncEnumerable<string> StreamChatAsync(
        string model,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default);

    public abstract Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default);

    /// <summary>Create a fresh client from the factory each call so handler rotation (DNS, etc.) keeps working.</summary>
    protected HttpClient CreateClient() => httpClientFactory.CreateClient(BackendName);

    protected async Task<HttpResponseMessage> PostStreamingAsync<TBody>(
        string relativeUrl,
        TBody body,
        CancellationToken cancellationToken)
    {
        var client = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, relativeUrl)
        {
            // Serialise up front so the request carries a Content-Length instead of a chunked body;
            // some thin proxies and minimal servers cannot read chunked request bodies.
            Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json"),
        };

        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return response;
    }

    protected async Task<T> GetJsonAsync<T>(string relativeUrl, CancellationToken cancellationToken)
    {
        var client = CreateClient();
        using var response = await client.GetAsync(relativeUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
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
