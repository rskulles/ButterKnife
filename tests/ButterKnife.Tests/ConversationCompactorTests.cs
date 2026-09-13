using ButterKnife.Services;

namespace ButterKnife.Tests;

public class ConversationCompactorTests
{
    [Fact]
    public void ComposeSystemPrompt_PutsInstructionsBetweenPersonaAndSummary()
    {
        Assert.Equal("Answer in Spanish.", ConversationCompactor.ComposeSystemPrompt(null, " Answer in Spanish. ", null));
        Assert.Equal("Be terse.", ConversationCompactor.ComposeSystemPrompt("Be terse.", "   ", null));

        var all = ConversationCompactor.ComposeSystemPrompt("Be terse.", "Answer in Spanish.", "The user likes cats.")!;
        var persona = all.IndexOf("Be terse.", StringComparison.Ordinal);
        var preamble = all.IndexOf(ConversationCompactor.InstructionsPreamble, StringComparison.Ordinal);
        var instructions = all.IndexOf("Answer in Spanish.", StringComparison.Ordinal);
        var summary = all.IndexOf("The user likes cats.", StringComparison.Ordinal);
        Assert.True(persona >= 0 && persona < preamble && preamble < instructions && instructions < summary);
    }

    [Fact]
    public void ComposeSystemPrompt_CombinesPersonaAndSummary()
    {
        Assert.Null(ConversationCompactor.ComposeSystemPrompt(null, null));
        Assert.Equal("Be terse.", ConversationCompactor.ComposeSystemPrompt(" Be terse. ", ""));

        var both = ConversationCompactor.ComposeSystemPrompt("Be terse.", "The user likes cats.")!;
        Assert.StartsWith("Be terse.\n\n", both);
        Assert.Contains(ConversationCompactor.SummaryPreamble, both);
        Assert.Contains("The user likes cats.", both);

        var onlySummary = ConversationCompactor.ComposeSystemPrompt(null, "S")!;
        Assert.StartsWith(ConversationCompactor.SummaryPreamble, onlySummary);
    }

    [Fact]
    public void BuildSummaryRequest_TranscribesOlderTurnsAndNotesImages()
    {
        var request = ConversationCompactor.BuildSummaryRequest("Earlier: user wanted a plan.",
        [
            new ChatMessage(ChatRole.System, "ignored persona"),
            new ChatMessage(ChatRole.User, "Look at this", [new ChatImage("image/png", [1])]),
            new ChatMessage(ChatRole.Assistant, "It is a cat."),
        ]);

        Assert.Equal(2, request.Count);
        Assert.Equal(ChatRole.System, request[0].Role);
        Assert.Equal(ConversationCompactor.SummarizerSystemPrompt, request[0].Content);
        var transcript = request[1].Content;
        Assert.Contains("Earlier: user wanted a plan.", transcript);
        Assert.Contains("User: [1 image attached] Look at this", transcript);
        Assert.Contains("Assistant: It is a cat.", transcript);
        Assert.DoesNotContain("ignored persona", transcript);
        Assert.False(request[1].HasImages);
    }

    [Fact]
    public async Task SummarizeAsync_ConcatenatesStreamedText_AndRejectsEmpty()
    {
        var compactor = new ConversationCompactor();
        var client = new EchoClient(["The user ", "asked about ", "cats."]);

        var summary = await compactor.SummarizeAsync(client, "m", null, [new ChatMessage(ChatRole.User, "cats?")], CancellationToken.None);

        Assert.Equal("The user asked about cats.", summary);
        Assert.Equal("m", client.LastModel);
        Assert.Equal(ChatRole.System, client.LastMessages![0].Role);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            compactor.SummarizeAsync(new EchoClient(["  "]), "m", null, [new ChatMessage(ChatRole.User, "x")], CancellationToken.None));
    }

    private sealed class EchoClient(string[] chunks) : ILlmClient
    {
        public Guid ConnectionId { get; } = Guid.NewGuid();
        public string BackendName => "Echo";
        public string? DefaultModel => null;
        public string? LastModel { get; private set; }
        public IReadOnlyList<ChatMessage>? LastMessages { get; private set; }

        public async IAsyncEnumerable<ChatDelta> StreamChatAsync(string model, IReadOnlyList<ChatMessage> messages, ChatOptions? options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastModel = model;
            LastMessages = messages;
            foreach (var chunk in chunks)
            {
                await Task.Yield();
                yield return ChatDelta.FromText(chunk);
            }
            yield return ChatDelta.FromUsage(new TokenUsage(10, 3));
        }

        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>(["m"]);

        public Task<int?> GetContextWindowAsync(string model, CancellationToken cancellationToken = default) => Task.FromResult<int?>(8192);

        public Task<bool?> SupportsImagesAsync(string model, CancellationToken cancellationToken = default) => Task.FromResult<bool?>(null);
    }
}
