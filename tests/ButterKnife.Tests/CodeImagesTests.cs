using System.Text.Json;
using ButterKnife.Data;
using ButterKnife.Options;
using ButterKnife.Services;

namespace ButterKnife.Tests;

/// <summary>Pictures in the Code tab: attached to prompts, returned by read_file, sent per backend, stored, and dropped for text-only models.</summary>
public sealed class CodeImagesTests : IDisposable
{
    // A 1×1 PNG.
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "butterknife-tests", Guid.NewGuid().ToString("N"));
    private readonly CodeWorkspace _workspace;

    public CodeImagesTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(Path.Combine(_dir, "shot.png"), Png);
        _workspace = new CodeWorkspace(_dir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static ChatImage Image => new("image/png", Png);

    /// <summary>A model that called read_file on a picture and got it back.</summary>
    private static List<ChatMessage> History() =>
    [
        new(ChatRole.User, "What does the screenshot show?"),
        new(ChatRole.Assistant, "Looking.") { ToolCalls = [new ToolCall("c1", CodeTools.ReadFile, """{"path":"shot.png"}""")] },
        new(ChatRole.Tool, "shot.png: image/png. The picture is attached.", [Image]) { ToolCallId = "c1", ToolName = CodeTools.ReadFile },
    ];

    [Fact]
    public async Task ReadFileReturnsPicturesForImageFiles()
    {
        var result = await CodeTools.Prepare(new ToolCall("c", CodeTools.ReadFile, """{"path":"shot.png"}"""), _workspace).RunAsync(null, CancellationToken.None);

        Assert.False(result.IsError);
        var image = Assert.Single(result.Images);
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(Png, image.Data);
        Assert.Single(CodeAgent.ResultMessage(new ToolCall("c", CodeTools.ReadFile, "{}"), result).Images);
    }

    [Fact]
    public async Task ReadFileRefusesHugePictures()
    {
        await File.WriteAllBytesAsync(Path.Combine(_dir, "huge.jpg"), new byte[CodeTools.MaxImageFileBytes + 1]);

        var result = await CodeTools.Prepare(new ToolCall("c", CodeTools.ReadFile, """{"path":"huge.jpg"}"""), _workspace).RunAsync(null, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("smaller copy", result.Output);
    }

    [Fact]
    public void ToolPicturesMoveIntoAFollowingUserMessage()
    {
        var history = History();
        history.Add(new ChatMessage(ChatRole.User, "thanks"));

        var moved = ToolImages.MoveToUserMessages(history);

        Assert.Equal([ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.User, ChatRole.User], moved.Select(m => m.Role));
        Assert.False(moved[2].HasImages);
        Assert.Single(moved[3].Images);
        Assert.StartsWith(ToolImages.Caption, moved[3].Content);
        Assert.Same(history[0], moved[0]);

        List<ChatMessage> plain = [new(ChatRole.User, "hi")];
        Assert.Same(plain, ToolImages.MoveToUserMessages(plain));
    }

    [Fact]
    public async Task OpenAiSendsToolPicturesInAUserMessage()
    {
        var handler = StubHandler.Text("data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"A login form.\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n", mediaType: "text/event-stream");
        var client = new OpenAiCompatibleClient(new StubClientFactory(handler, "http://lmstudio.test/v1"), TestConnections.Make("lm", BackendKind.OpenAiCompatible, "http://lmstudio.test/v1"));

        await foreach (var _ in client.StreamChatAsync("qwen-vl", History(), null, CancellationToken.None))
        {
        }

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal(4, messages.GetArrayLength());
        Assert.Equal(JsonValueKind.String, messages[2].GetProperty("content").ValueKind); // the tool message stays text
        Assert.Equal("user", messages[3].GetProperty("role").GetString());
        var parts = messages[3].GetProperty("content");
        Assert.Equal("text", parts[0].GetProperty("type").GetString());
        Assert.StartsWith("data:image/png;base64,", parts[1].GetProperty("image_url").GetProperty("url").GetString());
    }

    [Fact]
    public async Task OllamaSendsToolPicturesOnAUserMessage()
    {
        var handler = StubHandler.Text("""{"message":{"role":"assistant","content":"ok"},"done":true,"done_reason":"stop"}""", mediaType: "application/x-ndjson");
        var client = new OllamaClient(new StubClientFactory(handler, "http://ollama.test"), TestConnections.Make("o", BackendKind.Ollama, "http://ollama.test"));

        await foreach (var _ in client.StreamChatAsync("qwen2.5vl", History(), null, CancellationToken.None))
        {
        }

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var messages = body.RootElement.GetProperty("messages");
        Assert.False(messages[2].TryGetProperty("images", out _));
        Assert.Equal("user", messages[3].GetProperty("role").GetString());
        Assert.Equal(1, messages[3].GetProperty("images").GetArrayLength());
    }

    [Fact]
    public async Task AnthropicKeepsToolPicturesInsideTheToolResult()
    {
        var handler = StubHandler.Text(
            """
            event: message_start
            data: {"type":"message_start","message":{"id":"m","type":"message","role":"assistant","model":"claude","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":1}}}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":1}}

            event: message_stop
            data: {"type":"message_stop"}

            """, mediaType: "text/event-stream");
        var client = new AnthropicLlmClient(TestConnections.Make("c", BackendKind.Anthropic, "http://anthropic.test", apiKey: "k"), new HttpClient(handler, disposeHandler: false));

        await foreach (var _ in client.StreamChatAsync("claude", History(), null, CancellationToken.None))
        {
        }

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength());
        var content = messages[2].GetProperty("content")[0].GetProperty("content");
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("image", content[1].GetProperty("type").GetString());
    }

    [Fact]
    public void OldPictureResultsAreClearedWhenOverBudget()
    {
        var history = History();
        history.Add(new ChatMessage(ChatRole.Assistant, "") { ToolCalls = [new ToolCall("c2", CodeTools.ListFiles, "{}")] });
        history.Add(new ChatMessage(ChatRole.Tool, "shot.png\n") { ToolCallId = "c2", ToolName = CodeTools.ListFiles });

        var roomy = CodeCompactor.ClipOldResults(history, budgetChars: 100_000);
        var tight = CodeCompactor.ClipOldResults(history, budgetChars: 1_000);

        Assert.Single(roomy[2].Images);
        Assert.False(tight[2].HasImages);
        Assert.Contains("(a picture) was cleared", tight[2].Content);
    }

    [Fact]
    public async Task PicturesAreStoredWithTheirMessages()
    {
        var store = new SqliteCodeSessionStore(new SqliteDatabase(Microsoft.Extensions.Options.Options.Create(new DatabaseOptions { ConnectionString = $"Data Source={Path.Combine(_dir, "db", "test.db")}" })));
        var me = Guid.NewGuid();
        var session = await store.CreateAsync(me, "Pictures", _dir, Guid.NewGuid(), "m", CodePermissionMode.Ask, CancellationToken.None);
        foreach (var message in History())
        {
            await store.AppendAsync(session.Id, message, cancellationToken: CancellationToken.None);
        }
        await store.AppendAsync(session.Id, new ChatMessage(ChatRole.User, "", [Image, Image]), cancellationToken: CancellationToken.None);

        var entries = (await store.GetAsync(session.Id, me, CancellationToken.None))!.Entries;

        Assert.Equal([0, 0, 1, 2], entries.Select(e => e.Message.Images.Count));
        Assert.Equal(Png, entries[2].Message.Images[0].Data);

        await store.DeleteAsync(session.Id, CancellationToken.None); // pictures go with their messages (cascade)
    }
}
