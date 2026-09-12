using System.Text.Json;
using ButterKnife.Options;
using ButterKnife.Services;

namespace ButterKnife.Tests;

public class ContextTests
{
    private static readonly ChatMessage[] Messages = [new(ChatRole.User, "hi")];

    // ---------- usage reporting ----------

    [Fact]
    public async Task Ollama_ReportsUsageFromDoneChunk_AndSendsNumCtxWhenConfigured()
    {
        var handler = StubHandler.Text(
            """
            {"message":{"content":"a"},"done":false}
            {"message":{"content":""},"done":true,"prompt_eval_count":120,"eval_count":7,"prompt_eval_duration":250000000,"eval_duration":1400000000}
            """);
        var client = new OllamaClient(new StubClientFactory(handler, "http://ollama.test:11434"),
            TestConnections.Make("Ollama", BackendKind.Ollama, "http://ollama.test:11434", contextWindow: 16384));

        var usage = await client.StreamChatAsync("m", Messages, CancellationToken.None).LastUsageAsync();

        Assert.Equal(new TokenUsage(120, 7, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(1400)), usage);
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal(16384, body.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32());
    }

    [Fact]
    public async Task Ollama_OmitsOptionsWithoutConfiguredWindow()
    {
        var handler = StubHandler.Text("""{"message":{"content":""},"done":true}""");
        var client = new OllamaClient(new StubClientFactory(handler, "http://ollama.test:11434"),
            TestConnections.Make("Ollama", BackendKind.Ollama, "http://ollama.test:11434"));

        var usage = await client.StreamChatAsync("m", Messages, CancellationToken.None).LastUsageAsync();

        Assert.Null(usage);
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.False(body.RootElement.TryGetProperty("options", out _));
    }

    [Fact]
    public async Task OpenAi_RequestsUsageAndParsesFinalChunk()
    {
        var handler = StubHandler.Text(
            """
            data: {"choices":[{"delta":{"content":"a"},"index":0}]}

            data: {"choices":[],"usage":{"prompt_tokens":88,"completion_tokens":5,"total_tokens":93}}

            data: [DONE]
            """, mediaType: "text/event-stream");
        var client = new OpenAiCompatibleClient(new StubClientFactory(handler, "http://x.test/v1"),
            TestConnections.Make("X", BackendKind.OpenAiCompatible, "http://x.test/v1"));

        var usage = await client.StreamChatAsync("m", Messages, CancellationToken.None).LastUsageAsync();

        Assert.Equal(new TokenUsage(88, 5), usage);
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.True(body.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
    }

    [Fact]
    public async Task Anthropic_ReportsUsageFromStartAndDeltaEvents()
    {
        var handler = StubHandler.Text(
            """
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":410,"output_tokens":1}}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"a"}}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":42}}

            event: message_stop
            data: {"type":"message_stop"}

            """, mediaType: "text/event-stream");
        var client = new AnthropicLlmClient(
            TestConnections.Make("Claude", BackendKind.Anthropic, "http://anthropic.test", apiKey: "k"),
            new HttpClient(handler, disposeHandler: false));

        var usage = await client.StreamChatAsync("claude-opus-5", Messages, CancellationToken.None).LastUsageAsync();

        Assert.Equal(new TokenUsage(410, 42), usage);
    }

    // ---------- context window lookup ----------

    [Fact]
    public async Task Ollama_PrefersLoadedContextLength_ThenModelInfo_ThenNull()
    {
        var routes = new Dictionary<string, (string, string)>
        {
            ["GET /api/ps"] = ("""{"models":[{"name":"llama3:8b","model":"llama3:8b","context_length":8192}]}""", "application/json"),
            ["POST /api/show"] = ("""{"model_info":{"general.architecture":"llama","llama.context_length":131072}}""", "application/json"),
        };
        var client = new OllamaClient(new StubClientFactory(StubHandler.Route(routes), "http://ollama.test:11434"),
            TestConnections.Make("Ollama", BackendKind.Ollama, "http://ollama.test:11434"));

        Assert.Equal(8192, await client.GetContextWindowAsync("llama3:8b", CancellationToken.None));
        Assert.Equal(131072, await client.GetContextWindowAsync("other:latest", CancellationToken.None));

        var none = new OllamaClient(new StubClientFactory(StubHandler.Route(new Dictionary<string, (string, string)>()), "http://ollama.test:11434"),
            TestConnections.Make("Ollama", BackendKind.Ollama, "http://ollama.test:11434"));
        Assert.Null(await none.GetContextWindowAsync("x", CancellationToken.None));

        var configured = new OllamaClient(new StubClientFactory(StubHandler.Route(routes), "http://ollama.test:11434"),
            TestConnections.Make("Ollama", BackendKind.Ollama, "http://ollama.test:11434", contextWindow: 4096));
        Assert.Equal(4096, await configured.GetContextWindowAsync("llama3:8b", CancellationToken.None));
    }

    [Fact]
    public async Task OpenAi_ReadsLmStudioNativeApiAtServerRoot()
    {
        var routes = new Dictionary<string, (string, string)>
        {
            ["GET /api/v0/models/qwen"] = ("""{"id":"qwen","type":"llm","max_context_length":40960}""", "application/json"),
        };
        var client = new OpenAiCompatibleClient(new StubClientFactory(StubHandler.Route(routes), "http://lm.test:1234/v1"),
            TestConnections.Make("LM Studio", BackendKind.OpenAiCompatible, "http://lm.test:1234/v1"));

        Assert.Equal(40960, await client.GetContextWindowAsync("qwen", CancellationToken.None));
        Assert.Null(await client.GetContextWindowAsync("unknown", CancellationToken.None));
    }

    [Fact]
    public async Task Anthropic_ReadsMaxInputTokensFromModelsApi()
    {
        var routes = new Dictionary<string, (string, string)>
        {
            ["GET /v1/models/claude-opus-5"] = ("""{"id":"claude-opus-5","display_name":"Claude Opus 5","created_at":"2026-04-01T00:00:00Z","type":"model","max_input_tokens":1000000,"max_tokens":128000}""", "application/json"),
        };
        var client = new AnthropicLlmClient(
            TestConnections.Make("Claude", BackendKind.Anthropic, "http://anthropic.test", apiKey: "k"),
            new HttpClient(StubHandler.Route(routes), disposeHandler: false));

        Assert.Equal(1000000, await client.GetContextWindowAsync("claude-opus-5", CancellationToken.None));
        Assert.Null(await client.GetContextWindowAsync("nope", CancellationToken.None));
    }

    // ---------- image support ----------

    [Fact]
    public async Task Ollama_ReadsVisionCapabilityFromShow_NullWhenAbsent()
    {
        var vision = new OllamaClient(new StubClientFactory(StubHandler.Text("""{"capabilities":["completion","vision"],"model_info":{}}"""), "http://ollama.test:11434"),
            TestConnections.Make("Ollama", BackendKind.Ollama, "http://ollama.test:11434"));
        Assert.True(await vision.SupportsImagesAsync("gemma3", CancellationToken.None));

        var textOnly = new OllamaClient(new StubClientFactory(StubHandler.Text("""{"capabilities":["completion","tools"],"model_info":{}}"""), "http://ollama.test:11434"),
            TestConnections.Make("Ollama", BackendKind.Ollama, "http://ollama.test:11434"));
        Assert.False(await textOnly.SupportsImagesAsync("llama3", CancellationToken.None));

        var old = new OllamaClient(new StubClientFactory(StubHandler.Text("""{"model_info":{"llama.context_length":8192}}"""), "http://ollama.test:11434"),
            TestConnections.Make("Ollama", BackendKind.Ollama, "http://ollama.test:11434"));
        Assert.Null(await old.SupportsImagesAsync("llama3", CancellationToken.None));

        var down = new OllamaClient(new StubClientFactory(StubHandler.Route(new Dictionary<string, (string, string)>()), "http://ollama.test:11434"),
            TestConnections.Make("Ollama", BackendKind.Ollama, "http://ollama.test:11434"));
        Assert.Null(await down.SupportsImagesAsync("llama3", CancellationToken.None));
    }

    [Fact]
    public async Task OpenAi_ReadsLmStudioModelType_NullElsewhere()
    {
        var routes = new Dictionary<string, (string, string)>
        {
            ["GET /api/v0/models/qwen-vl"] = ("""{"id":"qwen-vl","type":"vlm","max_context_length":32768}""", "application/json"),
            ["GET /api/v0/models/qwen"] = ("""{"id":"qwen","type":"llm","max_context_length":40960}""", "application/json"),
        };
        var client = new OpenAiCompatibleClient(new StubClientFactory(StubHandler.Route(routes), "http://lm.test:1234/v1"),
            TestConnections.Make("LM Studio", BackendKind.OpenAiCompatible, "http://lm.test:1234/v1"));

        Assert.True(await client.SupportsImagesAsync("qwen-vl", CancellationToken.None));
        Assert.False(await client.SupportsImagesAsync("qwen", CancellationToken.None));
        Assert.Null(await client.SupportsImagesAsync("unknown", CancellationToken.None));
    }

    [Fact]
    public async Task Anthropic_AlwaysSupportsImages()
    {
        var client = new AnthropicLlmClient(
            TestConnections.Make("Claude", BackendKind.Anthropic, "http://anthropic.test", apiKey: "k"),
            new HttpClient(StubHandler.Route(new Dictionary<string, (string, string)>()), disposeHandler: false));

        Assert.True(await client.SupportsImagesAsync("claude-opus-5", CancellationToken.None));
    }

    [Fact]
    public void WithImagesAsText_ReplacesImagesWithNote_LeavesTextOnlyMessagesAlone()
    {
        var image = new ChatImage("image/png", [1, 2, 3]);
        var withText = new ChatMessage(ChatRole.User, "what is this?", [image]).WithImagesAsText();
        Assert.False(withText.HasImages);
        Assert.Equal("what is this?\n\n[1 image was attached here but omitted: this model cannot see images.]", withText.Content);

        var imagesOnly = new ChatMessage(ChatRole.User, "", [image, image]).WithImagesAsText();
        Assert.Equal("[2 images were attached here but omitted: this model cannot see images.]", imagesOnly.Content);

        var plain = new ChatMessage(ChatRole.Assistant, "hi");
        Assert.Same(plain, plain.WithImagesAsText());
    }

    // ---------- stats readout ----------

    [Fact]
    public void GenerationStats_PrefersBackendTimings_ElseClientSide()
    {
        var backend = new GenerationStats(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(0.5), 40,
            new TokenUsage(1000, 60, TimeSpan.FromSeconds(0.4), TimeSpan.FromSeconds(2)));
        Assert.Equal(2500, backend.PromptTokensPerSecond!.Value, 0.01);
        Assert.Equal(30, backend.GenerationTokensPerSecond!.Value, 0.01);

        var clientSide = new GenerationStats(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1), 41, new TokenUsage(500, null));
        Assert.Equal(500, clientSide.PromptTokensPerSecond!.Value, 0.01);  // prompt tokens over time-to-first-token
        Assert.Equal(20, clientSide.GenerationTokensPerSecond!.Value, 0.01); // (41-1) chunks over the 2s after the first token

        var early = new GenerationStats(TimeSpan.FromSeconds(0.2), null, 0, null);
        Assert.Null(early.PromptTokensPerSecond);
        Assert.Null(early.GenerationTokensPerSecond);
    }

    // ---------- estimation ----------

    [Fact]
    public void Estimator_CountsTextAndImages()
    {
        var text = new string('x', 400);
        Assert.Equal(100, TokenEstimator.Estimate(text));
        Assert.Equal(TokenEstimator.TokensPerMessageOverhead + 100, TokenEstimator.Estimate(new ChatMessage(ChatRole.User, text)));
        Assert.Equal(TokenEstimator.TokensPerMessageOverhead + 100 + TokenEstimator.TokensPerImage,
            TokenEstimator.Estimate(new ChatMessage(ChatRole.User, text, [new ChatImage("image/png", [1])])));
        Assert.Equal(0, TokenEstimator.Estimate(""));
    }
}
