using ButterKnife.Services;

namespace ButterKnife.Tests;

public class CodeCompactorTests
{
    private static ToolCall Call(string id, string name = CodeTools.ReadFile, string args = """{"path":"a.cs"}""") => new(id, name, args);

    private static ChatMessage Result(string id, string content, string name = CodeTools.ReadFile, bool error = false) =>
        new(ChatRole.Tool, content) { ToolCallId = id, ToolName = name, IsError = error };

    /// <summary>prompt, call, result, reply — <paramref name="count"/> times.</summary>
    private static List<ChatMessage> Turns(int count, int resultChars = 10)
    {
        var history = new List<ChatMessage>();
        for (var i = 0; i < count; i++)
        {
            history.Add(new(ChatRole.User, $"prompt {i}"));
            history.Add(new(ChatRole.Assistant, "") { ToolCalls = [Call($"c{i}")] });
            history.Add(Result($"c{i}", new string('x', resultChars)));
            history.Add(new(ChatRole.Assistant, $"reply {i}"));
        }
        return history;
    }

    /// <summary>One prompt, then <paramref name="steps"/> steps of one call and its result each, then a reply if <paramref name="done"/>.</summary>
    private static List<ChatMessage> LongTurn(int steps, bool done = false, int resultChars = 10)
    {
        var history = new List<ChatMessage> { new(ChatRole.User, "do the big task") };
        for (var i = 0; i < steps; i++)
        {
            history.Add(new(ChatRole.Assistant, "") { ToolCalls = [Call($"s{i}")] });
            history.Add(Result($"s{i}", new string('x', resultChars)));
        }
        if (done)
        {
            history.Add(new(ChatRole.Assistant, "All done."));
        }
        return history;
    }

    [Fact]
    public void TheCheckpointIsAUserMessageKeepingTheLastPrompts()
    {
        var history = Turns(5); // prompts at 0, 4, 8, 12, 16

        Assert.Equal(12, CodeCompactor.FindCheckpoint(history, 0, 2));
        Assert.Equal(8, CodeCompactor.FindCheckpoint(history, 0, 3));
        Assert.Equal(16, CodeCompactor.FindCheckpoint(history, 12, 2)); // only one more prompt: keep just the last
        Assert.Equal(16, CodeCompactor.FindCheckpoint(history, 4, 5)); // fewer than five follow: the last, not the first
        Assert.Equal(16, CodeCompactor.FindCheckpoint(history, 0, 0)); // keeps at least one prompt
    }

    [Fact]
    public void InsideOneTurnTheCheckpointKeepsItsLastSteps()
    {
        var history = LongTurn(6); // steps at 1, 3, 5, 7, 9, 11

        Assert.Equal(9, CodeCompactor.FindCheckpoint(history, 0, 2)); // the last two steps stay
        Assert.Equal(11, CodeCompactor.FindCheckpoint(history, 0, 2, keepSteps: 1));
        Assert.Equal(11, CodeCompactor.FindCheckpoint(history, 9, 2)); // already inside: at least the latest step stays
        Assert.Null(CodeCompactor.FindCheckpoint(history, 11, 2)); // the latest step is all there is
        Assert.Equal(3, CodeCompactor.FindCheckpoint(LongTurn(2), 0, 2)); // a short turn still gives up its first step
        Assert.Null(CodeCompactor.FindCheckpoint(LongTurn(1), 0, 2));

        // After an earlier exchange, a long turn in progress keeps only its last steps; a short one keeps its prompt.
        List<ChatMessage> earlier = [new(ChatRole.User, "hi"), new(ChatRole.Assistant, "hello")];
        Assert.Equal(2 + 9, CodeCompactor.FindCheckpoint([.. earlier, .. LongTurn(6)], 0, 2));
        Assert.Equal(2, CodeCompactor.FindCheckpoint([.. earlier, .. LongTurn(1)], 0, 2));

        // A finished turn: its closing reply is a step too.
        Assert.Equal(11, CodeCompactor.FindCheckpoint(LongTurn(6, done: true), 0, 2));
        Assert.Equal(3, CodeCompactor.FindCheckpoint(Turns(1), 0, 2));
    }

    [Fact]
    public void TheCheckpointNeverSplitsAStep()
    {
        List<ChatMessage> history =
        [
            new(ChatRole.User, "go"),
            new(ChatRole.Assistant, "") { ToolCalls = [Call("a"), Call("b")] },
            Result("a", "one"),
            Result("b", "two"),
            new(ChatRole.Assistant, "") { ToolCalls = [Call("c"), Call("d")] },
            Result("c", "three"), // d is not answered yet, as when the user stopped the turn
            new(ChatRole.Assistant, "") { ToolCalls = [Call("e")] },
            Result("e", "four"),
            new(ChatRole.Assistant, "Done."),
        ];

        Assert.Equal([0, 4, 8, 9], Enumerable.Range(0, history.Count + 1).Where(i => CodeCompactor.IsBoundary(history, i)));
        Assert.Equal(4, CodeCompactor.StartOf(history, 6)); // after an unanswered call: back to the last whole step
        Assert.Equal(4, CodeCompactor.StartOf(history, 7));
        Assert.Equal(8, CodeCompactor.StartOf(history, 8)); // the reply after e's result
        Assert.Equal(0, CodeCompactor.StartOf(history, 3)); // between a call and its results: back to the prompt
    }

    [Fact]
    public void AStaleCheckpointMovesBackToABoundary()
    {
        var history = Turns(2);

        Assert.Equal(4, CodeCompactor.StartOf(history, 4));
        Assert.Equal(4, CodeCompactor.StartOf(history, 6)); // pointed at a tool result: start at its prompt instead
        Assert.Equal(4, CodeCompactor.StartOf(history, 5)); // a call right after its prompt: the prompt
        Assert.Equal(7, CodeCompactor.StartOf(history, 7)); // the reply after a finished step
        Assert.Equal(0, CodeCompactor.StartOf(history, -3));
        Assert.Equal(history.Count, CodeCompactor.StartOf(history, 99));
    }

    [Fact]
    public void AfterACheckpointInsideATurnThePromptIsSentAgainFirst()
    {
        var history = LongTurn(6);
        history[0] = history[0] with { Id = 42 };

        var tail = CodeCompactor.RequestTail(history, 9);

        Assert.Equal([ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.Tool], tail.Select(m => m.Role));
        Assert.Equal($"do the big task\n\n{CodeCompactor.ResentNote}", tail[0].Content);
        Assert.Equal("do the big task", history[0].Content); // the stored prompt is not changed
        Assert.Same(history[9], tail[1]);
        Assert.Equal(tail.Select(m => m.Content), CodeAgent.Repair(tail).Select(m => m.Content)); // valid as it stands

        // At a prompt (or at the start) the tail is just the history from there.
        Assert.Equal(history, CodeCompactor.RequestTail(history, 0));
        var turns = Turns(3);
        Assert.Equal(turns.Skip(4), CodeCompactor.RequestTail(turns, 4));
        Assert.Equal(turns.Skip(4), CodeCompactor.RequestTail(turns, 6)); // stale: back to the prompt, nothing added
    }

    [Fact]
    public void EveryCheckpointLeavesAValidRequest()
    {
        // Wherever a checkpoint lands, what is sent starts with the user and keeps every call with its result.
        var history = Turns(2).Concat(LongTurn(5)).ToList();
        for (var through = 0; through <= history.Count; through++)
        {
            var tail = CodeCompactor.RequestTail(history, through);
            if (tail.Count == 0)
            {
                continue;
            }
            Assert.Equal(ChatRole.User, tail[0].Role);
            Assert.Equal(tail.Select(m => (m.Role, m.ToolCallId, m.Content)), CodeAgent.Repair(tail).Select(m => (m.Role, m.ToolCallId, m.Content)));
            if (CodeCompactor.FindCheckpoint(history, through, 2) is { } next)
            {
                Assert.True(next > CodeCompactor.StartOf(history, through));
                Assert.True(CodeCompactor.IsBoundary(history, next));
            }
        }
    }

    [Fact]
    public void ShrinksOldSuccessfulEditsAndKeepsTheLatestStep()
    {
        var big = new string('w', 2000);
        var write = new ToolCall("w", CodeTools.WriteFile, $$"""{"path":"src/Big.cs","content":"{{big}}"}""");
        var edit = new ToolCall("e", CodeTools.EditFile, $$"""{"path":"src/Big.cs","old_string":"{{new string('o', 300)}}","new_string":"{{new string('n', 500)}}\nend","replace_all":true}""");
        var failed = new ToolCall("f", CodeTools.WriteFile, $$"""{"path":"src/Other.cs","content":"{{big}}"}""");
        var latest = new ToolCall("l", CodeTools.WriteFile, $$"""{"path":"src/Last.cs","content":"{{big}}"}""");
        List<ChatMessage> messages =
        [
            new(ChatRole.User, "go"),
            new(ChatRole.Assistant, "") { ToolCalls = [write, Call("r")] },
            Result("w", "Wrote src/Big.cs (1 line).", CodeTools.WriteFile),
            Result("r", "read"),
            new(ChatRole.Assistant, "") { ToolCalls = [edit, failed] },
            Result("e", "Edited src/Big.cs.", CodeTools.EditFile),
            Result("f", "Permission denied", CodeTools.WriteFile, error: true),
            new(ChatRole.Assistant, "") { ToolCalls = [latest] },
            Result("l", "Wrote src/Last.cs (1 line).", CodeTools.WriteFile),
        ];

        var shrunk = CodeCompactor.ShrinkOldEdits(messages);

        using (var args = System.Text.Json.JsonDocument.Parse(shrunk[1].ToolCalls[0].Arguments))
        {
            Assert.Equal("src/Big.cs", args.RootElement.GetProperty("path").GetString());
            Assert.Equal("[2000 characters, 1 line; left out of the context to save space, the change was made]", args.RootElement.GetProperty("content").GetString());
        }
        Assert.Equal(("w", CodeTools.WriteFile), (shrunk[1].ToolCalls[0].Id, shrunk[1].ToolCalls[0].Name));
        Assert.Same(messages[1].ToolCalls[1], shrunk[1].ToolCalls[1]); // not an edit
        using (var args = System.Text.Json.JsonDocument.Parse(shrunk[4].ToolCalls[0].Arguments))
        {
            Assert.StartsWith("[300 characters, 1 line;", args.RootElement.GetProperty("old_string").GetString());
            Assert.StartsWith("[504 characters, 2 lines;", args.RootElement.GetProperty("new_string").GetString());
            Assert.True(args.RootElement.GetProperty("replace_all").GetBoolean());
        }
        Assert.Same(failed, shrunk[4].ToolCalls[1]); // it failed: the model may need to see what it tried
        Assert.Same(latest, shrunk[7].ToolCalls[0]); // the latest step is kept whole
        Assert.Same(messages[2], shrunk[2]);
        Assert.Contains(big, messages[1].ToolCalls[0].Arguments); // the input is not changed
        Assert.True(TokenEstimator.Estimate(shrunk) < TokenEstimator.Estimate(messages) - 500); // and the estimate sees arguments

        // Short edits cost little and stay as they are.
        var small = new ToolCall("s", CodeTools.EditFile, """{"path":"a.cs","old_string":"x","new_string":"y"}""");
        Assert.Same(small, CodeCompactor.ShrinkArguments(small));
    }

    [Fact]
    public void ClearsOlderLongResultsPastTheBudgetAndKeepsTheLatestStep()
    {
        List<ChatMessage> messages =
        [
            new(ChatRole.User, "go"),
            new(ChatRole.Assistant, "") { ToolCalls = [Call("a")] },
            Result("a", "first\n" + new string('a', 1000)),
            new(ChatRole.Assistant, "") { ToolCalls = [Call("b"), Call("e", CodeTools.EditFile)] },
            Result("b", new string('b', 1000)),
            Result("e", "Edited a.cs.", CodeTools.EditFile),
            new(ChatRole.Assistant, "") { ToolCalls = [Call("c"), Call("d")] },
            Result("c", new string('c', 3000)),
            Result("d", new string('d', 3000)),
        ];

        var clipped = CodeCompactor.ClipOldResults(messages, budgetChars: 6500);

        Assert.Equal(messages.Count, clipped.Count);
        Assert.Equal(messages[7].Content, clipped[7].Content); // the latest step, even though it alone is near the budget
        Assert.Equal(messages[8].Content, clipped[8].Content);
        Assert.StartsWith("[This read_file result", clipped[4].Content); // 7000 characters by then: past the budget
        Assert.Equal("Edited a.cs.", clipped[5].Content); // short results stay
        Assert.Equal("[This read_file result (2 lines) was cleared from the context to save space. Call the tool again if you still need it.]", clipped[2].Content);
        Assert.Equal(("a", CodeTools.ReadFile), (clipped[2].ToolCallId, clipped[2].ToolName));
        Assert.StartsWith("first", messages[2].Content); // the input is not changed

        var roomier = CodeCompactor.ClipOldResults(messages, budgetChars: 7500);
        Assert.Equal(messages[4].Content, roomier[4].Content);
        Assert.StartsWith("[This read_file result", roomier[2].Content);

        var tight = CodeCompactor.ClipOldResults(messages, budgetChars: 0);
        Assert.Equal(messages[8].Content, tight[8].Content);
        Assert.Equal(CodeAgent.Repair(messages).Select(m => m.ToolCallId), CodeAgent.Repair(tight).Select(m => m.ToolCallId)); // every call still has its result
    }

    [Fact]
    public void ResultBudgetFollowsTheWindow()
    {
        Assert.Equal(16_000, CodeCompactor.ResultBudget(8000));
        Assert.Equal(CodeCompactor.ResultBudget(CodeCompactor.FallbackWindow), CodeCompactor.ResultBudget(null));
    }

    [Fact]
    public void TheSummaryRequestShowsCallsResultsAndThePreviousSummary()
    {
        List<ChatMessage> older =
        [
            new(ChatRole.User, "fix the build"),
            new(ChatRole.Assistant, "Building.") { ToolCalls = [Call("r", CodeTools.RunCommand, """{"command":"dotnet build"}""")] },
            Result("r", "start " + new string('.', 5000) + " error CS1002: ; expected", CodeTools.RunCommand, error: true),
        ];

        var request = CodeCompactor.BuildSummaryRequest("Earlier: read Program.cs.", older);

        Assert.Equal(CodeCompactor.SummarizerSystemPrompt, request[0].Content);
        var text = request[1].Content;
        Assert.Contains("Earlier: read Program.cs.", text);
        Assert.Contains("User: fix the build", text);
        Assert.Contains("Assistant: Building.", text);
        Assert.Contains("""Assistant called run_command {"command":"dotnet build"}""", text);
        Assert.Contains("Result of run_command (error):", text);
        Assert.Contains("start", text);
        Assert.Contains("error CS1002", text); // the tail of a long output is kept
        Assert.True(text.Length < 2000);
    }

    [Fact]
    public void TheSummaryRequestShrinksToFit()
    {
        var older = Turns(40, resultChars: 2000);

        var roomy = CodeCompactor.BuildSummaryRequest(null, older)[1].Content;
        var fitted = CodeCompactor.BuildSummaryRequest(null, older, maxChars: 4000)[1].Content;

        Assert.True(roomy.Length > 30_000);
        Assert.True(fitted.Length < 4200);
        Assert.Contains("prompt 0", fitted);
        Assert.Contains("reply 39", fitted);
    }

    [Fact]
    public void ThePromptCarriesTheSummaryAtTheEnd()
    {
        var root = Path.Combine(Path.GetTempPath(), "butterknife-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var workspace = new CodeWorkspace(root);
            Assert.DoesNotContain(CodeCompactor.SummaryPreamble, CodePrompt.Compose(workspace, readOnly: false));
            var prompt = CodePrompt.Compose(workspace, readOnly: false, summary: "  Edited a.cs.  ");
            Assert.EndsWith($"{CodeCompactor.SummaryPreamble}\n\nEdited a.cs.\n\nThe messages that follow continue this session.\n", prompt);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
