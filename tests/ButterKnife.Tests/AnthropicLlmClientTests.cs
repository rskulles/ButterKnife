using System.Text.Json;
using ButterKnife.Options;
using ButterKnife.Services;

namespace ButterKnife.Tests;

public class AnthropicLlmClientTests
{
    private static AnthropicLlmClient Make(StubHandler handler, string? apiKey = "sk-ant-test") =>
        new(TestConnections.Make("Claude", BackendKind.Anthropic, "http://anthropic.test", apiKey: apiKey),
            new HttpClient(handler, disposeHandler: false));

    [Fact]
    public async Task StreamsTextDeltasAndFoldsSystemPrompt()
    {
        var handler = StubHandler.Text(
            """
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":1}}}

            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hel"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"lo"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":0}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":2}}

            event: message_stop
            data: {"type":"message_stop"}

            """, mediaType: "text/event-stream");
        var client = Make(handler);

        var tokens = await client.StreamChatAsync("claude-opus-5",
            [new(ChatRole.System, "Be terse."), new(ChatRole.User, "hi"), new(ChatRole.Assistant, "hey"), new(ChatRole.User, "again")],
            CancellationToken.None).ToTextListAsync();

        Assert.Equal(["Hel", "lo"], tokens);
        Assert.Equal("http://anthropic.test/v1/messages", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("sk-ant-test", handler.LastRequest.Headers.GetValues("x-api-key").Single());
        Assert.True(handler.LastRequest.Headers.Contains("anthropic-version"));

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var root = body.RootElement;
        Assert.Equal("claude-opus-5", root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal("Be terse.", root.GetProperty("system").GetString());
        var messages = root.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
        Assert.DoesNotContain(messages.EnumerateArray(), m => m.GetProperty("role").GetString() == "system");
    }

    [Fact]
    public async Task SendsImagesAsBase64Blocks()
    {
        var handler = StubHandler.Text("event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n", mediaType: "text/event-stream");
        var client = Make(handler);
        var webp = new byte[] { 0x52, 0x49, 0x46, 0x46 };

        await client.StreamChatAsync("claude-opus-5",
            [new(ChatRole.User, "what's here?", [new ChatImage("image/webp", webp)])],
            CancellationToken.None).ToTextListAsync();

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var content = body.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Equal("image", content[0].GetProperty("type").GetString());
        var source = content[0].GetProperty("source");
        Assert.Equal("base64", source.GetProperty("type").GetString());
        Assert.Equal("image/webp", source.GetProperty("media_type").GetString());
        Assert.Equal(Convert.ToBase64String(webp), source.GetProperty("data").GetString());
        Assert.Equal("text", content[1].GetProperty("type").GetString());
        Assert.Equal("what's here?", content[1].GetProperty("text").GetString());
    }

    [Fact]
    public async Task ListsModelIds()
    {
        var handler = StubHandler.Text(
            """{"data":[{"id":"claude-sonnet-5","display_name":"Claude Sonnet 5","created_at":"2026-03-01T00:00:00Z","type":"model"},{"id":"claude-opus-5","display_name":"Claude Opus 5","created_at":"2026-04-01T00:00:00Z","type":"model"}],"has_more":false,"first_id":"claude-sonnet-5","last_id":"claude-opus-5"}""");
        var client = Make(handler);

        var models = await client.ListModelsAsync(CancellationToken.None);

        Assert.Equal(["claude-opus-5", "claude-sonnet-5"], models);
        Assert.StartsWith("http://anthropic.test/v1/models", handler.LastRequest!.RequestUri!.ToString());
    }
}
