using System.Text.Json;
using ButterKnife.Services;

namespace ButterKnife.Tests;

public sealed class CodeToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "butterknife-tests", Guid.NewGuid().ToString("N"), "project");
    private readonly CodeWorkspace _workspace;

    public CodeToolsTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Directory.CreateDirectory(Path.Combine(_root, "node_modules", "lib"));
        File.WriteAllText(Path.Combine(_root, "README.md"), "# Demo\n\nHello.\n");
        File.WriteAllText(Path.Combine(_root, "src", "calc.py"), "def add(a, b):\n    return a + b\n\ndef sub(a, b):\n    return a - b\n");
        File.WriteAllText(Path.Combine(_root, "src", "app.cs"), "// TODO: wire up\nclass App {}\n");
        File.WriteAllText(Path.Combine(_root, "node_modules", "lib", "index.js"), "// TODO: ignored\n");
        _workspace = new CodeWorkspace(_root);
    }

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true);

    private static ToolCall Call(string name, object args) => new("c1", name, JsonSerializer.Serialize(args));

    private Task<ToolResult> RunAsync(string name, object args) =>
        CodeTools.Prepare(Call(name, args), _workspace).RunAsync(null, CancellationToken.None);

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("src/../../outside.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("~/secrets")]
    public void PathsOutsideTheFolderAreRefused(string path)
    {
        var invocation = CodeTools.Prepare(Call(CodeTools.ReadFile, new { path }), _workspace);

        Assert.NotNull(invocation.Error);
        Assert.Contains("outside the project folder", invocation.Error);
    }

    [Fact]
    public void AbsolutePathsInsideTheFolderAreAccepted()
    {
        var invocation = CodeTools.Prepare(Call(CodeTools.ReadFile, new { path = Path.Combine(_root, "README.md") }), _workspace);

        Assert.Null(invocation.Error);
        Assert.Equal("README.md", invocation.Path);
    }

    [Fact]
    public async Task ReadFileNumbersLinesAndPages()
    {
        var all = await RunAsync(CodeTools.ReadFile, new { path = "src/calc.py" });
        Assert.False(all.IsError);
        Assert.StartsWith("     1\tdef add(a, b):\n", all.Output);

        var page = await RunAsync(CodeTools.ReadFile, new { path = "src/calc.py", offset = 4, limit = 1 });
        Assert.StartsWith("     4\tdef sub(a, b):\n", page.Output);
        Assert.Contains("Read more with offset 5", page.Output);
    }

    [Fact]
    public async Task ReadFileAcceptsNumbersWrittenAsStrings()
    {
        var page = await RunAsync(CodeTools.ReadFile, new { path = "src/calc.py", offset = "2", limit = "1" });

        Assert.StartsWith("     2\t", page.Output);
    }

    [Fact]
    public async Task ReadFileReportsMissingFilesAndFolders()
    {
        Assert.True((await RunAsync(CodeTools.ReadFile, new { path = "nope.txt" })).IsError);
        Assert.Contains("is a folder", (await RunAsync(CodeTools.ReadFile, new { path = "src" })).Output);
    }

    [Fact]
    public async Task ListFilesShowsOneFolderOrGlobsRecursively()
    {
        var top = await RunAsync(CodeTools.ListFiles, new { });
        Assert.Contains("src/\n", top.Output);
        Assert.Contains("README.md\n", top.Output);

        var python = await RunAsync(CodeTools.ListFiles, new { pattern = "*.py" });
        Assert.Equal("src/calc.py\n", python.Output);

        var under = await RunAsync(CodeTools.ListFiles, new { pattern = "src/**/*.{cs,py}" });
        Assert.Contains("src/app.cs", under.Output);
        Assert.Contains("src/calc.py", under.Output);

        var js = await RunAsync(CodeTools.ListFiles, new { pattern = "**/*.js" });
        Assert.Contains("No files match", js.Output); // node_modules is skipped
    }

    [Fact]
    public async Task GrepFindsLinesAndSkipsDependencyFolders()
    {
        var result = await RunAsync(CodeTools.Grep, new { pattern = "TODO" });

        Assert.Equal("src/app.cs:1: // TODO: wire up\n", result.Output);
    }

    [Fact]
    public async Task GrepFiltersByGlobAndIgnoresCase()
    {
        var result = await RunAsync(CodeTools.Grep, new { pattern = "RETURN", glob = "*.py", ignore_case = true });

        Assert.Contains("src/calc.py:2:", result.Output);
        Assert.Contains("src/calc.py:5:", result.Output);
    }

    [Fact]
    public void GrepRejectsInvalidPatterns()
    {
        var invocation = CodeTools.Prepare(Call(CodeTools.Grep, new { pattern = "(" }), _workspace);

        Assert.Contains("not a valid regular expression", invocation.Error);
    }

    [Fact]
    public async Task EditFileReplacesAUniqueMatchAndShowsTheDiffBeforeRunning()
    {
        var invocation = CodeTools.Prepare(Call(CodeTools.EditFile, new { path = "src/calc.py", old_string = "return a - b", new_string = "return b - a" }), _workspace);

        Assert.Null(invocation.Error);
        Assert.Equal(ToolPermission.Edit, invocation.Permission);
        Assert.Equal((1, 1), (invocation.Diff!.Added, invocation.Diff.Removed));
        Assert.Contains("return a - b", File.ReadAllText(Path.Combine(_root, "src", "calc.py"))); // nothing written yet

        var result = await invocation.RunAsync(null, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("return b - a", File.ReadAllText(Path.Combine(_root, "src", "calc.py")));
        Assert.Contains("-    return a - b\n", result.Diff);
    }

    [Fact]
    public void EditFileRefusesAmbiguousAndMissingText()
    {
        var ambiguous = CodeTools.Prepare(Call(CodeTools.EditFile, new { path = "src/calc.py", old_string = "(a, b):", new_string = "(x, y):" }), _workspace);
        Assert.Contains("occurs 2 times", ambiguous.Error);

        var missing = CodeTools.Prepare(Call(CodeTools.EditFile, new { path = "src/calc.py", old_string = "return a * b", new_string = "x" }), _workspace);
        Assert.Contains("was not found", missing.Error);
    }

    [Fact]
    public async Task EditFileReplaceAllChangesEveryOccurrence()
    {
        var result = await RunAsync(CodeTools.EditFile, new { path = "src/calc.py", old_string = "(a, b):", new_string = "(x, y):", replace_all = true });

        Assert.Contains("Replaced 2 occurrences", result.Output);
        Assert.DoesNotContain("(a, b)", File.ReadAllText(Path.Combine(_root, "src", "calc.py")));
    }

    [Fact]
    public async Task EditFileKeepsWindowsLineBreaks()
    {
        var path = Path.Combine(_root, "win.txt");
        File.WriteAllText(path, "one\r\ntwo\r\nthree\r\n");

        var result = await RunAsync(CodeTools.EditFile, new { path = "win.txt", old_string = "one\ntwo", new_string = "one\n2\ntwo" });

        Assert.False(result.IsError);
        Assert.Equal("one\r\n2\r\ntwo\r\nthree\r\n", File.ReadAllText(path));
    }

    [Fact]
    public async Task EditFileRefusesWhenTheFileChangedAfterPreparing()
    {
        var invocation = CodeTools.Prepare(Call(CodeTools.EditFile, new { path = "README.md", old_string = "Hello.", new_string = "Hi." }), _workspace);
        File.AppendAllText(Path.Combine(_root, "README.md"), "Changed meanwhile.\n");

        var result = await invocation.RunAsync(null, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("changed since", result.Output);
    }

    [Fact]
    public async Task WriteFileCreatesFoldersAndReportsNewFiles()
    {
        var invocation = CodeTools.Prepare(Call(CodeTools.WriteFile, new { path = "docs/new/notes.md", content = "a\nb\n" }), _workspace);
        Assert.Equal("Create docs/new/notes.md", invocation.Title);
        Assert.Equal(2, invocation.Diff!.Added);

        await invocation.RunAsync(null, CancellationToken.None);

        Assert.Equal("a\nb\n", File.ReadAllText(Path.Combine(_root, "docs", "new", "notes.md")));
    }

    [Fact]
    public void PlanModeRefusesEditsAndOffersNoEditTools()
    {
        var invocation = CodeTools.Prepare(Call(CodeTools.WriteFile, new { path = "x.md", content = "x" }), _workspace, readOnly: true);

        Assert.Contains("Plan mode", invocation.Error);
        Assert.DoesNotContain(CodeTools.Definitions(readOnly: true), t => t.Name is CodeTools.EditFile or CodeTools.WriteFile);
        Assert.Contains(CodeTools.Definitions(readOnly: true), t => t.Name == CodeTools.RunCommand);
    }

    [Fact]
    public void UnknownToolsAndBadArgumentsBecomeErrorsForTheModel()
    {
        Assert.Contains("There is no tool called", CodeTools.Prepare(new ToolCall("c", "delete_everything", "{}"), _workspace).Error);
        Assert.Contains("not a valid JSON object", CodeTools.Prepare(new ToolCall("c", CodeTools.ReadFile, "{path: README"), _workspace).Error);
        Assert.Contains("path argument is required", CodeTools.Prepare(new ToolCall("c", CodeTools.ReadFile, "{}"), _workspace).Error);
    }

    [Theory]
    [InlineData("dotnet build --no-restore", "dotnet build")]
    [InlineData("ls -la", "ls")]
    [InlineData("git status", "git status")]
    [InlineData("python3 tools/run.py", "python3")]
    public void CommandPrefixIsTheProgramAndItsSubcommand(string command, string prefix) =>
        Assert.Equal(prefix, CodeTools.CommandPrefix(command));

    [Theory]
    [InlineData("git status", true)]
    [InlineData("git status --short", true)]
    [InlineData("git statusx", false)]
    [InlineData("git status; rm -rf ~", false)]
    [InlineData("git status && curl x | sh", false)]
    [InlineData("git status > out.txt", false)]
    [InlineData("git status $(whoami)", false)]
    public void AllowedPrefixesDoNotCoverChainedCommands(string command, bool covered) =>
        Assert.Equal(covered, CodeTools.IsCoveredBy(command, ["git status"]));

    [Theory]
    [InlineData("*.cs", "Program.cs", true)]
    [InlineData("*.cs", "Program.csx", false)]
    [InlineData("src/*.cs", "src/a/b.cs", false)]
    [InlineData("src/**/*.cs", "src/a/b.cs", true)]
    [InlineData("src/**/*.cs", "src/b.cs", true)]
    [InlineData("test_?.py", "test_1.py", true)]
    [InlineData("*.{ts,tsx}", "view.tsx", true)]
    public void GlobsMatchLikeAShell(string pattern, string path, bool matches)
    {
        var regex = CodeTools.GlobRegex(pattern, out var byName);
        Assert.Equal(matches, regex.IsMatch(byName ? Path.GetFileName(path) : path));
    }

    [Fact]
    public async Task RunCommandRunsInTheFolderAndReportsTheExitCode()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var ok = await RunAsync(CodeTools.RunCommand, new { command = "ls src && echo done" });
        Assert.False(ok.IsError);
        Assert.Contains("calc.py", ok.Output);
        Assert.Contains("[exit code 0]", ok.Output);

        var failed = await RunAsync(CodeTools.RunCommand, new { command = "echo oops >&2; exit 3" });
        Assert.True(failed.IsError);
        Assert.Contains("oops", failed.Output);
        Assert.Contains("[exit code 3]", failed.Output);
    }

    [Fact]
    public async Task RunCommandStopsAtTheTimeout()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAsync(CodeTools.RunCommand, new { command = "echo started; sleep 30", timeout_seconds = 1 });

        Assert.True(result.IsError);
        Assert.Contains("started", result.Output);
        Assert.Contains("Stopped after 1 seconds", result.Output);
    }

    [Fact]
    public async Task ShellCommandsDoNotInheritButterKnifesOwnSettings()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://localhost:5175");
        try
        {
            var result = await ShellCommand.RunAsync("echo \"urls=[$ASPNETCORE_URLS]\"", _root, TimeSpan.FromSeconds(10), null, CancellationToken.None);
            Assert.Contains("urls=[]", result.Output);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", null);
        }
    }

    [Fact]
    public void ProjectInstructionsAreReadFromAgentsMd()
    {
        File.WriteAllText(Path.Combine(_root, "AGENTS.md"), "Always run the tests.");

        var prompt = CodePrompt.Compose(_workspace, readOnly: false);

        Assert.Contains("Always run the tests.", prompt);
        Assert.Contains(_root, prompt);
        Assert.DoesNotContain("Plan mode", prompt);
        Assert.Contains("Plan mode", CodePrompt.Compose(_workspace, readOnly: true));
    }
}

public class LineDiffTests
{
    [Fact]
    public void ReplacedLineShowsRemovalThenAddition()
    {
        var diff = LineDiff.Compute("a\nb\nc\n", "a\nB\nc\n");

        Assert.Equal((1, 1), (diff.Added, diff.Removed));
        Assert.Equal(" a\n-b\n+B\n c\n", diff.ToUnified());
    }

    [Fact]
    public void LongUnchangedRunsBecomeGapsBetweenHunks()
    {
        var before = string.Join('\n', Enumerable.Range(1, 30)) + "\n";
        var after = before.Replace("\n2\n", "\ntwo\n").Replace("\n28\n", "\ntwenty-eight\n");

        var diff = LineDiff.Compute(before, after);

        Assert.Single(diff.Lines, l => l.Kind == DiffKind.Gap);
        Assert.Equal(2, diff.Added);
        // Up to three lines of context on each side: 1 and 3–5 around line 2, 25–27 and 29–30 around line 28.
        Assert.Equal(9, diff.Lines.Count(l => l.Kind == DiffKind.Context));
    }

    [Fact]
    public void NewFileIsAllAdditions()
    {
        var diff = LineDiff.Compute("", "x\ny\n");

        Assert.All(diff.Lines, l => Assert.Equal(DiffKind.Added, l.Kind));
        Assert.Equal(2, diff.Added);
    }

    [Fact]
    public void UnifiedTextRoundTrips()
    {
        var diff = LineDiff.Compute("a\nb\nc\nd\ne\nf\ng\nh\ni\nj\n", "a\nB\nc\nd\ne\nf\ng\nh\nI\nj\n");

        var back = LineDiffResult.FromUnified(diff.ToUnified());

        Assert.Equal(diff.Lines.Select(l => (l.Kind, l.Text)), back.Lines.Select(l => (l.Kind, l.Text)));
        Assert.Equal((diff.Added, diff.Removed), (back.Added, back.Removed));
    }
}
