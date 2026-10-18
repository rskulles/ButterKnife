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

    [Fact]
    public void TheCheckpointIsAUserMessageKeepingTheLastPrompts()
    {
        var history = Turns(5); // prompts at 0, 4, 8, 12, 16

        Assert.Equal(12, CodeCompactor.FindCheckpoint(history, 0, 2));
        Assert.Equal(16, CodeCompactor.FindCheckpoint(history, 12, 2)); // only one more prompt: keep just the last
        Assert.Null(CodeCompactor.FindCheckpoint(history, 16, 2)); // the current prompt is all there is
        Assert.Equal(16, CodeCompactor.FindCheckpoint(history, 0, 0)); // keeps at least one prompt
        Assert.Null(CodeCompactor.FindCheckpoint(Turns(1), 0, 2));
    }

    [Fact]
    public void AStaleCheckpointMovesBackToAUserMessage()
    {
        var history = Turns(2);

        Assert.Equal(4, CodeCompactor.StartOf(history, 4));
        Assert.Equal(4, CodeCompactor.StartOf(history, 6)); // pointed at a tool result: start at its prompt instead
        Assert.Equal(0, CodeCompactor.StartOf(history, -3));
        Assert.Equal(history.Count, CodeCompactor.StartOf(history, 99));
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
