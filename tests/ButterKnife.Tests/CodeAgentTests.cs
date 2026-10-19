using System.Runtime.CompilerServices;
using ButterKnife.Data;
using ButterKnife.Services;

namespace ButterKnife.Tests;

public sealed class CodeAgentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "butterknife-tests", Guid.NewGuid().ToString("N"));
    private readonly CodeWorkspace _workspace;

    public CodeAgentTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "notes.md"), "Status: draft\n");
        _workspace = new CodeWorkspace(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static ToolCall Edit(string id = "e1") =>
        new(id, CodeTools.EditFile, """{"path":"notes.md","old_string":"Status: draft","new_string":"Status: done"}""");

    private static ToolCall Read(string id = "r1") => new(id, CodeTools.ReadFile, """{"path":"notes.md"}""");

    [Fact]
    public async Task RunsReadsWithoutAskingAndStopsWhenTheModelAnswers()
    {
        var client = new ScriptedClient([Read()], []);
        var observer = new RecordingObserver(PermissionChoice.Allow);
        var history = new List<ChatMessage> { new(ChatRole.User, "what's in notes?") };

        var end = await new CodeAgent(client, "m", _workspace).RunAsync(history, observer, CancellationToken.None);

        Assert.Equal(CodeTurnEnd.Done, end);
        Assert.Empty(observer.Asked);
        Assert.Equal([ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant], history.Select(m => m.Role));
        Assert.Contains("Status: draft", history[2].Content);
        Assert.Equal("r1", history[2].ToolCallId);
        // The second request carried the tool result, with the tools and a system prompt.
        Assert.Equal(ChatRole.System, client.Requests[1][0].Role);
        Assert.Equal(ChatRole.Tool, client.Requests[1][^1].Role);
        Assert.Contains(client.Options[0].Tools!, t => t.Name == CodeTools.EditFile);
    }

    [Fact]
    public async Task AsksBeforeEditingAndDenyEndsTheTurnWithAnAnswerForEveryCall()
    {
        var client = new ScriptedClient([Edit(), Read("r2")], [Read("never")]);
        var observer = new RecordingObserver(PermissionChoice.Deny, feedback: "leave it");
        var history = new List<ChatMessage> { new(ChatRole.User, "finish it") };

        var end = await new CodeAgent(client, "m", _workspace).RunAsync(history, observer, CancellationToken.None);

        Assert.Equal(CodeTurnEnd.Denied, end);
        Assert.Single(observer.Asked);
        Assert.Single(client.Requests);
        Assert.Equal("Status: draft\n", File.ReadAllText(Path.Combine(_root, "notes.md")));
        var results = history.Where(m => m.Role == ChatRole.Tool).ToList();
        Assert.Equal(["e1", "r2"], results.Select(r => r.ToolCallId));
        Assert.All(results, r => Assert.True(r.IsError));
        Assert.Contains("leave it", results[0].Content);
    }

    [Fact]
    public async Task AllowAlwaysSwitchesToAcceptingEdits()
    {
        var client = new ScriptedClient([Edit("e1")], [new ToolCall("e2", CodeTools.EditFile, """{"path":"notes.md","old_string":"done","new_string":"shipped"}""")], []);
        var observer = new RecordingObserver(PermissionChoice.AllowAlways);
        var agent = new CodeAgent(client, "m", _workspace);

        await agent.RunAsync([new(ChatRole.User, "go")], observer, CancellationToken.None);

        Assert.Single(observer.Asked); // the second edit went ahead without asking
        Assert.Equal(CodePermissionMode.AcceptEdits, agent.Mode);
        Assert.Equal("Status: shipped\n", File.ReadAllText(Path.Combine(_root, "notes.md")));
    }

    [Fact]
    public void CommandsAskUnlessAllowedOrInAutoMode()
    {
        var agent = new CodeAgent(new ScriptedClient(), "m", _workspace) { Mode = CodePermissionMode.AcceptEdits };
        var command = CodeTools.Prepare(new ToolCall("c", CodeTools.RunCommand, """{"command":"git status"}"""), _workspace);
        var edit = CodeTools.Prepare(Edit(), _workspace);

        Assert.True(agent.NeedsPermission(command));
        Assert.False(agent.NeedsPermission(edit));

        agent.AllowedCommands.Add("git status");
        Assert.False(agent.NeedsPermission(command));

        agent.AllowedCommands.Clear();
        agent.Mode = CodePermissionMode.Auto;
        Assert.False(agent.NeedsPermission(command));
    }

    [Fact]
    public async Task ATextOnlyModelGetsNotesInsteadOfPictures()
    {
        var png = new ChatImage("image/png", [1, 2, 3]);
        var client = new ScriptedClient([]);
        var agent = new CodeAgent(client, "m", _workspace) { SupportsImages = false };

        await agent.RunAsync([new(ChatRole.User, "what is this?", [png])], new RecordingObserver(PermissionChoice.Allow), CancellationToken.None);

        var prompt = client.Requests[0][1];
        Assert.False(prompt.HasImages);
        Assert.Contains("cannot see images", prompt.Content);

        var seeing = new ScriptedClient([]);
        await new CodeAgent(seeing, "m", _workspace).RunAsync([new(ChatRole.User, "what is this?", [png])], new RecordingObserver(PermissionChoice.Allow), CancellationToken.None);
        Assert.Single(seeing.Requests[0][1].Images); // unknown: sent as is
    }

    [Fact]
    public async Task PlanModeOffersNoEditTools()
    {
        var client = new ScriptedClient([]);
        var agent = new CodeAgent(client, "m", _workspace) { Mode = CodePermissionMode.Plan };

        await agent.RunAsync([new(ChatRole.User, "plan it")], new RecordingObserver(PermissionChoice.Allow), CancellationToken.None);

        Assert.DoesNotContain(client.Options[0].Tools!, t => t.Name is CodeTools.EditFile or CodeTools.WriteFile);
        Assert.Contains("Plan mode", client.Requests[0][0].Content);
    }

    [Fact]
    public async Task StopsAtTheStepLimit()
    {
        var client = new ScriptedClient(Enumerable.Range(0, CodeAgent.MaxSteps + 5).Select(i => new[] { Read($"r{i}") }).ToArray());

        var end = await new CodeAgent(client, "m", _workspace).RunAsync([new(ChatRole.User, "loop")], new RecordingObserver(PermissionChoice.Allow), CancellationToken.None);

        Assert.Equal(CodeTurnEnd.StepLimit, end);
        Assert.Equal(CodeAgent.MaxSteps, client.Requests.Count);
    }

    [Fact]
    public void RepairAnswersOpenCallsAndDropsStrayResults()
    {
        List<ChatMessage> stored =
        [
            new(ChatRole.User, "go"),
            new(ChatRole.Assistant, "") { ToolCalls = [Read("a"), Read("b")] },
            new(ChatRole.Tool, "ok") { ToolCallId = "a", ToolName = CodeTools.ReadFile },
            new(ChatRole.Tool, "stray") { ToolCallId = "zzz", ToolName = CodeTools.ReadFile },
            new(ChatRole.User, "again"),
        ];

        var repaired = CodeAgent.Repair(stored);

        Assert.Equal([ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Tool, ChatRole.User], repaired.Select(m => m.Role));
        Assert.Equal("b", repaired[3].ToolCallId);
        Assert.True(repaired[3].IsError);
        Assert.Same(stored[2], repaired[2]);
    }

    [Fact]
    public async Task CompactsEarlierPromptsWhenTheContextFillsUp()
    {
        // Two earlier prompts with a call each, then a third: the request would pass 80% of a 1000-token window.
        var history = new List<ChatMessage>();
        for (var i = 0; i < 3; i++)
        {
            history.Add(new(ChatRole.User, $"prompt {i}"));
            history.Add(new(ChatRole.Assistant, "") { ToolCalls = [Read($"old{i}")] });
            history.Add(new(ChatRole.Tool, new string('x', 1500)) { ToolCallId = $"old{i}", ToolName = CodeTools.ReadFile });
            history.Add(new(ChatRole.Assistant, "Read it."));
        }
        history.Add(new(ChatRole.User, "and now?"));
        var client = new ScriptedClient([]);
        var observer = new RecordingObserver(PermissionChoice.Allow);
        var agent = new CodeAgent(client, "m", _workspace) { ContextWindow = 1000, KeepRecentPrompts = 2 };

        var end = await agent.RunAsync(history, observer, CancellationToken.None);

        Assert.Equal(CodeTurnEnd.Done, end);
        var summaryRequest = Assert.Single(client.SummaryRequests);
        Assert.Equal(CodeCompactor.SummarizerSystemPrompt, summaryRequest[0].Content);
        Assert.Contains("prompt 0", summaryRequest[1].Content);
        Assert.Contains("Assistant called read_file", summaryRequest[1].Content);
        Assert.DoesNotContain("and now?", summaryRequest[1].Content);

        // Kept: the last two prompts. The checkpoint is a user message and the tail is valid as it stands.
        var compaction = Assert.Single(observer.Compactions);
        Assert.Equal(8, compaction.Through);
        Assert.Equal((compaction.Summary, compaction.Through), (agent.Summary, agent.SummaryThrough));
        var request = client.Requests[0];
        Assert.Contains(CodeCompactor.SummaryPreamble, request[0].Content);
        Assert.Contains("Summary of earlier work.", request[0].Content);
        Assert.Equal("prompt 2", request[1].Content);
        Assert.Equal(request.Skip(1).Select(m => m.Content), CodeAgent.Repair(request.Skip(1)).Select(m => m.Content));
        Assert.Equal(14, history.Count); // the stored transcript only grew by the reply
    }

    [Fact]
    public async Task ClearsOldResultsWhenOnePromptFillsTheWindowAndCannotBeSummarised()
    {
        // A single prompt whose summary fails: earlier results are cleared and the latest step's are kept.
        var history = new List<ChatMessage> { new(ChatRole.User, "read everything") };
        for (var i = 0; i < 3; i++)
        {
            history.Add(new(ChatRole.Assistant, "") { ToolCalls = [Read($"r{i}")] });
            history.Add(new(ChatRole.Tool, $"result {i} " + new string('x', 1500)) { ToolCallId = $"r{i}", ToolName = CodeTools.ReadFile });
        }
        var client = new ScriptedClient([]) { FailSummaries = true };
        var observer = new RecordingObserver(PermissionChoice.Allow);

        await new CodeAgent(client, "m", _workspace) { ContextWindow = 1000 }.RunAsync(history, observer, CancellationToken.None);

        Assert.Single(client.SummaryRequests);
        Assert.Empty(observer.Compactions);
        var results = client.Requests[0].Where(m => m.Role == ChatRole.Tool).ToList();
        Assert.Equal(["r0", "r1", "r2"], results.Select(r => r.ToolCallId));
        Assert.All(results.Take(2), r => Assert.StartsWith("[This read_file result", r.Content));
        Assert.StartsWith("result 2", results[2].Content);
        Assert.StartsWith("result 0", history[2].Content); // only the wire copy changed
    }

    [Fact]
    public async Task CompactsInsideOneLongTurnAndKeepsSendingValidHistories()
    {
        // One prompt, then twenty steps that each write a 3000-character file, in a small window.
        const int steps = 20;
        var client = new ScriptedClient(Enumerable.Range(0, steps)
            .Select(i => new[] { new ToolCall($"w{i}", CodeTools.WriteFile, $$"""{"path":"f{{i}}.txt","content":"{{new string((char)('a' + i), 3000)}}"}""") })
            .ToArray());
        var observer = new RecordingObserver(PermissionChoice.Allow);
        var agent = new CodeAgent(client, "m", _workspace) { Mode = CodePermissionMode.Auto, ContextWindow = 2800, KeepRecentPrompts = 2 };
        var history = new List<ChatMessage> { new(ChatRole.User, "write twenty files") };

        var end = await agent.RunAsync(history, observer, CancellationToken.None);

        Assert.Equal(CodeTurnEnd.Done, end);
        Assert.Equal(steps + 1, client.Requests.Count);
        Assert.True(File.Exists(Path.Combine(_root, $"f{steps - 1}.txt")));
        Assert.True(observer.Compactions.Count >= 2, $"compacted {observer.Compactions.Count} times");
        Assert.Equal(observer.Compactions.Count, client.SummaryRequests.Count);

        // Each checkpoint moves forward to the start of a whole step, never past the step the model has not seen.
        var throughs = observer.Compactions.Select(c => c.Through).ToList();
        Assert.Equal(throughs.Order(), throughs);
        Assert.Equal(throughs.Count, throughs.Distinct().Count());
        Assert.All(throughs, t => Assert.True(history[t].Role == ChatRole.Assistant && history[t].HasToolCalls && CodeCompactor.IsBoundary(history, t)));
        Assert.Equal((observer.Compactions[^1].Summary, throughs[^1]), (agent.Summary, agent.SummaryThrough));
        Assert.Contains("User: write twenty files", client.SummaryRequests[0][1].Content);
        Assert.Contains("A summary of the session before these messages", client.SummaryRequests[^1][1].Content);

        foreach (var request in client.Requests)
        {
            // System prompt, then the user, then whole steps; and the request fits the window.
            Assert.Equal(ChatRole.System, request[0].Role);
            Assert.Equal(ChatRole.User, request[1].Role);
            Assert.StartsWith("write twenty files", request[1].Content);
            Assert.Equal(request.Skip(1).Select(m => (m.Role, m.ToolCallId)), CodeAgent.Repair(request.Skip(1)).Select(m => (m.Role, m.ToolCallId)));
            var tools = client.Options[client.Requests.IndexOf(request)].Tools!;
            var estimate = TokenEstimator.Estimate(request) + tools.Sum(t => TokenEstimator.Estimate(t.Name + t.Description + t.Parameters.GetRawText()));
            Assert.True(estimate < agent.ContextWindow, $"request of ~{estimate} tokens");

            // Only the latest step carries a whole file.
            var calls = request.SelectMany(m => m.ToolCalls).ToList();
            Assert.All(calls.SkipLast(1), c => Assert.True(c.Arguments.Length < 300, c.Arguments));
        }
        var afterFirst = client.Requests.Last();
        Assert.Contains(CodeCompactor.SummaryPreamble, afterFirst[0].Content);
        Assert.EndsWith(CodeCompactor.ResentNote, afterFirst[1].Content);

        // The stored transcript is untouched: the prompt once, every call and result, the closing reply.
        Assert.Equal(1 + steps * 2 + 1, history.Count);
        Assert.Equal("write twenty files", history[0].Content);
        Assert.Single(history, m => m.Role == ChatRole.User);
        Assert.Contains(new string('a', 3000), history[1].ToolCalls[0].Arguments);
    }

    [Fact]
    public async Task ResumesFromAStoredCheckpointInsideATurn()
    {
        // Reloaded mid-turn: the checkpoint is the second step, so the prompt is sent again before it.
        List<ChatMessage> history =
        [
            new(ChatRole.User, "tidy notes"),
            new(ChatRole.Assistant, "") { ToolCalls = [Read("a")] },
            new(ChatRole.Tool, "Status: draft") { ToolCallId = "a", ToolName = CodeTools.ReadFile },
            new(ChatRole.Assistant, "") { ToolCalls = [Read("b")] },
            new(ChatRole.Tool, "Status: draft") { ToolCallId = "b", ToolName = CodeTools.ReadFile },
            new(ChatRole.User, "go on"),
        ];
        var client = new ScriptedClient([]);

        await new CodeAgent(client, "m", _workspace) { Summary = "Read notes.md once.", SummaryThrough = 3 }
            .RunAsync(history, new RecordingObserver(PermissionChoice.Allow), CancellationToken.None);

        var request = client.Requests[0];
        Assert.Equal([ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.User], request.Select(m => m.Role));
        Assert.Equal($"tidy notes\n\n{CodeCompactor.ResentNote}", request[1].Content);
        Assert.Equal("b", request[3].ToolCallId);
    }

    [Fact]
    public async Task UsesTheReportedUsageToDecideAndCarriesOnWhenSummarisingFails()
    {
        // Tiny messages, but the server said the last request used 900 of 1000 tokens.
        List<ChatMessage> history = [new(ChatRole.User, "one"), new(ChatRole.Assistant, "ok"), new(ChatRole.User, "two"), new(ChatRole.Assistant, "ok"), new(ChatRole.User, "three")];
        var client = new ScriptedClient([Read()], []) { FailSummaries = true };
        var observer = new RecordingObserver(PermissionChoice.Allow);
        var agent = new CodeAgent(client, "m", _workspace) { ContextWindow = 1000, LastUsage = (900, 4) };

        var end = await agent.RunAsync(history, observer, CancellationToken.None);

        Assert.Equal(CodeTurnEnd.Done, end);
        Assert.Single(client.SummaryRequests); // tried once, not again on the next step
        Assert.Single(observer.Failures);
        Assert.Null(agent.Summary);
        Assert.Equal(2, client.Requests.Count);
    }

    [Fact]
    public async Task SendsOnlyTheMessagesAfterAStoredCheckpoint()
    {
        List<ChatMessage> history = [new(ChatRole.User, "old"), new(ChatRole.Assistant, "old reply"), new(ChatRole.User, "new")];
        var client = new ScriptedClient([]);

        await new CodeAgent(client, "m", _workspace) { Summary = "The user said old.", SummaryThrough = 2 }
            .RunAsync(history, new RecordingObserver(PermissionChoice.Allow), CancellationToken.None);

        Assert.Equal([ChatRole.System, ChatRole.User], client.Requests[0].Select(m => m.Role));
        Assert.EndsWith(CodeCompactor.SummarySection("The user said old.") + "\n", client.Requests[0][0].Content);
        Assert.Empty(client.SummaryRequests); // no window known: never compacts on its own
    }

    /// <summary>Answers each request with the next scripted list of calls (an empty list is a plain "Done." reply); a request without tools is a summary.</summary>
    private sealed class ScriptedClient(params IReadOnlyList<ToolCall>[] steps) : ILlmClient
    {
        private int _step;

        public List<List<ChatMessage>> Requests { get; } = [];
        public List<ChatOptions> Options { get; } = [];
        public List<List<ChatMessage>> SummaryRequests { get; } = [];
        public bool FailSummaries { get; init; }

        public Guid ConnectionId { get; } = Guid.NewGuid();
        public string BackendName => "scripted";
        public string? DefaultModel => null;

        public async IAsyncEnumerable<ChatDelta> StreamChatAsync(string model, IReadOnlyList<ChatMessage> messages, ChatOptions? options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (options?.Tools is null)
            {
                SummaryRequests.Add([.. messages]);
                await Task.Yield();
                if (FailSummaries)
                {
                    throw new InvalidOperationException("summary failed");
                }
                yield return ChatDelta.FromText("<think>hmm</think>Summary of earlier work.");
                yield break;
            }
            Requests.Add([.. messages]);
            Options.Add(options);
            var calls = _step < steps.Length ? steps[_step++] : [];
            await Task.Yield();
            yield return ChatDelta.FromText(calls.Count == 0 ? "Done." : "Working.");
            foreach (var call in calls)
            {
                yield return ChatDelta.FromToolCall(call);
            }
            yield return ChatDelta.FromUsage(new TokenUsage(100, 10));
        }

        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>(["m"]);
        public Task<int?> GetContextWindowAsync(string model, CancellationToken cancellationToken = default) => Task.FromResult<int?>(null);
        public Task<bool?> SupportsImagesAsync(string model, CancellationToken cancellationToken = default) => Task.FromResult<bool?>(null);
    }

    private sealed class RecordingObserver(PermissionChoice answer, string? feedback = null) : ICodeAgentObserver
    {
        public List<ToolInvocation> Asked { get; } = [];
        public List<CodeCompaction> Compactions { get; } = [];
        public List<string> Failures { get; } = [];

        public void OnCompacting()
        {
        }

        public Task OnCompactedAsync(CodeCompaction compaction)
        {
            Compactions.Add(compaction);
            return Task.CompletedTask;
        }

        public void OnCompactionFailed(string error) => Failures.Add(error);

        public void OnStepStarted()
        {
        }

        public void OnDelta(ChatDelta delta)
        {
        }

        public Task OnAssistantMessageAsync(ChatMessage message, GenerationStats stats) => Task.CompletedTask;

        public Task<PermissionDecision> RequestPermissionAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            Asked.Add(invocation);
            return Task.FromResult(new PermissionDecision(answer, feedback));
        }

        public void OnToolStarted(ToolInvocation invocation)
        {
        }

        public void OnToolOutput(ToolInvocation invocation, string chunk)
        {
        }

        public Task OnToolFinishedAsync(ToolInvocation invocation, ToolResult result, ChatMessage resultMessage) => Task.CompletedTask;
    }
}

public sealed class SqliteCodeSessionStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "butterknife-tests", Guid.NewGuid().ToString("N"));
    private static readonly Guid Me = Guid.NewGuid();
    private static readonly Guid Someone = Guid.NewGuid();
    private static readonly Guid Conn = Guid.NewGuid();
    private readonly SqliteCodeSessionStore _store;

    public SqliteCodeSessionStoreTests()
    {
        var db = new SqliteDatabase(Microsoft.Extensions.Options.Options.Create(new DatabaseOptions { ConnectionString = $"Data Source={Path.Combine(_dir, "test.db")}" }));
        _store = new SqliteCodeSessionStore(db);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task RoundTripsATranscriptWithToolCallsResultsAndDiffs()
    {
        var session = await _store.CreateAsync(Me, "Fix the build", "/work/app", Conn, "qwen3", CodePermissionMode.Ask, CancellationToken.None);
        await _store.AppendAsync(session.Id, new ChatMessage(ChatRole.User, "fix it"), cancellationToken: CancellationToken.None);
        await _store.AppendAsync(session.Id, new ChatMessage(ChatRole.Assistant, "Looking.")
        {
            Reasoning = "hmm",
            Model = "qwen3",
            ToolCalls = [new ToolCall("c1", CodeTools.EditFile, """{"path":"a.cs"}""")],
            Stats = new GenerationStats(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(0.5), 10, new TokenUsage(100, 20)),
        }, cancellationToken: CancellationToken.None);
        await _store.AppendAsync(session.Id, new ChatMessage(ChatRole.Tool, "Edited a.cs.") { ToolCallId = "c1", ToolName = CodeTools.EditFile, IsError = false }, "-old\n+new\n", cancellationToken: CancellationToken.None);

        var loaded = await _store.GetAsync(session.Id, Me, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(("Fix the build", "/work/app", Conn, "qwen3", CodePermissionMode.Ask), (loaded.Title, loaded.Workspace, loaded.ConnectionId, loaded.Model, loaded.Mode));
        Assert.Equal(3, loaded.Entries.Count);
        var reply = loaded.Entries[1].Message;
        Assert.Equal(new ToolCall("c1", CodeTools.EditFile, """{"path":"a.cs"}"""), Assert.Single(reply.ToolCalls));
        Assert.Equal("hmm", reply.Reasoning);
        Assert.Equal(100, reply.Stats!.PromptTokens);
        var result = loaded.Entries[2];
        Assert.Equal(("c1", CodeTools.EditFile), (result.Message.ToolCallId, result.Message.ToolName));
        Assert.Equal("-old\n+new\n", result.Diff);
    }

    [Fact]
    public async Task KeepsWhatWasTypedBesideTheExpandedPrompt()
    {
        var session = await _store.CreateAsync(Me, "/review", "/a", Conn, "m", CodePermissionMode.Ask, CancellationToken.None);
        await _store.AppendAsync(session.Id, new ChatMessage(ChatRole.User, "Review the diff. Focus on: tests"), display: "/review tests", cancellationToken: CancellationToken.None);
        await _store.AppendAsync(session.Id, new ChatMessage(ChatRole.User, "plain prompt"), cancellationToken: CancellationToken.None);

        var entries = (await _store.GetAsync(session.Id, Me, CancellationToken.None))!.Entries;

        Assert.Equal(("Review the diff. Focus on: tests", "/review tests"), (entries[0].Message.Content, entries[0].Display));
        Assert.Null(entries[1].Display);
    }

    [Fact]
    public async Task SessionsBelongToTheirUser()
    {
        var mine = await _store.CreateAsync(Me, "Mine", "/a", Conn, "m", CodePermissionMode.Ask, CancellationToken.None);
        await _store.CreateAsync(Someone, "Theirs", "/b", Conn, "m", CodePermissionMode.Ask, CancellationToken.None);

        Assert.Null(await _store.GetAsync(mine.Id, Someone, CancellationToken.None));
        Assert.Equal(["Mine"], (await _store.ListAsync(Me, CancellationToken.None)).Select(s => s.Title));
        Assert.Equal(["/a"], await _store.RecentWorkspacesAsync(Me, cancellationToken: CancellationToken.None));
    }

    [Fact]
    public async Task UpdatesAndDeletes()
    {
        var session = await _store.CreateAsync(Me, "Old", "/a", Conn, "m", CodePermissionMode.Ask, CancellationToken.None);
        await _store.AppendAsync(session.Id, new ChatMessage(ChatRole.User, "hi"), cancellationToken: CancellationToken.None);
        var other = Guid.NewGuid();

        await _store.SetTitleAsync(session.Id, "  New  ", CancellationToken.None);
        await _store.SetModelAsync(session.Id, other, "m2", CancellationToken.None);
        await _store.SetModeAsync(session.Id, CodePermissionMode.Auto, CancellationToken.None);
        await _store.SetContextAsync(session.Id, 1234, 8192, CancellationToken.None);
        var loaded = await _store.GetAsync(session.Id, Me, CancellationToken.None);

        Assert.Equal(("New", other, "m2", CodePermissionMode.Auto, 1234, 8192), (loaded!.Title, loaded.ConnectionId, loaded.Model, loaded.Mode, loaded.ContextTokens, loaded.ContextWindow));

        await _store.DeleteAsync(session.Id, CancellationToken.None);
        Assert.Null(await _store.GetAsync(session.Id, Me, CancellationToken.None));
    }

    [Fact]
    public async Task StoresAndClearsTheCompactionCheckpoint()
    {
        var session = await _store.CreateAsync(Me, "S", "/a", Conn, "m", CodePermissionMode.Ask, CancellationToken.None);
        Assert.Equal((null, 0), (session.Summary, session.SummaryThrough));

        await _store.SetSummaryAsync(session.Id, "Read a.cs.", 6, CancellationToken.None);
        var loaded = await _store.GetAsync(session.Id, Me, CancellationToken.None);
        Assert.Equal(("Read a.cs.", 6), (loaded!.Summary, loaded.SummaryThrough));

        await _store.SetSummaryAsync(session.Id, null, null, CancellationToken.None);
        loaded = await _store.GetAsync(session.Id, Me, CancellationToken.None);
        Assert.Equal((null, 0), (loaded!.Summary, loaded.SummaryThrough));
    }
}
