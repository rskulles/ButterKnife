using System.Text.Json;
using ButterKnife.Services;

namespace ButterKnife.Tests;

/// <summary>The Code tab's persistent shell. POSIX only: the Windows wrapper is PowerShell and is not exercised here.</summary>
public sealed class CodeShellTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "butterknife-tests", Guid.NewGuid().ToString("N"));
    private readonly CodeShell _shell;
    private readonly CodeWorkspace _workspace;

    public CodeShellTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        _shell = new CodeShell(_root);
        _workspace = new CodeWorkspace(_root);
    }

    public void Dispose()
    {
        _shell.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private Task<ShellResult> RunAsync(string command) => _shell.RunAsync(command, Timeout, null, CancellationToken.None);

    private Task<ToolResult> ToolAsync(string name, object args) =>
        CodeTools.Prepare(new ToolCall("c1", name, JsonSerializer.Serialize(args)), _workspace, shell: _shell).RunAsync(null, CancellationToken.None);

    [Fact]
    public async Task TheWorkingDirectoryCarriesOver()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await RunAsync("cd sub");
        var pwd = await RunAsync("pwd");

        Assert.Equal("sub", _shell.DisplayDirectory);
        Assert.EndsWith("/sub\n", pwd.Output);
    }

    [Fact]
    public async Task ExportedVariablesCarryOverButShellLocalsDoNot()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await RunAsync("export GREETING='hello there'; LOCAL_ONLY=1");
        var echo = await RunAsync("echo \"[$GREETING][$LOCAL_ONLY]\"");

        Assert.Equal("[hello there][]\n", echo.Output);
    }

    [Fact]
    public async Task AFailingCommandKeepsItsExitCodeAndStillSavesState()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAsync("cd sub && false");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("sub", _shell.DisplayDirectory);
    }

    [Fact]
    public async Task ACommandSeesItsOwnTextNotThePreviousOne()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await RunAsync("echo first");
        var second = await RunAsync("echo second");

        Assert.Equal("second\n", second.Output);
    }

    [Fact]
    public async Task BackgroundCommandsRunOnAndCanBeReadAndStopped()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var background = _shell.Start("echo ready; sleep 30");
        var first = await background.ReadNewAsync(TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal("bg1", background.Id);
        Assert.Equal("ready\n", first);
        Assert.True(background.IsRunning);
        Assert.Equal("", await background.ReadNewAsync(TimeSpan.Zero, CancellationToken.None)); // already read

        background.Stop();

        Assert.False(background.IsRunning);
        Assert.Equal("stopped", background.Status);
    }

    [Fact]
    public async Task BackgroundCommandsStartInTheShellsDirectoryAndReportTheirExit()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await RunAsync("cd sub");
        var background = _shell.Start("pwd; exit 3");

        Assert.True(await background.WaitForExitAsync(Timeout, CancellationToken.None));
        Assert.Equal(3, background.ExitCode);
        Assert.Equal("exited with code 3", background.Status);
        Assert.EndsWith("/sub\n", await background.ReadNewAsync(TimeSpan.Zero, CancellationToken.None));
    }

    [Fact]
    public void DisposingTheShellStopsItsBackgroundCommands()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var registry = new CodeShellRegistry();
        var id = Guid.NewGuid();
        var background = registry.Get(id, _root).Start("sleep 30");

        registry.Remove(id);

        Assert.False(background.IsRunning);
        Assert.Null(registry.Find(id));
    }

    [Fact]
    public async Task RunCommandToolReportsAMoveAndStartsBackgroundCommands()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var moved = await ToolAsync(CodeTools.RunCommand, new { command = "cd sub" });
        Assert.Contains("[working directory is now sub]", moved.Output);

        var started = await ToolAsync(CodeTools.RunCommand, new { command = "echo listening; sleep 30", background = true });
        Assert.False(started.IsError);
        Assert.Contains("Started bg1 in sub", started.Output);
        Assert.Contains("listening", started.Output);

        var check = await ToolAsync(CodeTools.CommandOutput, new { id = "bg1" });
        Assert.Contains("(nothing new)", check.Output);
        Assert.Contains("running", check.Output);

        var stopped = await ToolAsync(CodeTools.CommandOutput, new { id = "bg1", stop = true });
        Assert.Contains("stopped", stopped.Output);
        Assert.False(_shell.Find("bg1")!.IsRunning);
    }

    [Fact]
    public async Task ABackgroundCommandThatExitsAtOnceSaysSo()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await ToolAsync(CodeTools.RunCommand, new { command = "echo nope >&2; exit 2", background = true });

        Assert.True(result.IsError);
        Assert.Contains("bg1 exited with code 2 straight away", result.Output);
        Assert.Contains("nope", result.Output);
    }

    [Fact]
    public void CommandOutputNeedsAKnownId()
    {
        var unknown = CodeTools.Prepare(new ToolCall("c", CodeTools.CommandOutput, """{"id":"bg9"}"""), _workspace, shell: _shell);
        Assert.Contains("none have been started", unknown.Error);

        var noShell = CodeTools.Prepare(new ToolCall("c", CodeTools.RunCommand, """{"command":"sleep 1","background":true}"""), _workspace);
        Assert.Contains("not available", noShell.Error);
    }

    [Fact]
    public async Task TheSystemPromptListsBackgroundCommandsAndAMovedDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await RunAsync("cd sub");
        _shell.Start("sleep 30");

        var prompt = CodePrompt.Compose(_workspace, readOnly: false, shell: _shell);

        Assert.Contains("run_command's working directory: sub", prompt);
        Assert.Contains("bg1 `sleep 30` (running)", prompt);
        Assert.Contains("background: true", prompt);
    }
}
