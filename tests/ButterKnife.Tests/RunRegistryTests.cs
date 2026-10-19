using System.Runtime.CompilerServices;
using ButterKnife.Data;
using ButterKnife.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ButterKnife.Tests;

/// <summary>Server-side runs: replies and Code turns that outlive the page, can be re-attached, and save themselves.</summary>
public sealed class RunRegistryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "butterknife-tests", Guid.NewGuid().ToString("N"));
    private static readonly Guid Me = Guid.NewGuid();
    private static readonly Guid Someone = Guid.NewGuid();
    private readonly SqliteDatabase _db;

    public RunRegistryTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "work"));
        _db = new SqliteDatabase(Microsoft.Extensions.Options.Options.Create(new DatabaseOptions { ConnectionString = $"Data Source={Path.Combine(_dir, "test.db")}" }));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    // ---------------------------------------------------------------- chat

    private (ChatRunRegistry Runs, SqliteConversationStore Store) Chat(ILlmClient client)
    {
        var store = new SqliteConversationStore(_db);
        var titler = new ChatTitler(new FakeRegistry(client), NullLogger<ChatTitler>.Instance);
        return (new ChatRunRegistry(store, new ConversationEvents(), titler, NullLogger<ChatRunRegistry>.Instance), store);
    }

    [Fact]
    public async Task AChatReplyIsSavedWithoutAnyPageAndTitlesTheChat()
    {
        var client = new ScriptedClient(["Hello ", "there."]);
        var (runs, store) = Chat(client);
        var conversation = await store.CreateAsync(Me, "say hi", client.ConnectionId, "m", null, CancellationToken.None);
        await store.AppendMessageAsync(conversation.Id, new ChatMessage(ChatRole.User, "say hi"), CancellationToken.None);

        var run = runs.Start(new ChatRunStart(conversation.Id, Me, client, "m", [new(ChatRole.User, "say hi")], ChatOptions.Default, 8192, "say hi", SuggestTitle: true));
        await run.Completion;

        var saved = await store.GetAsync(conversation.Id, Me, CancellationToken.None);
        Assert.Equal("Hello there.", saved!.Messages[^1].Content);
        Assert.Equal("m", saved.Messages[^1].Model);
        Assert.Equal("A Fine Title", saved.Title);
        Assert.Null(runs.Find(conversation.Id, Me)); // gone once finished
    }

    [Fact]
    public async Task APageAttachingMidReplyCatchesUpAndFollows()
    {
        var client = new ScriptedClient(["one ", "two ", "three"]) { Gate = new SemaphoreSlim(0) };
        var (runs, store) = Chat(client);
        var conversation = await store.CreateAsync(Me, "count", client.ConnectionId, "m", null, CancellationToken.None);

        var run = runs.Start(new ChatRunStart(conversation.Id, Me, client, "m", [new(ChatRole.User, "count")], ChatOptions.Default, null, "count", SuggestTitle: false));
        client.Gate.Release(2); // two chunks stream before anyone is looking
        await WaitUntil(() => client.Sent == 2);

        Assert.Same(run, runs.Find(conversation.Id, Me));
        Assert.Null(runs.Find(conversation.Id, Someone)); // only the owner may watch
        var view = new ChatView();
        using (run.Attach(view))
        {
            Assert.Equal("one two ", view.Text.ToString()); // replayed at once
            client.Gate.Release(1);
            await run.Completion;
        }

        Assert.Equal("one two three", view.Text.ToString());
        Assert.NotNull(view.Result?.MessageId);
    }

    [Fact]
    public async Task StoppingKeepsWhatStreamedAndContinuingSavesTheWholeReply()
    {
        var client = new ScriptedClient(["partial ", "never"]) { Gate = new SemaphoreSlim(0) };
        var (runs, store) = Chat(client);
        var conversation = await store.CreateAsync(Me, "go", client.ConnectionId, "m", null, CancellationToken.None);

        var run = runs.Start(new ChatRunStart(conversation.Id, Me, client, "m", [new(ChatRole.User, "go")], ChatOptions.Default, null, "go", SuggestTitle: true));
        client.Gate.Release(1);
        await WaitUntil(() => client.Sent == 1);
        var view = new ChatView();
        using var _ = run.Attach(view);
        run.Stop();
        await run.Completion;

        Assert.True(view.Result!.Stopped);
        var stored = (await store.GetAsync(conversation.Id, Me, CancellationToken.None))!.Messages[^1];
        Assert.Equal("partial ", stored.Content);
        Assert.Equal("go", (await store.GetAsync(conversation.Id, Me, CancellationToken.None))!.Title); // no title for a stopped reply

        var more = new ScriptedClient(["and the rest."]);
        var (continueRuns, _) = Chat(more);
        var continued = continueRuns.Start(new ChatRunStart(conversation.Id, Me, more, "m", [new(ChatRole.User, "go"), new(ChatRole.Assistant, "partial ")], ChatOptions.Default, null, "go",
            SuggestTitle: false, ContinueMessageId: stored.Id, ContinueText: "partial "));
        await continued.Completion;

        var messages = (await store.GetAsync(conversation.Id, Me, CancellationToken.None))!.Messages;
        Assert.Single(messages);
        Assert.Equal("partial and the rest.", messages[0].Content);
    }

    [Fact]
    public async Task AViewMayDetachItselfWhenTheReplyEnds()
    {
        var client = new ScriptedClient(["done"]);
        var (runs, store) = Chat(client);
        var conversation = await store.CreateAsync(Me, "first", client.ConnectionId, "m", null, CancellationToken.None);
        var run = runs.Start(new ChatRunStart(conversation.Id, Me, client, "m", [new(ChatRole.User, "first")], ChatOptions.Default, null, "first", SuggestTitle: true));
        var view = new DetachingView();
        view.Attachment = run.Attach(view); // what the chat page does: its Ended handler detaches
        await run.Completion;

        Assert.True(view.Ended);
        Assert.Equal("A Fine Title", (await store.GetAsync(conversation.Id, Me, CancellationToken.None))!.Title); // the run got past the end
    }

    private sealed class DetachingView : IChatRunView
    {
        public IDisposable? Attachment { get; set; }
        public bool Ended { get; private set; }

        public void Delta(bool alternative, ChatDelta delta) { }

        void IChatRunView.Ended(ChatRunResult result)
        {
            Ended = true;
            Attachment?.Dispose();
        }
    }

    [Fact]
    public async Task OneReplyAtATimePerChat()
    {
        var client = new ScriptedClient(["x"]) { Gate = new SemaphoreSlim(0) };
        var (runs, store) = Chat(client);
        var conversation = await store.CreateAsync(Me, "x", client.ConnectionId, "m", null, CancellationToken.None);
        var start = new ChatRunStart(conversation.Id, Me, client, "m", [new(ChatRole.User, "x")], ChatOptions.Default, null, "x", SuggestTitle: false);

        var run = runs.Start(start);
        Assert.Throws<InvalidOperationException>(() => runs.Start(start));

        await runs.StopAsync(conversation.Id);
        Assert.False(run.IsRunning);
    }

    // ---------------------------------------------------------------- Code

    [Fact]
    public async Task ACodeTurnWaitsForAnAnswerFromWhicheverPageIsShowingIt()
    {
        var work = Path.Combine(_dir, "work");
        await File.WriteAllTextAsync(Path.Combine(work, "notes.md"), "draft\n");
        var store = new SqliteCodeSessionStore(_db);
        var client = new ToolScriptedClient(new ToolCall("e1", CodeTools.EditFile, """{"path":"notes.md","old_string":"draft","new_string":"done"}"""));
        var runs = new CodeRunRegistry(store, new CodeSessionEvents(), new ChatTitler(new FakeRegistry(client), NullLogger<ChatTitler>.Instance), NullLogger<CodeRunRegistry>.Instance);
        var session = await store.CreateAsync(Me, "edit", work, client.ConnectionId, "m", CodePermissionMode.Ask, CancellationToken.None);
        var prompt = new ChatMessage(ChatRole.User, "finish the notes");
        var promptId = await store.AppendAsync(session.Id, prompt, cancellationToken: CancellationToken.None);

        var agent = new CodeAgent(client, "m", new CodeWorkspace(work));
        var run = runs.Start(new CodeRunStart(session.Id, Me, promptId, agent, [prompt with { Id = promptId }], client.ConnectionId, "m", "finish the notes", false, null, null));

        // The first page sees the prompt, then goes away without answering.
        var first = new CodeView();
        var attachment = run.Attach(first);
        await WaitUntil(() => first.Waiting.Count == 1);
        attachment.Dispose();

        // A second page (a reload) catches up, sees the same waiting prompt, and answers it.
        var second = new CodeView();
        using (run.Attach(second))
        {
            Assert.Equal(["e1"], second.Waiting);
            Assert.True(run.Answer("e1", new PermissionDecision(PermissionChoice.Allow)));
            await run.Completion;
        }

        Assert.Equal(CodeTurnEnd.Done, second.Result!.End);
        Assert.Equal("done\n", await File.ReadAllTextAsync(Path.Combine(work, "notes.md")));
        var stored = (await store.GetAsync(session.Id, Me, CancellationToken.None))!.Entries.Select(e => e.Message.Role);
        Assert.Equal([ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant], stored);
        Assert.Null(runs.Find(session.Id, Me));
    }

    [Fact]
    public async Task StoppingACodeTurnAnswersTheWaitingCallAndSavesIt()
    {
        var work = Path.Combine(_dir, "work");
        var store = new SqliteCodeSessionStore(_db);
        var client = new ToolScriptedClient(new ToolCall("r1", CodeTools.RunCommand, """{"command":"echo hi"}"""));
        var runs = new CodeRunRegistry(store, new CodeSessionEvents(), new ChatTitler(new FakeRegistry(client), NullLogger<ChatTitler>.Instance), NullLogger<CodeRunRegistry>.Instance);
        var session = await store.CreateAsync(Me, "run", work, client.ConnectionId, "m", CodePermissionMode.Ask, CancellationToken.None);
        var prompt = new ChatMessage(ChatRole.User, "say hi");
        var promptId = await store.AppendAsync(session.Id, prompt, cancellationToken: CancellationToken.None);

        var run = runs.Start(new CodeRunStart(session.Id, Me, promptId, new CodeAgent(client, "m", new CodeWorkspace(work)), [prompt with { Id = promptId }], client.ConnectionId, "m", "say hi", false, null, null));
        var view = new CodeView();
        using var _ = run.Attach(view);
        await WaitUntil(() => view.Waiting.Count == 1);
        await runs.StopAsync(session.Id);

        Assert.Null(view.Result!.End);
        var last = (await store.GetAsync(session.Id, Me, CancellationToken.None))!.Entries[^1].Message;
        Assert.Equal((ChatRole.Tool, "r1"), (last.Role, last.ToolCallId));
        Assert.True(last.IsError);
    }

    // ---------------------------------------------------------------- helpers

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25);
        }
        Assert.True(condition());
    }

    private sealed class ChatView : IChatRunView
    {
        public System.Text.StringBuilder Text { get; } = new();
        public ChatRunResult? Result { get; private set; }

        public void Delta(bool alternative, ChatDelta delta)
        {
            if (!alternative)
            {
                Text.Append(delta.Text);
            }
        }

        public void Ended(ChatRunResult result) => Result = result;
    }

    private sealed class CodeView : ICodeRunView
    {
        public List<string> Waiting { get; } = [];
        public CodeRunResult? Result { get; private set; }

        public void StepStarted(string model, long startedAt) { }
        public void Delta(ChatDelta delta) { }
        public void AssistantMessage(ChatMessage message, GenerationStats stats) { }
        public void PermissionRequested(ToolInvocation invocation) => Waiting.Add(invocation.Call.Id);
        public void PermissionAnswered(string callId, PermissionChoice choice) => Waiting.Remove(callId);
        public void ToolStarted(ToolInvocation invocation, CodePermissionMode mode) { }
        public void ToolOutput(string callId, string chunk) { }
        public void ToolFinished(ToolInvocation invocation, ToolResult result, ChatMessage message) { }
        public void Compacting() { }
        public void Compacted(CodeCompaction compaction) { }
        public void CompactionFailed(string error) { }
        public void Interrupted(ChatMessage? partial, IReadOnlyList<ChatMessage> closed) { }
        public void Error(string message) { }
        public void Ended(CodeRunResult result) => Result = result;
    }

    private sealed class FakeRegistry(ILlmClient client) : ILlmClientRegistry
    {
        public Task<IReadOnlyList<ILlmClient>> GetClientsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ILlmClient>>([client]);
        public Task<ILlmClient> GetAsync(Guid connectionId, CancellationToken cancellationToken = default) => Task.FromResult(client);
    }

    /// <summary>Streams the given chunks (each waiting for the gate when there is one); a title request gets a title.</summary>
    private class ScriptedClient(string[] chunks) : ILlmClient
    {
        public SemaphoreSlim? Gate { get; init; }
        public int Sent;

        public Guid ConnectionId { get; } = Guid.NewGuid();
        public string BackendName => "scripted";
        public string? DefaultModel => null;

        public virtual async IAsyncEnumerable<ChatDelta> StreamChatAsync(string model, IReadOnlyList<ChatMessage> messages, ChatOptions? options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (messages[^1].Content.StartsWith(ChatTitler.Instruction, StringComparison.Ordinal))
            {
                yield return ChatDelta.FromText("A Fine Title");
                yield break;
            }
            foreach (var chunk in chunks)
            {
                if (Gate is not null)
                {
                    await Gate.WaitAsync(cancellationToken);
                }
                Interlocked.Increment(ref Sent);
                yield return ChatDelta.FromText(chunk);
            }
            yield return ChatDelta.FromUsage(new TokenUsage(10, chunks.Length));
        }

        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>(["m"]);
        public Task<int?> GetContextWindowAsync(string model, CancellationToken cancellationToken = default) => Task.FromResult<int?>(null);
        public Task<bool?> SupportsImagesAsync(string model, CancellationToken cancellationToken = default) => Task.FromResult<bool?>(null);
    }

    /// <summary>The first request calls one tool; later ones answer "Done.".</summary>
    private sealed class ToolScriptedClient(ToolCall call) : ScriptedClient([])
    {
        private int _requests;

        public override async IAsyncEnumerable<ChatDelta> StreamChatAsync(string model, IReadOnlyList<ChatMessage> messages, ChatOptions? options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (Interlocked.Increment(ref _requests) == 1)
            {
                yield return ChatDelta.FromToolCall(call);
            }
            else
            {
                yield return ChatDelta.FromText("Done.");
            }
        }
    }
}
