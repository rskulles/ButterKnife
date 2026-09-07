using System.Net;
using System.Text.Json;
using ButterKnife.Options;
using ButterKnife.Services;

namespace ButterKnife.Tests;

public class OpenAiCompatibleClientTests
{
    private static readonly ChatMessage[] Messages = [new(ChatRole.User, "hi")];

    [Fact]
    public async Task StreamsTokensFromSse()
    {
        var handler = StubHandler.Text(
            """
            : keep-alive comment
            data: {"id":"1","choices":[{"delta":{"role":"assistant"},"index":0}]}

            data: {"id":"1","choices":[{"delta":{"content":"Hel"},"index":0}]}

            data:{"id":"1","choices":[{"delta":{"content":"lo"},"index":0}]}

            data: {"id":"1","choices":[{"delta":{},"finish_reason":"stop","index":0}]}

            data: [DONE]

            data: {"id":"1","choices":[{"delta":{"content":"IGNORED"},"index":0}]}
            """, mediaType: "text/event-stream");
        var factory = new StubClientFactory(handler, "http://lmstudio.test:1234/v1");
        var client = new OpenAiCompatibleClient(factory, TestConnections.Make("LM Studio", BackendKind.OpenAiCompatible, "http://lmstudio.test:1234/v1"));

        var tokens = await client.StreamChatAsync("m", Messages, CancellationToken.None).ToListAsync();

        Assert.Equal(["Hel", "lo"], tokens);
        Assert.Equal("http://lmstudio.test:1234/v1/chat/completions", handler.LastRequest!.RequestUri!.ToString());

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("user", body.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
    }

    [Fact]
    public async Task SendsImagesAsContentParts()
    {
        var handler = StubHandler.Text("data: [DONE]\n", mediaType: "text/event-stream");
        var client = new OpenAiCompatibleClient(new StubClientFactory(handler, "http://x.test/v1"), TestConnections.Make("X", BackendKind.OpenAiCompatible, "http://x.test/v1"));
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF };

        await client.StreamChatAsync("m",
            [new(ChatRole.User, "describe", [new ChatImage("image/jpeg", jpeg)]), new(ChatRole.Assistant, "a cat")],
            CancellationToken.None).ToListAsync();

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var messages = body.RootElement.GetProperty("messages");
        var parts = messages[0].GetProperty("content");
        Assert.Equal(JsonValueKind.Array, parts.ValueKind);
        Assert.Equal("text", parts[0].GetProperty("type").GetString());
        Assert.Equal("describe", parts[0].GetProperty("text").GetString());
        Assert.Equal("image_url", parts[1].GetProperty("type").GetString());
        Assert.Equal($"data:image/jpeg;base64,{Convert.ToBase64String(jpeg)}", parts[1].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal(JsonValueKind.String, messages[1].GetProperty("content").ValueKind);
    }

    [Fact]
    public async Task SurfacesInlineErrorFromStream()
    {
        var handler = StubHandler.Text("""data: {"error":{"message":"context length exceeded","type":"invalid_request_error"}}""" + "\n");
        var client = new OpenAiCompatibleClient(new StubClientFactory(handler, "http://x.test/v1"), TestConnections.Make("X", BackendKind.OpenAiCompatible, "http://x.test/v1"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.StreamChatAsync("m", Messages, CancellationToken.None).ToListAsync().AsTask());

        Assert.Contains("context length exceeded", ex.Message);
    }

    [Fact]
    public async Task ThrowsLlmExceptionOnHttpError()
    {
        var handler = StubHandler.Text("""{"error":{"message":"no such model"}}""", HttpStatusCode.NotFound);
        var client = new OpenAiCompatibleClient(new StubClientFactory(handler, "http://x.test/v1"), TestConnections.Make("X", BackendKind.OpenAiCompatible, "http://x.test/v1"));

        var ex = await Assert.ThrowsAsync<LlmException>(
            () => client.StreamChatAsync("m", Messages, CancellationToken.None).ToListAsync().AsTask());

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task SendsBearerTokenWhenApiKeyIsSet()
    {
        var handler = StubHandler.Text("""{"data":[]}""");
        var client = new OpenAiCompatibleClient(new StubClientFactory(handler, "http://x.test/v1"),
            TestConnections.Make("X", BackendKind.OpenAiCompatible, "http://x.test/v1", apiKey: "sk-secret"));

        await client.ListModelsAsync(CancellationToken.None);

        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("sk-secret", handler.LastRequest.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task ListsModelsSorted()
    {
        var handler = StubHandler.Text("""{"object":"list","data":[{"id":"qwen2.5","object":"model"},{"id":"Llama-3","object":"model"}]}""");
        var client = new OpenAiCompatibleClient(new StubClientFactory(handler, "http://x.test/v1"), TestConnections.Make("X", BackendKind.OpenAiCompatible, "http://x.test/v1"));

        var models = await client.ListModelsAsync(CancellationToken.None);

        Assert.Equal(["Llama-3", "qwen2.5"], models);
        Assert.Equal("http://x.test/v1/models", handler.LastRequest!.RequestUri!.ToString());
    }
}
