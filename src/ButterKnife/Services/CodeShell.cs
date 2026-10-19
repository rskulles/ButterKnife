using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace ButterKnife.Services;

/// <summary>
/// The Code tab's shell for one session: what a terminal would keep between commands, without a terminal. Each
/// run_command is still its own process (so a hung command cannot wedge the next one and timeouts kill cleanly), but a
/// small wrapper restores the previous command's working directory and exported variables first and saves them
/// afterwards, so <c>cd src</c> or <c>export FOO=1</c> carry over like in a terminal. Background commands (servers,
/// watchers) run on beside it until they exit, are stopped, or the session or ButterKnife goes away.
/// On Windows only the working directory carries over.
/// </summary>
public sealed class CodeShell : IDisposable
{
    /// <summary>Background commands one session may have running at once.</summary>
    public const int MaxRunning = 8;

    private const string PosixWrapper = """
        __bk_cmd=$BK_CMD; __bk_state=$BK_STATE; __bk_cwd=$BK_CWD; __bk_save=$BK_SAVE
        unset BK_CMD BK_STATE BK_CWD BK_SAVE
        [ -f "$__bk_state/env" ] && . "$__bk_state/env" >/dev/null 2>&1
        cd -- "$__bk_cwd" 2>/dev/null
        eval "$__bk_cmd"
        __bk_status=$?
        if [ "$__bk_save" = 1 ]; then
          pwd > "$__bk_state/cwd" 2>/dev/null
          export -p > "$__bk_state/env" 2>/dev/null
        fi
        exit $__bk_status
        """;

    private const string PowerShellWrapper = """
        $__bk_cmd = $env:BK_CMD; $__bk_state = $env:BK_STATE; $__bk_save = $env:BK_SAVE
        Remove-Item Env:BK_CMD, Env:BK_STATE, Env:BK_CWD, Env:BK_SAVE -ErrorAction SilentlyContinue
        Invoke-Expression $__bk_cmd
        $__bk_status = if ($LASTEXITCODE) { $LASTEXITCODE } elseif ($?) { 0 } else { 1 }
        if ($__bk_save -eq '1') { (Get-Location).ProviderPath | Set-Content -LiteralPath (Join-Path $__bk_state 'cwd') }
        exit $__bk_status
        """;

    private readonly string _stateDirectory = Path.Combine(Path.GetTempPath(), "butterknife-shell-" + Guid.NewGuid().ToString("N"));
    private readonly List<BackgroundCommand> _background = [];
    private readonly object _gate = new();
    private int _nextId = 1;
    private bool _disposed;

    public CodeShell(string root)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        WorkingDirectory = Root;
        Directory.CreateDirectory(_stateDirectory);
    }

    /// <summary>The project folder.</summary>
    public string Root { get; }

    /// <summary>Command prefixes the user allowed "always" in this session (CodeAgent.AllowedCommands); kept here so they outlive page reloads.</summary>
    public HashSet<string> AllowedCommands { get; } = new(StringComparer.Ordinal);

    /// <summary>Where the next command starts: where the last one ended (the project folder until something cds).</summary>
    public string WorkingDirectory { get; private set; }

    /// <summary>A background command started, printed something, exited or was removed. May fire on any thread.</summary>
    public event Action? Changed;

    /// <summary>Background commands, oldest first, including ones that have exited until they are removed.</summary>
    public IReadOnlyList<BackgroundCommand> Background
    {
        get
        {
            lock (_gate)
            {
                return _background.ToArray();
            }
        }
    }

    /// <summary>The working directory as the transcript shows it: relative to the project folder when inside it.</summary>
    public string DisplayDirectory
    {
        get
        {
            var relative = Path.GetRelativePath(Root, WorkingDirectory);
            return relative == "." ? "." : relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? WorkingDirectory : relative.Replace(Path.DirectorySeparatorChar, '/');
        }
    }

    /// <summary>Runs a command to the end in the saved working directory and environment, then saves them again.</summary>
    public async Task<ShellResult> RunAsync(string command, TimeSpan timeout, Action<string>? onOutput, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var result = await ShellCommand.RunAsync(StartInfo(command, save: true), timeout, onOutput, cancellationToken);
        ReadState();
        return result;
    }

    /// <summary>Starts a command in the background and returns at once. Throws <see cref="CodeToolException"/> past <see cref="MaxRunning"/>.</summary>
    public BackgroundCommand Start(string command)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        BackgroundCommand background;
        lock (_gate)
        {
            if (_background.Count(b => b.IsRunning) >= MaxRunning)
            {
                throw new CodeToolException($"{MaxRunning} background commands are already running. Stop one with command_output (stop: true) first.");
            }
            background = new BackgroundCommand($"bg{_nextId++}", command, DisplayDirectory, StartInfo(command, save: false), () => Changed?.Invoke());
            _background.Add(background);
        }
        background.Start();
        Changed?.Invoke();
        return background;
    }

    public BackgroundCommand? Find(string id)
    {
        lock (_gate)
        {
            return _background.FirstOrDefault(b => string.Equals(b.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Stops a background command if it is still running and drops it from the list.</summary>
    public void Remove(string id)
    {
        BackgroundCommand? background;
        lock (_gate)
        {
            background = _background.FirstOrDefault(b => b.Id == id);
            if (background is not null)
            {
                _background.Remove(background);
            }
        }
        background?.Stop();
        background?.Dispose();
        Changed?.Invoke();
    }

    private ProcessStartInfo StartInfo(string command, bool save)
    {
        var start = Directory.Exists(WorkingDirectory) ? WorkingDirectory : Root;
        return ShellCommand.CreateStartInfo(OperatingSystem.IsWindows() ? PowerShellWrapper : PosixWrapper, start, new Dictionary<string, string>
        {
            ["BK_CMD"] = command,
            ["BK_STATE"] = _stateDirectory,
            ["BK_CWD"] = start,
            ["BK_SAVE"] = save ? "1" : "0",
        });
    }

    private void ReadState()
    {
        try
        {
            var cwd = Path.Combine(_stateDirectory, "cwd");
            if (File.Exists(cwd) && File.ReadAllText(cwd).Trim() is { Length: > 0 } path && Directory.Exists(path))
            {
                WorkingDirectory = Path.TrimEndingDirectorySeparator(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // keep the previous directory
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        foreach (var background in Background)
        {
            background.Stop();
            background.Dispose();
        }
        try
        {
            Directory.Delete(_stateDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// A command running in the background: its output is kept (the last <see cref="MaxKeptChars"/> characters) and read in
/// pieces, each read returning what arrived since the previous one.
/// </summary>
public sealed class BackgroundCommand : IDisposable
{
    public const int MaxKeptChars = 200_000;

    private readonly ProcessStartInfo _start;
    private readonly Action _changed;
    private readonly StringBuilder _output = new();
    private readonly object _gate = new();
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Process? _process;
    private long _dropped;
    private long _readTo;
    private TaskCompletionSource _more = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal BackgroundCommand(string id, string command, string directory, ProcessStartInfo start, Action changed)
    {
        Id = id;
        Command = command;
        Directory = directory;
        _start = start;
        _changed = changed;
    }

    public string Id { get; }
    public string Command { get; }

    /// <summary>Where it was started, as <see cref="CodeShell.DisplayDirectory"/> showed it then.</summary>
    public string Directory { get; }

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;
    public int? ExitCode { get; private set; }
    public bool Stopped { get; private set; }
    public bool IsRunning => !_exited.Task.IsCompleted;

    /// <summary>"running", "exited with code 0", "stopped".</summary>
    public string Status => IsRunning ? "running" : Stopped ? "stopped" : $"exited with code {ExitCode}";

    internal void Start()
    {
        var process = new Process { StartInfo = _start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        process.Exited += (_, _) =>
        {
            // Let the readers drain what was written just before the exit.
            process.WaitForExit();
            ExitCode = Stopped ? null : process.ExitCode;
            _exited.TrySetResult();
            Pulse();
        };
        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;
    }

    private void Append(string? line)
    {
        if (line is null)
        {
            return;
        }
        lock (_gate)
        {
            _output.Append(line).Append('\n');
            if (_output.Length > MaxKeptChars)
            {
                var cut = _output.Length - MaxKeptChars;
                _output.Remove(0, cut);
                _dropped += cut;
            }
        }
        Pulse();
    }

    private void Pulse()
    {
        TaskCompletionSource previous;
        lock (_gate)
        {
            previous = _more;
            _more = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        previous.TrySetResult();
        _changed();
    }

    /// <summary>
    /// Output since the previous read. With <paramref name="wait"/>, waits up to that long for something new (or the
    /// exit) when nothing has arrived yet, so the model can give a server time to start without a separate sleep.
    /// </summary>
    public async Task<string> ReadNewAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        Task more;
        lock (_gate)
        {
            more = _more.Task;
        }
        if (wait > TimeSpan.Zero && IsRunning && !HasUnread)
        {
            await Task.WhenAny(more, Task.Delay(wait, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            // A burst of output usually follows the first line; give it a moment to land.
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        lock (_gate)
        {
            var end = _dropped + _output.Length;
            var from = Math.Max(_readTo, _dropped);
            var skipped = from - _readTo;
            _readTo = end;
            var text = _output.ToString((int)(from - _dropped), (int)(end - from));
            return skipped > 0 ? $"[{skipped:N0} earlier characters were not kept]\n{text}" : text;
        }
    }

    private bool HasUnread
    {
        get
        {
            lock (_gate)
            {
                return _dropped + _output.Length > _readTo;
            }
        }
    }

    /// <summary>The last <paramref name="chars"/> characters of output, for the page (does not count as a read).</summary>
    public string Tail(int chars)
    {
        lock (_gate)
        {
            return _output.Length <= chars ? _output.ToString() : _output.ToString(_output.Length - chars, chars);
        }
    }

    /// <summary>Waits for the command to exit, up to <paramref name="timeout"/>; true when it has.</summary>
    public async Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        await Task.WhenAny(_exited.Task, Task.Delay(timeout, cancellationToken));
        return !IsRunning;
    }

    /// <summary>Kills the command and everything it started.</summary>
    public void Stop()
    {
        if (!IsRunning || _process is not { } process)
        {
            return;
        }
        Stopped = true;
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // already gone
        }
        _exited.Task.Wait(TimeSpan.FromSeconds(5));
    }

    public void Dispose() => _process?.Dispose();
}

/// <summary>
/// One <see cref="CodeShell"/> per Code session, kept for the life of the process so background commands and the
/// working directory survive a page reload. Deleting the session, or ButterKnife shutting down (the container disposes
/// this singleton), stops everything the session started.
/// </summary>
public sealed class CodeShellRegistry : IDisposable
{
    private readonly ConcurrentDictionary<Guid, CodeShell> _shells = new();

    public CodeShell Get(Guid sessionId, string root) => _shells.GetOrAdd(sessionId, _ => new CodeShell(root));

    public CodeShell? Find(Guid sessionId) => _shells.TryGetValue(sessionId, out var shell) ? shell : null;

    public void Remove(Guid sessionId)
    {
        if (_shells.TryRemove(sessionId, out var shell))
        {
            shell.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var id in _shells.Keys)
        {
            Remove(id);
        }
    }
}
