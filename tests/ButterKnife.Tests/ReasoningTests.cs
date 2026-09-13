using ButterKnife.Options;
using ButterKnife.Services;

namespace ButterKnife.Tests;

public class ReasoningTests
{
    private static readonly ChatMessage[] Messages = [new(ChatRole.User, "hi")];

    private static (string Text, string Reasoning) Run(ThinkTagSplitter splitter, params string[] chunks)
    {
        var text = ""; var reasoning = "";
        foreach (var chunk in chunks)
        {
            foreach (var piece in splitter.Feed(chunk))
            {
                text += piece.Text; reasoning += piece.Reasoning;
            }
        }
        if (splitter.Flush() is { } tail)
        {
            text += tail.Text; reasoning += tail.Reasoning;
        }
        return (text, reasoning);
    }

    [Fact]
    public void Splitter_SeparatesInlineThinkTags_EvenWhenSplitAcrossChunks()
    {
        Assert.Equal(("Hello world", "let me see"), Run(new ThinkTagSplitter(), "<think>let me see</think>Hello world"));
        Assert.Equal(("Answer", "deep"), Run(new ThinkTagSplitter(), "<thi", "nk>de", "ep</th", "ink>Ans", "wer"));
        Assert.Equal(("a < b and c", ""), Run(new ThinkTagSplitter(), "a < b and c"));
        Assert.Equal(("x <b>bold</b>", ""), Run(new ThinkTagSplitter(), "x <b>bo", "ld</b>"));
        Assert.Equal(("", "never closed"), Run(new ThinkTagSplitter(), "<think>never closed"));
        Assert.Equal(("tail<", ""), Run(new ThinkTagSplitter(), "tail<"));
    }

    [Fact]
    public async Task Ollama_YieldsThinkingAndDoneReason()
    {
        var handler = StubHandler.Text(
            """
            {"message":{"role":"assistant","content":"","thinking":"hmm "},"done":false}
            {"message":{"role":"assistant","content":"","thinking":"ok"},"done":false}
            {"message":{"role":"assistant","content":"Answer"},"done":false}
            {"message":{"role":"assistant","content":""},"done":true,"done_reason":"length","prompt_eval_count":5,"eval_count":3}
            """);
        var client = new OllamaClient(new StubClientFactory(handler, "http://ollama.test:11434"),
            TestConnections.Make("Ollama", BackendKind.Ollama, "http://ollama.test:11434"));

        var deltas = new List<ChatDelta>();
        await foreach (var d in client.StreamChatAsync("m", Messages, null, CancellationToken.None))
        {
            deltas.Add(d);
        }

        Assert.Equal("hmm ok", string.Concat(deltas.Select(d => d.Reasoning)));
        Assert.Equal("Answer", string.Concat(deltas.Select(d => d.Text)));
        Assert.Equal(FinishReason.Length, deltas.Single(d => d.Finish is not null).Finish);
    }

    [Fact]
    public async Task OpenAi_YieldsReasoningContentAndFinishReason()
    {
        var handler = StubHandler.Text(
            """
            data: {"choices":[{"delta":{"reasoning_content":"think "},"index":0}]}

            data: {"choices":[{"delta":{"reasoning":"more"},"index":0}]}

            data: {"choices":[{"delta":{"content":"Answer"},"index":0,"finish_reason":null}]}

            data: {"choices":[{"delta":{},"index":0,"finish_reason":"stop"}]}

            data: [DONE]
            """, mediaType: "text/event-stream");
        var client = new OpenAiCompatibleClient(new StubClientFactory(handler, "http://x.test/v1"),
            TestConnections.Make("X", BackendKind.OpenAiCompatible, "http://x.test/v1"));

        var deltas = new List<ChatDelta>();
        await foreach (var d in client.StreamChatAsync("m", Messages, null, CancellationToken.None))
        {
            deltas.Add(d);
        }

        Assert.Equal("think more", string.Concat(deltas.Select(d => d.Reasoning)));
        Assert.Equal("Answer", string.Concat(deltas.Select(d => d.Text)));
        Assert.Equal(FinishReason.Stop, deltas.Single(d => d.Finish is not null).Finish);
    }
}
