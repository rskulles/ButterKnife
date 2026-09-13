using System.Text.Json;
using ButterKnife.Data;
using ButterKnife.Options;
using ButterKnife.Services;

namespace ButterKnife.Tests;

/// <summary>How per-chat sampling settings reach each backend, and how they are stored.</summary>
public sealed class ChatOptionsTests : IDisposable
{
    private static readonly ChatMessage[] Messages = [new(ChatRole.User, "hi")];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "butterknife-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void NormalisesValues()
    {
        Assert.Equal(0.3, ChatOptions.NormalizeTemperature(0.30000000000000004));
        Assert.Equal(2, ChatOptions.NormalizeTemperature(7));
        Assert.Equal(0, ChatOptions.NormalizeTemperature(-1));
        Assert.Null(ChatOptions.NormalizeTemperature(null));
        Assert.Null(ChatOptions.NormalizeMaxTokens(0));
        Assert.Equal(50, ChatOptions.NormalizeMaxTokens(50));
        Assert.True(ChatOptions.Default.IsDefault);
        Assert.False(new ChatOptions(Think: false).IsDefault);
    }

    [Fact]
    public async Task OllamaSendsOptionsAndThinkOnlyWhenSet()
    {
        var handler = StubHandler.Text("""{"model":"m","message":{"role":"assistant","content":"ok"},"done":true}""", mediaType: "application/x-ndjson");
        var client = new OllamaClient(new StubClientFactory(handler, "http://ollama.test:11434"), TestConnections.Make("Ollama", BackendKind.Ollama, "http://ollama.test:11434"));

        await client.StreamChatAsync("m", Messages, null, CancellationToken.None).ToTextListAsync();
        var plain = JsonDocument.Parse(handler.LastRequestBody!).RootElement;
        Assert.False(plain.TryGetProperty("options", out _));
        Assert.False(plain.TryGetProperty("think", out _));

        await client.StreamChatAsync("m", Messages, new ChatOptions(0.2, 128, false), CancellationToken.None).ToTextListAsync();
        var tuned = JsonDocument.Parse(handler.LastRequestBody!).RootElement;
        Assert.Equal(0.2, tuned.GetProperty("options").GetProperty("temperature").GetDouble());
        Assert.Equal(128, tuned.GetProperty("options").GetProperty("num_predict").GetInt32());
        Assert.False(tuned.GetProperty("options").TryGetProperty("num_ctx", out _));
        Assert.False(tuned.GetProperty("think").GetBoolean());
    }

    [Fact]
    public async Task OllamaKeepsNumCtxBesideSamplingOptions()
    {
        var handler = StubHandler.Text("""{"model":"m","message":{"role":"assistant","content":"ok"},"done":true}""", mediaType: "application/x-ndjson");
        var client = new OllamaClient(new StubClientFactory(handler, "http://ollama.test:11434"), TestConnections.Make("Ollama", BackendKind.Ollama, "http://ollama.test:11434", contextWindow: 8192));

        await client.StreamChatAsync("m", Messages, new ChatOptions(Temperature: 1.5), CancellationToken.None).ToTextListAsync();

        var options = JsonDocument.Parse(handler.LastRequestBody!).RootElement.GetProperty("options");
        Assert.Equal(8192, options.GetProperty("num_ctx").GetInt32());
        Assert.Equal(1.5, options.GetProperty("temperature").GetDouble());
        Assert.False(options.TryGetProperty("num_predict", out _));
    }

    [Fact]
    public async Task OpenAiCompatibleSendsStandardFieldsAndTemplateKwargsForThinking()
    {
        var handler = StubHandler.Text("data: [DONE]\n\n", mediaType: "text/event-stream");
        var client = new OpenAiCompatibleClient(new StubClientFactory(handler, "http://lmstudio.test:1234/v1"), TestConnections.Make("LM Studio", BackendKind.OpenAiCompatible, "http://lmstudio.test:1234/v1"));

        await client.StreamChatAsync("m", Messages, null, CancellationToken.None).ToTextListAsync();
        var plain = JsonDocument.Parse(handler.LastRequestBody!).RootElement;
        Assert.False(plain.TryGetProperty("temperature", out _));
        Assert.False(plain.TryGetProperty("max_tokens", out _));
        Assert.False(plain.TryGetProperty("chat_template_kwargs", out _));

        await client.StreamChatAsync("m", Messages, new ChatOptions(0.7, 256, true), CancellationToken.None).ToTextListAsync();
        var tuned = JsonDocument.Parse(handler.LastRequestBody!).RootElement;
        Assert.Equal(0.7, tuned.GetProperty("temperature").GetDouble());
        Assert.Equal(256, tuned.GetProperty("max_tokens").GetInt32());
        Assert.True(tuned.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
    }

    [Fact]
    public async Task AnthropicSendsMaxTokensAndThinkingButNeverTemperature()
    {
        var handler = StubHandler.Text(
            """
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":1}}}

            event: message_stop
            data: {"type":"message_stop"}

            """, mediaType: "text/event-stream");
        var client = new AnthropicLlmClient(TestConnections.Make("Claude", BackendKind.Anthropic, "http://anthropic.test", apiKey: "sk-ant-test"), new HttpClient(handler, disposeHandler: false));

        await client.StreamChatAsync("claude-opus-5", Messages, new ChatOptions(0.5, 4000, true), CancellationToken.None).ToTextListAsync();

        var body = JsonDocument.Parse(handler.LastRequestBody!).RootElement;
        Assert.Equal(4000, body.GetProperty("max_tokens").GetInt32());
        Assert.Equal("enabled", body.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal(2000, body.GetProperty("thinking").GetProperty("budget_tokens").GetInt32());
        Assert.False(body.TryGetProperty("temperature", out _));

        await client.StreamChatAsync("claude-opus-5", Messages, new ChatOptions(Think: false), CancellationToken.None).ToTextListAsync();
        body = JsonDocument.Parse(handler.LastRequestBody!).RootElement;
        Assert.Equal("disabled", body.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal(64000, body.GetProperty("max_tokens").GetInt32());

        await client.StreamChatAsync("claude-opus-5", Messages, null, CancellationToken.None).ToTextListAsync();
        Assert.False(JsonDocument.Parse(handler.LastRequestBody!).RootElement.TryGetProperty("thinking", out _));
    }

    [Fact]
    public async Task StoreRoundTripsOptionsAndBranchesCopyThem()
    {
        var db = new SqliteDatabase(Microsoft.Extensions.Options.Options.Create(new DatabaseOptions { ConnectionString = $"Data Source={Path.Combine(_dir, "test.db")}" }));
        var store = new SqliteConversationStore(db);
        var created = await store.CreateAsync("t", Guid.NewGuid(), "m", null, CancellationToken.None);
        Assert.True((await store.GetAsync(created.Id, CancellationToken.None))!.Options.IsDefault);

        await store.SetOptionsAsync(created.Id, new ChatOptions(0.30000000000000004, 512, false), CancellationToken.None);
        var loaded = (await store.GetAsync(created.Id, CancellationToken.None))!.Options;
        Assert.Equal(new ChatOptions(0.3, 512, false), loaded);

        var messageId = await store.AppendMessageAsync(created.Id, new ChatMessage(ChatRole.User, "hi"), CancellationToken.None);
        var branch = await store.BranchAsync(created.Id, messageId, "b", CancellationToken.None);
        Assert.Equal(loaded, branch.Options);

        await store.SetOptionsAsync(created.Id, ChatOptions.Default, CancellationToken.None);
        Assert.True((await store.GetAsync(created.Id, CancellationToken.None))!.Options.IsDefault);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
