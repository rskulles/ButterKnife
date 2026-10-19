using System.Diagnostics;
using System.Text;

namespace ButterKnife.Services;

/// <summary>How a command ended. <see cref="ExitCode"/> is null when it was stopped (timeout or cancel) before exiting.</summary>
public sealed record ShellResult(int? ExitCode, string Output, bool TimedOut, bool Cancelled, bool Truncated);

/// <summary>
/// Runs one command line for the Code tab's run_command tool: through the user's login shell on macOS and Linux (so
/// PATH matches their terminal even when ButterKnife was started from the menu bar), PowerShell on Windows. Stdout and
/// stderr are merged line by line, stdin is closed at once (a command that waits for input gets end-of-file), and the
/// whole process tree is killed on timeout or cancel.
/// </summary>
public static class ShellCommand
{
    /// <summary>What the model gets back at most; the middle of longer output is cut.</summary>
    public const int MaxOutputChars = 30_000;

    /// <summary>What is kept in memory while a command runs; anything beyond is counted, not stored.</summary>
    private const int MaxBufferedChars = 1_000_000;

    /// <summary>Environment that belongs to ButterKnife itself and must not leak into the user's builds (ports, environment name, config overrides).</summary>
    private static readonly string[] StrippedPrefixes = ["ASPNETCORE_", "BUTTERKNIFE_", "DOTNET_WATCH", "DOTNET_STARTUP_HOOKS", "DOTNET_MODIFIABLE_ASSEMBLIES", "DOTNET_LAUNCH_PROFILE"];

    /// <summary>True for a variable that belongs to ButterKnife itself (its port, environment name, config overrides) and must not leak into programs it starts.</summary>
    public static bool IsButterKnifeVariable(string key) =>
        key.Contains("__", StringComparison.Ordinal) || StrippedPrefixes.Any(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    public static string ShellName => OperatingSystem.IsWindows() ? "PowerShell" : Path.GetFileName(UnixShell());

    public static Task<ShellResult> RunAsync(
        string command,
        string workingDirectory,
        TimeSpan timeout,
        Action<string>? onOutput,
        CancellationToken cancellationToken) =>
        RunAsync(CreateStartInfo(command, workingDirectory), timeout, onOutput, cancellationToken);

    /// <summary>
    /// The process for one script: the login shell (or PowerShell) with ButterKnife's own environment removed and plain,
    /// pager-free output. <paramref name="environment"/> is added on top (CodeShell passes its state file paths that way).
    /// </summary>
    public static ProcessStartInfo CreateStartInfo(string script, string workingDirectory, IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (OperatingSystem.IsWindows())
        {
            start.FileName = "powershell.exe";
            start.ArgumentList.Add("-NoLogo");
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add(script);
        }
        else
        {
            start.FileName = UnixShell();
            start.ArgumentList.Add("-l");
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(script);
        }

        foreach (var key in start.Environment.Keys.ToArray())
        {
            if (IsButterKnifeVariable(key))
            {
                start.Environment.Remove(key);
            }
        }
        // Plain output: no pagers waiting for a keypress, no colour codes in the transcript.
        start.Environment["PAGER"] = "cat";
        start.Environment["GIT_PAGER"] = "cat";
        start.Environment["TERM"] = "dumb";
        start.Environment["NO_COLOR"] = "1";
        foreach (var (key, value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[key] = value;
        }
        return start;
    }

    /// <summary>Runs a prepared process to the end (or the timeout / cancel, which kill its whole tree) and collects its output.</summary>
    public static async Task<ShellResult> RunAsync(ProcessStartInfo start, TimeSpan timeout, Action<string>? onOutput, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var dropped = 0;
        var gate = new object();
        void Append(string? line)
        {
            if (line is null)
            {
                return;
            }
            lock (gate)
            {
                if (output.Length + line.Length + 1 <= MaxBufferedChars)
                {
                    output.Append(line).Append('\n');
                }
                else
                {
                    dropped += line.Length + 1;
                }
            }
            onOutput?.Invoke(line + "\n");
        }

        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);

        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timer = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timer.Token, cancellationToken);
        var timedOut = false;
        var cancelled = false;
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = timer.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
            cancelled = !timedOut;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // already exited
            }
            // Give the readers a moment to drain what was written before the kill.
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ContinueWith(_ => { }, TaskScheduler.Default);
        }

        string text;
        lock (gate)
        {
            text = output.ToString();
        }
        var (clipped, truncated) = Clip(text, MaxOutputChars);
        if (dropped > 0)
        {
            truncated = true;
            clipped += $"\n[{dropped:N0} more characters of output were not kept]\n";
        }
        return new ShellResult(timedOut || cancelled ? null : process.ExitCode, clipped, timedOut, cancelled, truncated);
    }

    /// <summary>Keeps the head and the tail (errors usually come last) and says how much was cut between them.</summary>
    public static (string Text, bool Truncated) Clip(string text, int max)
    {
        if (text.Length <= max)
        {
            return (text, false);
        }
        var half = max / 2;
        return ($"{text[..half]}\n\n[... {text.Length - 2 * half:N0} characters cut ...]\n\n{text[^half..]}", true);
    }

    /// <summary>
    /// The user's login shell when it speaks POSIX sh (zsh, bash, sh, dash, ksh): CodeShell's state-saving wrapper is
    /// written in that dialect. fish and friends fall back to zsh on a Mac, bash elsewhere.
    /// </summary>
    public static string UnixShell()
    {
        var shell = Environment.GetEnvironmentVariable("SHELL");
        return !string.IsNullOrEmpty(shell) && File.Exists(shell) && Path.GetFileName(shell) is "zsh" or "bash" or "sh" or "dash" or "ksh" ? shell
            : File.Exists("/bin/zsh") && OperatingSystem.IsMacOS() ? "/bin/zsh"
            : "/bin/bash";
    }
}
