using System.Runtime.CompilerServices;
using ButterKnife.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ButterKnife.Tests;

public sealed class ChatTitlerTests
{
    [Theory]
    [InlineData("Word Frequencies in Python", "Word Frequencies in Python")]
    [InlineData("\"Word Frequencies in Python.\"", "Word Frequencies in Python")]
    [InlineData("**Title:** Counting Words\n\nThat captures it.", "Counting Words")]
    [InlineData("# Counting Words", "Counting Words")]
    [InlineData("\n\n  Counting Words!  \n", "Counting Words")]
    [InlineData("Why is the sky blue?", "Why is the sky blue?")]
    public void CleanStripsDecoration(string raw, string expected) => Assert.Equal(expected, ChatTitler.Clean(raw));

    [Theory]
    [InlineData("")]
    [InlineData("   \n  ")]
    [InlineData("\"\"")]
    public void CleanReturnsNullWhenNothingIsLeft(string raw) => Assert.Null(ChatTitler.Clean(raw));

    [Fact]
    public void CleanTrimsLongTitles()
    {
        var title = ChatTitler.Clean(new string('a', 100));
        Assert.NotNull(title);
        Assert.Equal(ChatTitler.MaxLength + 1, title.Length);
        Assert.EndsWith("…", title);
    }

    [Fact]
    public async Task SuggestUsesTheReplyTextAndIgnoresInlineThinking()
    {
        var client = new FakeClient("<think>They want a title.</think>\"Word Frequencies in Python.\"\n");
        var titler = new ChatTitler(new FakeRegistry(client), NullLogger<ChatTitler>.Instance);

        var title = await titler.SuggestAsync(client.ConnectionId, "m", "How do I count words?", "Use Counter.", CancellationToken.None);

        Assert.Equal("Word Frequencies in Python", title);
        Assert.NotNull(client.LastMessages);
        Assert.Equal(ChatRole.User, client.LastMessages![^1].Role);
        Assert.StartsWith(ChatTitler.Instruction, client.LastMessages[^1].Content);
        Assert.Contains("How do I count words?", client.LastMessages[^1].Content);
    }

    [Fact]
    public async Task SuggestReturnsNullWhenTheBackendFails()
    {
        var client = new FakeClient(null);
        var titler = new ChatTitler(new FakeRegistry(client), NullLogger<ChatTitler>.Instance);

        Assert.Null(await titler.SuggestAsync(client.ConnectionId, "m", "p", "r", CancellationToken.None));
    }

    private sealed class FakeRegistry(ILlmClient client) : ILlmClientRegistry
    {
        public Task<IReadOnlyList<ILlmClient>> GetClientsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ILlmClient>>([client]);

        public Task<ILlmClient> GetAsync(Guid connectionId, CancellationToken cancellationToken = default) => Task.FromResult(client);
    }

    /// <summary>Streams <paramref name="reply"/> in two chunks; a null reply throws like a dead server.</summary>
    private sealed class FakeClient(string? reply) : ILlmClient
    {
        public Guid ConnectionId { get; } = Guid.NewGuid();

        public string BackendName => "fake";

        public string? DefaultModel => null;

        public IReadOnlyList<ChatMessage>? LastMessages { get; private set; }

        public async IAsyncEnumerable<ChatDelta> StreamChatAsync(string model, IReadOnlyList<ChatMessage> messages, ChatOptions? options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastMessages = messages;
            if (reply is null)
            {
                throw new HttpRequestException("connection refused");
            }
            await Task.Yield();
            var half = reply.Length / 2;
            yield return new ChatDelta(reply[..half], null);
            yield return new ChatDelta(reply[half..], null);
        }

        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>(["m"]);

        public Task<int?> GetContextWindowAsync(string model, CancellationToken cancellationToken = default) => Task.FromResult<int?>(null);

        public Task<bool?> SupportsImagesAsync(string model, CancellationToken cancellationToken = default) => Task.FromResult<bool?>(null);
    }
}
