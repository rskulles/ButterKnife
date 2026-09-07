using System.Net;
using System.Text.Json;
using ButterKnife.Options;
using ButterKnife.Services;

namespace ButterKnife.Tests;

public class OllamaClientTests
{
    private static readonly ChatMessage[] Messages =
    [
        new(ChatRole.System, "be brief"),
        new(ChatRole.User, "hi"),
    ];

    [Fact]
    public async Task StreamsTokensFromNdjson()
    {
        var handler = StubHandler.Text(
            """
            {"model":"m","message":{"role":"assistant","content":"Hel"},"done":false}
            {"model":"m","message":{"role":"assistant","content":"lo"},"done":false}

            {"model":"m","message":{"role":"assistant","content":""},"done":true,"total_duration":1}
            {"model":"m","message":{"role":"assistant","content":"IGNORED AFTER DONE"},"done":false}
            """, mediaType: "application/x-ndjson");
        var factory = new StubClientFactory(handler, "http://ollama.test:11434");
        var client = new OllamaClient(factory, TestConnections.Make("Ollama", BackendKind.Ollama, "http://ollama.test:11434", "m"));

        var tokens = await client.StreamChatAsync("m", Messages, CancellationToken.None).ToListAsync();

        Assert.Equal(["Hel", "lo"], tokens);
        Assert.Equal(LlmClientBase.HttpClientName, factory.LastName);
        Assert.Equal("http://ollama.test:11434/api/chat", handler.LastRequest!.RequestUri!.ToString());

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("m", body.RootElement.GetProperty("model").GetString());
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        var wire = body.RootElement.GetProperty("messages");
        Assert.Equal("system", wire[0].GetProperty("role").GetString());
        Assert.Equal("user", wire[1].GetProperty("role").GetString());
        Assert.Equal("hi", wire[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task SendsImagesAsBase64Array()
    {
        var handler = StubHandler.Text("""{"message":{"content":"ok"},"done":true}""");
        var client = new OllamaClient(new StubClientFactory(handler, "http://ollama.test:11434"), TestConnections.Make("Ollama", BackendKind.Ollama, "http://ollama.test:11434"));
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47 };

        await client.StreamChatAsync("m", [new(ChatRole.User, "what is this?", [new ChatImage("image/png", png)])], CancellationToken.None).ToListAsync();

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var msg = body.RootElement.GetProperty("messages")[0];
        Assert.Equal("what is this?", msg.GetProperty("content").GetString());
        Assert.Equal(Convert.ToBase64String(png), msg.GetProperty("images")[0].GetString());

        handler = StubHandler.Text("""{"message":{"content":"ok"},"done":true}""");
        client = new OllamaClient(new StubClientFactory(handler, "http://ollama.test:11434"), TestConnections.Make("Ollama", BackendKind.Ollama, "http://ollama.test:11434"));
        await client.StreamChatAsync("m", [new(ChatRole.User, "text only")], CancellationToken.None).ToListAsync();
        using var body2 = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.False(body2.RootElement.GetProperty("messages")[0].TryGetProperty("images", out _), "images must be omitted when absent");
    }

    [Fact]
    public async Task SurfacesInlineErrorFromStream()
    {
        var handler = StubHandler.Text("""{"error":"model 'nope' not found"}""");
        var client = new OllamaClient(new StubClientFactory(handler, "http://ollama.test:11434"), TestConnections.Make("Ollama", BackendKind.Ollama, "http://ollama.test:11434"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.StreamChatAsync("nope", Messages, CancellationToken.None).ToListAsync().AsTask());

        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task ThrowsLlmExceptionOnHttpError()
    {
        var handler = StubHandler.Text("""{"error":"boom"}""", HttpStatusCode.InternalServerError);
        var client = new OllamaClient(new StubClientFactory(handler, "http://ollama.test:11434"), TestConnections.Make("Ollama", BackendKind.Ollama, "http://ollama.test:11434"));

        var ex = await Assert.ThrowsAsync<LlmException>(
            () => client.StreamChatAsync("m", Messages, CancellationToken.None).ToListAsync().AsTask());

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.Contains("boom", ex.Body);
        Assert.Contains("Ollama", ex.Message);
    }

    [Fact]
    public async Task CancellationStopsStreamMidGeneration()
    {
        var handler = StubHandler.NeverEnding(
            """{"message":{"content":"partial"},"done":false}""" + "\n");
        var client = new OllamaClient(new StubClientFactory(handler, "http://ollama.test:11434"), TestConnections.Make("Ollama", BackendKind.Ollama, "http://ollama.test:11434"));

        using var cts = new CancellationTokenSource();
        var received = new List<string>();

        var task = Task.Run(async () =>
        {
            await foreach (var t in client.StreamChatAsync("m", Messages, cts.Token))
            {
                received.Add(t);
                cts.Cancel(); // cancel as soon as the first token arrives; the stream never ends on its own
            }
        }, CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Equal(["partial"], received);
    }

    [Fact]
    public async Task ListsModelsSorted()
    {
        var handler = StubHandler.Text("""{"models":[{"name":"zeta:latest","modified_at":"2024-01-01T00:00:00Z"},{"name":"alpha:8b"}]}""");
        var client = new OllamaClient(new StubClientFactory(handler, "http://ollama.test:11434"), TestConnections.Make("Ollama", BackendKind.Ollama, "http://ollama.test:11434"));

        var models = await client.ListModelsAsync(CancellationToken.None);

        Assert.Equal(["alpha:8b", "zeta:latest"], models);
        Assert.Equal("http://ollama.test:11434/api/tags", handler.LastRequest!.RequestUri!.ToString());
    }
}
