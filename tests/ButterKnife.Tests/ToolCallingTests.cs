using System.Text.Json;
using ButterKnife.Options;
using ButterKnife.Services;

namespace ButterKnife.Tests;

/// <summary>Tool calling on the wire, per backend: tools in the request, calls assembled from the stream, results sent back.</summary>
public class ToolCallingTests
{
    private static readonly ToolDefinition ReadTool = ToolDefinition.Create("read_file", "Read a file",
        """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""");

    private static readonly ChatOptions WithTools = ChatOptions.Default with { Tools = [ReadTool] };

    /// <summary>A conversation in which the model already called read_file once and got its result.</summary>
    private static List<ChatMessage> History() =>
    [
        new(ChatRole.System, "You are a coding agent."),
        new(ChatRole.User, "What is in a.txt?"),
        new(ChatRole.Assistant, "Let me look.") { ToolCalls = [new ToolCall("call_1", "read_file", """{"path":"a.txt"}""")] },
        new(ChatRole.Tool, "     1\thello") { ToolCallId = "call_1", ToolName = "read_file" },
    ];

    private static async Task<(List<string> Text, List<ToolCall> Calls, List<FinishReason> Finish)> CollectAsync(IAsyncEnumerable<ChatDelta> stream)
    {
        var text = new List<string>();
        var calls = new List<ToolCall>();
        var finish = new List<FinishReason>();
        await foreach (var delta in stream)
        {
            if (delta.Text is { } t)
            {
                text.Add(t);
            }
            if (delta.ToolCall is { } c)
            {
                calls.Add(c);
            }
            if (delta.Finish is { } f)
            {
                finish.Add(f);
            }
        }
        return (text, calls, finish);
    }

    [Fact]
    public async Task OllamaSendsToolsAndHistoryAndParsesCalls()
    {
        var handler = StubHandler.Text(
            """
            {"message":{"role":"assistant","content":"Reading."},"done":false}
            {"message":{"role":"assistant","content":"","tool_calls":[{"function":{"name":"read_file","arguments":{"path":"b.txt"}}}]},"done":false}
            {"message":{"role":"assistant","content":""},"done":true,"done_reason":"stop","prompt_eval_count":50,"eval_count":9}
            """, mediaType: "application/x-ndjson");
        var client = new OllamaClient(new StubClientFactory(handler, "http://ollama.test"), TestConnections.Make("o", BackendKind.Ollama, "http://ollama.test"));

        var (text, calls, finish) = await CollectAsync(client.StreamChatAsync("qwen3", History(), WithTools, CancellationToken.None));

        Assert.Equal(["Reading."], text);
        var call = Assert.Single(calls);
        Assert.Equal("read_file", call.Name);
        Assert.Equal("b.txt", call.TryParseArguments()!.Value.GetProperty("path").GetString());
        Assert.StartsWith("call_", call.Id); // Ollama sent no id; one is made up so the result can refer to it
        Assert.Equal([FinishReason.ToolCalls], finish);

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var tool = body.RootElement.GetProperty("tools")[0];
        Assert.Equal("function", tool.GetProperty("type").GetString());
        Assert.Equal("read_file", tool.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("object", tool.GetProperty("function").GetProperty("parameters").GetProperty("type").GetString());
        var messages = body.RootElement.GetProperty("messages");
        var sentCall = messages[2].GetProperty("tool_calls")[0].GetProperty("function");
        Assert.Equal("read_file", sentCall.GetProperty("name").GetString());
        Assert.Equal("a.txt", sentCall.GetProperty("arguments").GetProperty("path").GetString()); // an object, not a string
        Assert.Equal("tool", messages[3].GetProperty("role").GetString());
        Assert.Equal("read_file", messages[3].GetProperty("tool_name").GetString());
    }

    [Fact]
    public async Task OllamaChatWithoutToolsSendsNone()
    {
        var handler = StubHandler.Text("""{"message":{"role":"assistant","content":"hi"},"done":true,"done_reason":"stop"}""", mediaType: "application/x-ndjson");
        var client = new OllamaClient(new StubClientFactory(handler, "http://ollama.test"), TestConnections.Make("o", BackendKind.Ollama, "http://ollama.test"));

        await CollectAsync(client.StreamChatAsync("llama3", [new(ChatRole.User, "hi")], null, CancellationToken.None));

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.False(body.RootElement.TryGetProperty("tools", out _));
        Assert.False(body.RootElement.GetProperty("messages")[0].TryGetProperty("tool_calls", out _));
    }

    [Fact]
    public async Task OllamaReportsToolCapability()
    {
        var handler = StubHandler.Route(new Dictionary<string, (string, string)>
        {
            ["POST /api/show"] = ("""{"capabilities":["completion","tools"]}""", "application/json"),
        });
        var client = new OllamaClient(new StubClientFactory(handler, "http://ollama.test"), TestConnections.Make("o", BackendKind.Ollama, "http://ollama.test"));

        Assert.True(await ((ILlmClient)client).SupportsToolsAsync("qwen3", CancellationToken.None));
    }

    [Fact]
    public async Task OpenAiAssemblesStreamedFragmentsAndSendsToolCallIds()
    {
        var handler = StubHandler.Text(
            """
            data: {"choices":[{"index":0,"delta":{"content":"Reading."}}]}

            data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_9","type":"function","function":{"name":"read_file","arguments":""}}]}}]}

            data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"pa"}}]}}]}

            data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"th\":\"b.txt\"}"}}]}}]}

            data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"id":"call_10","type":"function","function":{"name":"read_file","arguments":"{\"path\":\"c.txt\"}"}}]}}]}

            data: {"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}

            data: [DONE]

            """, mediaType: "text/event-stream");
        var client = new OpenAiCompatibleClient(new StubClientFactory(handler, "http://vllm.test/v1"), TestConnections.Make("v", BackendKind.OpenAiCompatible, "http://vllm.test/v1"));

        var (text, calls, finish) = await CollectAsync(client.StreamChatAsync("qwen", History(), WithTools, CancellationToken.None));

        Assert.Equal(["Reading."], text);
        Assert.Equal(2, calls.Count);
        Assert.Equal(new ToolCall("call_9", "read_file", """{"path":"b.txt"}"""), calls[0]);
        Assert.Equal("c.txt", calls[1].TryParseArguments()!.Value.GetProperty("path").GetString());
        Assert.Equal([FinishReason.ToolCalls], finish);

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("read_file", body.RootElement.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
        var messages = body.RootElement.GetProperty("messages");
        var sent = messages[2].GetProperty("tool_calls")[0];
        Assert.Equal("call_1", sent.GetProperty("id").GetString());
        Assert.Equal("""{"path":"a.txt"}""", sent.GetProperty("function").GetProperty("arguments").GetString()); // a string here
        Assert.Equal("tool", messages[3].GetProperty("role").GetString());
        Assert.Equal("call_1", messages[3].GetProperty("tool_call_id").GetString());
    }

    [Fact]
    public async Task OpenAiDeliversCallsWhenTheStreamEndsWithoutFinishReason()
    {
        var handler = StubHandler.Text(
            """
            data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"x","function":{"name":"read_file","arguments":"{}"}}]}}]}

            data: [DONE]

            """, mediaType: "text/event-stream");
        var client = new OpenAiCompatibleClient(new StubClientFactory(handler, "http://vllm.test/v1"), TestConnections.Make("v", BackendKind.OpenAiCompatible, "http://vllm.test/v1"));

        var (_, calls, _) = await CollectAsync(client.StreamChatAsync("qwen", History(), WithTools, CancellationToken.None));

        Assert.Equal("read_file", Assert.Single(calls).Name);
    }

    [Fact]
    public async Task AnthropicAssemblesToolUseAndSendsToolResultBlocks()
    {
        var handler = StubHandler.Text(
            """
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"claude","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":1}}}

            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Reading."}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":0}

            event: content_block_start
            data: {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_1","name":"read_file","input":{}}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"path\":"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"\"b.txt\"}"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":1}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"output_tokens":20}}

            event: message_stop
            data: {"type":"message_stop"}

            """, mediaType: "text/event-stream");
        var client = new AnthropicLlmClient(TestConnections.Make("c", BackendKind.Anthropic, "http://anthropic.test", apiKey: "k"), new HttpClient(handler, disposeHandler: false));

        var (text, calls, finish) = await CollectAsync(client.StreamChatAsync("claude", History(), WithTools, CancellationToken.None));

        Assert.Equal(["Reading."], text);
        Assert.Equal(new ToolCall("toolu_1", "read_file", """{"path":"b.txt"}"""), Assert.Single(calls));
        Assert.Equal([FinishReason.ToolCalls], finish);

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var tool = body.RootElement.GetProperty("tools")[0];
        Assert.Equal("read_file", tool.GetProperty("name").GetString());
        Assert.Equal("string", tool.GetProperty("input_schema").GetProperty("properties").GetProperty("path").GetProperty("type").GetString());
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength()); // user, assistant (text + tool_use), user (tool_result)
        var use = messages[1].GetProperty("content")[1];
        Assert.Equal("tool_use", use.GetProperty("type").GetString());
        Assert.Equal("call_1", use.GetProperty("id").GetString());
        Assert.Equal("a.txt", use.GetProperty("input").GetProperty("path").GetString());
        Assert.Equal("user", messages[2].GetProperty("role").GetString());
        var result = messages[2].GetProperty("content")[0];
        Assert.Equal("tool_result", result.GetProperty("type").GetString());
        Assert.Equal("call_1", result.GetProperty("tool_use_id").GetString());
    }
}
