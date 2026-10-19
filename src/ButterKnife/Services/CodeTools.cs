using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ButterKnife.Services;

/// <summary>What a tool call needs from the user before it runs.</summary>
public enum ToolPermission
{
    /// <summary>Reads only: runs without asking.</summary>
    None,

    /// <summary>Changes a file in the project folder.</summary>
    Edit,

    /// <summary>Runs a command; anything is possible, so it always shows exactly what will run.</summary>
    Command,

    /// <summary>A tool from an MCP server: it may act outside the project folder, so it shows the tool and its arguments.</summary>
    External,
}

/// <summary>What a tool sends back: the text the model reads, whether it failed, and for edits the stored diff.</summary>
public sealed record ToolResult(string Output, bool IsError, string? Diff = null)
{
    public static ToolResult Fail(string message) => new(message, true);

    /// <summary>Pictures the model should see with the result (read_file on an image). Each client sends them its own way.</summary>
    public IReadOnlyList<ChatImage> Images { get; init; } = ChatMessage.NoImages;
}

/// <summary>
/// A tool call checked and ready to run: what the transcript shows (title, icon, diff or command) and what the user
/// is asked to allow. A call with bad arguments has <see cref="Error"/> set and is answered with it, without asking.
/// </summary>
public sealed class ToolInvocation
{
    private readonly Func<Action<string>?, CancellationToken, Task<ToolResult>>? _run;

    internal ToolInvocation(ToolCall call, string title, string icon, ToolPermission permission, Func<Action<string>?, CancellationToken, Task<ToolResult>>? run)
    {
        Call = call;
        Title = title;
        Icon = icon;
        Permission = permission;
        _run = run;
    }

    public ToolCall Call { get; }

    /// <summary>One line for the transcript, e.g. "Edit src/app.css".</summary>
    public string Title { get; }

    /// <summary>Bootstrap icon name without the "bi-" prefix.</summary>
    public string Icon { get; }

    public ToolPermission Permission { get; }

    public string? Error { get; private init; }

    /// <summary>For edits: the change as it will be made (computed before asking, so the user approves what runs).</summary>
    public LineDiffResult? Diff { get; init; }

    /// <summary>For run_command: the command line exactly as it will run.</summary>
    public string? Command { get; init; }

    /// <summary>For an MCP tool: its qualified name (what "Always allow" remembers) and its arguments, indented for reading.</summary>
    public string? ExternalTool { get; init; }

    public string? Arguments { get; init; }

    /// <summary>The path a file tool works on, relative to the project folder.</summary>
    public string? Path { get; init; }

    internal static ToolInvocation Invalid(ToolCall call, string title, string message) =>
        new(call, title, "exclamation-triangle", ToolPermission.None, null) { Error = message };

    public Task<ToolResult> RunAsync(Action<string>? onOutput, CancellationToken cancellationToken) =>
        Error is not null || _run is null ? Task.FromResult(ToolResult.Fail(Error ?? "This tool cannot run.")) : _run(onOutput, cancellationToken);
}

/// <summary>
/// The Code tab's tools. Kept few so the schemas cost a small local model little context: read_file, list_files,
/// grep, read_skill and command_output (never asked), edit_file and write_file (file changes) and run_command
/// (anything; it runs in the session's <see cref="CodeShell"/>, so the working directory and exported variables carry
/// over, and can start background commands). File tools stay inside the workspace folder; read_skill reads any listed
/// skill wherever it lives. Output is capped so one call cannot flood a small context window.
/// </summary>
public static class CodeTools
{
    public const string ReadFile = "read_file";
    public const string ListFiles = "list_files";
    public const string Grep = "grep";
    public const string EditFile = "edit_file";
    public const string WriteFile = "write_file";
    public const string RunCommand = "run_command";
    public const string ReadSkill = "read_skill";
    public const string CommandOutput = "command_output";

    /// <summary>How long a background start waits for first output, so the model sees at once whether it came up.</summary>
    public static readonly TimeSpan BackgroundFirstOutput = TimeSpan.FromSeconds(3);

    public const int MaxOutputWaitSeconds = 60;

    public const int DefaultReadLines = 1000;
    public const int MaxLineChars = 2000;
    public const int MaxReadChars = 40_000;

    /// <summary>Image files read_file hands to the model as pictures, by extension, and the largest it sends (larger: refused).</summary>
    public static readonly IReadOnlyDictionary<string, string> ImageTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
    };

    public const long MaxImageFileBytes = 5 * 1024 * 1024;
    public const int MaxListEntries = 400;
    public const int MaxGrepResults = 300;
    public const int DefaultCommandSeconds = 120;
    public const int MaxCommandSeconds = 600;

    private static readonly ToolDefinition[] All =
    [
        ToolDefinition.Create(ReadFile,
            "Read a file from the project. Text files come back as numbered lines (number, tab, text); use offset and limit to page through long ones. Images (png, jpg, gif, webp) come back as the picture itself, so you can look at screenshots, mockups and icons.",
            """
            {"type":"object","properties":{
              "path":{"type":"string","description":"File path relative to the project folder"},
              "offset":{"type":"integer","description":"First line to read, starting at 1 (default 1)"},
              "limit":{"type":"integer","description":"How many lines to read (default 1000)"}
            },"required":["path"]}
            """),
        ToolDefinition.Create(ListFiles,
            "List files. Without a pattern: the entries of one folder (folders end with /). With a glob pattern such as **/*.cs or src/**/test_*.py: matching files in all sub-folders. A pattern without / matches file names at any depth. Dependency and build folders are skipped.",
            """
            {"type":"object","properties":{
              "path":{"type":"string","description":"Folder relative to the project folder (default: the project folder)"},
              "pattern":{"type":"string","description":"Optional glob pattern"}
            }}
            """),
        ToolDefinition.Create(Grep,
            "Search file contents with a regular expression (.NET syntax). Returns path:line: text for each matching line.",
            """
            {"type":"object","properties":{
              "pattern":{"type":"string","description":"Regular expression to search for"},
              "path":{"type":"string","description":"File or folder to search (default: the project folder)"},
              "glob":{"type":"string","description":"Only search files whose path matches this glob, e.g. *.ts"},
              "ignore_case":{"type":"boolean","description":"Case-insensitive search"},
              "max_results":{"type":"integer","description":"Stop after this many matching lines (default 100)"}
            },"required":["pattern"]}
            """),
        ToolDefinition.Create(EditFile,
            "Replace text in a file. Copy old_string from your most recent read_file output of that file (without the line numbers), not from memory: it must match the file, including line breaks, and occur exactly once unless replace_all is true. Keep it short: the lines that change plus a line of context. For several changes, make several edit_file calls.",
            """
            {"type":"object","properties":{
              "path":{"type":"string","description":"File path relative to the project folder"},
              "old_string":{"type":"string","description":"Exact text to replace"},
              "new_string":{"type":"string","description":"Replacement text"},
              "replace_all":{"type":"boolean","description":"Replace every occurrence (default false)"}
            },"required":["path","old_string","new_string"]}
            """),
        ToolDefinition.Create(WriteFile,
            "Create a file, or replace a whole file's content. Prefer edit_file for changes to an existing file. Missing folders are created.",
            """
            {"type":"object","properties":{
              "path":{"type":"string","description":"File path relative to the project folder"},
              "content":{"type":"string","description":"The complete new content of the file"}
            },"required":["path","content"]}
            """),
        ToolDefinition.Create(ReadSkill,
            "Read a skill: step-by-step instructions for a kind of task, listed by name in the system prompt. Call it before starting a task that matches a skill.",
            """
            {"type":"object","properties":{
              "name":{"type":"string","description":"The skill's name, as listed"}
            },"required":["name"]}
            """),
        ToolDefinition.Create(RunCommand,
            "Run a shell command and return its output and exit code. Use it to build, run tests, use git and other command-line tools. Like a terminal, the working directory and exported variables carry over to the next command. Commands cannot read input, so avoid interactive programs. For servers, watchers and anything that keeps running, set background to true and read its output later with command_output.",
            """
            {"type":"object","properties":{
              "command":{"type":"string","description":"The command line to run"},
              "timeout_seconds":{"type":"integer","description":"Stop the command after this many seconds (default 120, at most 600); ignored for background commands"},
              "background":{"type":"boolean","description":"Start it and return at once with an id (default false)"}
            },"required":["command"]}
            """),
        ToolDefinition.Create(CommandOutput,
            "Read what a background command (started by run_command with background true) has printed since the last read, and whether it is still running. Set stop to true to stop it.",
            """
            {"type":"object","properties":{
              "id":{"type":"string","description":"The background command's id, such as bg1"},
              "wait_seconds":{"type":"integer","description":"If nothing new has been printed yet, wait up to this long for output (default 0, at most 60)"},
              "stop":{"type":"boolean","description":"Stop the command (default false)"}
            },"required":["id"]}
            """),
    ];

    /// <summary>The tools offered to the model; plan mode leaves out the two that change files.</summary>
    public static IReadOnlyList<ToolDefinition> Definitions(bool readOnly) =>
        readOnly ? All.Where(t => t.Name is not (EditFile or WriteFile)).ToArray() : All;

    /// <summary>
    /// Checks a call's arguments and prepares it. Never throws for a bad call: the invocation carries the error instead.
    /// <paramref name="shell"/> is the session's shell; without one each command runs in a fresh shell in the project
    /// folder and background commands are unavailable. <paramref name="external"/> holds the session's MCP tools by
    /// qualified name.
    /// </summary>
    public static ToolInvocation Prepare(ToolCall call, CodeWorkspace workspace, bool readOnly = false, CodeShell? shell = null, IReadOnlyDictionary<string, McpTool>? external = null)
    {
        if (call.TryParseArguments() is not { } args)
        {
            return ToolInvocation.Invalid(call, $"{call.Name} (unreadable arguments)",
                $"The arguments for {call.Name} were not a valid JSON object: {Truncate(call.Arguments, 200)}");
        }

        try
        {
            return call.Name switch
            {
                ReadFile => PrepareRead(call, args, workspace),
                ListFiles => PrepareList(call, args, workspace),
                Grep => PrepareGrep(call, args, workspace),
                EditFile when !readOnly => PrepareEdit(call, args, workspace),
                WriteFile when !readOnly => PrepareWrite(call, args, workspace),
                RunCommand => PrepareCommand(call, args, workspace, shell),
                CommandOutput => PrepareOutput(call, args, shell),
                ReadSkill => PrepareSkill(call, args, workspace),
                EditFile or WriteFile => ToolInvocation.Invalid(call, call.Name, "Plan mode is on: files cannot be changed. Describe the change instead."),
                _ when external?.TryGetValue(call.Name, out var tool) == true => PrepareExternal(call, args, tool),
                _ => ToolInvocation.Invalid(call, call.Name, $"There is no tool called {call.Name}. Available tools: {string.Join(", ", Definitions(readOnly).Select(t => t.Name).Concat(external?.Keys ?? []))}."),
            };
        }
        catch (CodeToolException ex)
        {
            return ToolInvocation.Invalid(call, Describe(call, args), ex.Message);
        }
    }

    /// <summary>
    /// Title and icon for a call from a stored transcript, from its arguments alone (no file access: the file may have
    /// changed since). Matches what <see cref="Prepare"/> shows closely enough to read the same.
    /// </summary>
    public static (string Title, string Icon, string? Command) Label(ToolCall call)
    {
        var args = call.TryParseArguments();
        string? Arg(string name) => args is { } a ? GetString(a, name) : null;
        var path = Arg("path") is { Length: > 0 } p ? p : ".";
        return call.Name switch
        {
            ReadFile => ($"Read {path}", "file-earmark-text", null),
            ListFiles => Arg("pattern") is { Length: > 0 } pattern ? ($"Find {pattern}{(path == "." ? "" : $" in {path}")}", "folder2-open", null)
                : ($"List {(path == "." ? "project folder" : path)}", "folder2-open", null),
            Grep => ($"Search for {Truncate(Arg("pattern") ?? "", 60)}{(path == "." ? "" : $" in {path}")}", "search", null),
            EditFile => ($"Edit {path}", "pencil-square", null),
            WriteFile => ($"Write {path}", "file-earmark-diff", null),
            RunCommand => ($"{(Arg("background") is "true" ? "Start" : "Run")} {Truncate((Arg("command") ?? "").ReplaceLineEndings(" "), 80)}", Arg("background") is "true" ? "play-circle" : "terminal", Arg("command")),
            ReadSkill => ($"Use skill {Arg("name")}", "journal-bookmark", null),
            CommandOutput => Arg("stop") is "true" ? ($"Stop {Arg("id")}", "stop-circle", null) : ($"Check {Arg("id")}", "terminal-split", null),
            _ when McpToolNames.TrySplit(call.Name, out var server, out var tool) => ($"{server}: {tool}", "plug", null),
            _ => (call.Name, "exclamation-triangle", null),
        };
    }

    // ---------------------------------------------------------------- read_file

    private static ToolInvocation PrepareRead(ToolCall call, JsonElement args, CodeWorkspace workspace)
    {
        var full = workspace.Resolve(RequireString(args, "path"));
        var relative = workspace.Relative(full);
        var offset = Math.Max(1, GetInt(args, "offset") ?? 1);
        var limit = Math.Clamp(GetInt(args, "limit") ?? DefaultReadLines, 1, 5000);
        return new ToolInvocation(call, $"Read {relative}", "file-earmark-text", ToolPermission.None,
            (_, ct) => Task.Run(() => Read(full, relative, offset, limit, ct), ct))
        { Path = relative };
    }

    private static ToolResult Read(string full, string relative, int offset, int limit, CancellationToken cancellationToken)
    {
        if (Directory.Exists(full))
        {
            return ToolResult.Fail($"{relative} is a folder; use list_files to see what is in it.");
        }
        if (!File.Exists(full))
        {
            return ToolResult.Fail($"{relative} does not exist.");
        }
        if (ImageTypes.TryGetValue(System.IO.Path.GetExtension(full), out var mediaType))
        {
            var size = new FileInfo(full).Length;
            if (size > MaxImageFileBytes)
            {
                return ToolResult.Fail($"{relative} is an image of {size / 1024.0 / 1024.0:0.#} MB, more than the {MaxImageFileBytes / 1024 / 1024} MB that can be shown. Make a smaller copy first (for example with sips or ImageMagick) and read that.");
            }
            return new ToolResult($"{relative}: {mediaType}, {size / 1024.0:0.#} KB. The picture is attached.", false) { Images = [new ChatImage(mediaType, File.ReadAllBytes(full))] };
        }
        if (LooksBinary(full))
        {
            return ToolResult.Fail($"{relative} is a binary file and cannot be shown as text.");
        }

        var sb = new StringBuilder();
        var number = 0;
        var shown = 0;
        var cut = false;
        foreach (var line in File.ReadLines(full))
        {
            cancellationToken.ThrowIfCancellationRequested();
            number++;
            if (number < offset)
            {
                continue;
            }
            if (shown == limit || sb.Length > MaxReadChars)
            {
                cut = true;
                break;
            }
            sb.Append(number.ToString().PadLeft(6)).Append('\t').Append(line.Length > MaxLineChars ? line[..MaxLineChars] + " [line cut]" : line).Append('\n');
            shown++;
        }

        if (number == 0)
        {
            return new ToolResult($"{relative} is empty.", false);
        }
        if (shown == 0)
        {
            return ToolResult.Fail($"{relative} has only {number} lines; offset {offset} is past the end.");
        }
        if (cut)
        {
            sb.Append($"[Showing lines {offset}–{offset + shown - 1}. Read more with offset {offset + shown}.]\n");
        }
        return new ToolResult(sb.ToString(), false);
    }

    // ---------------------------------------------------------------- list_files

    private static ToolInvocation PrepareList(ToolCall call, JsonElement args, CodeWorkspace workspace)
    {
        var full = workspace.Resolve(GetString(args, "path"));
        var relative = workspace.Relative(full);
        var pattern = GetString(args, "pattern");
        var title = string.IsNullOrWhiteSpace(pattern) ? $"List {(relative == "." ? "project folder" : relative)}" : $"Find {pattern}{(relative == "." ? "" : $" in {relative}")}";
        return new ToolInvocation(call, title, "folder2-open", ToolPermission.None,
            (_, ct) => Task.Run(() => string.IsNullOrWhiteSpace(pattern) ? ListFolder(full, relative) : FindFiles(workspace, full, pattern, ct), ct))
        { Path = relative };
    }

    private static ToolResult ListFolder(string full, string relative)
    {
        if (!Directory.Exists(full))
        {
            return ToolResult.Fail(File.Exists(full) ? $"{relative} is a file, not a folder." : $"{relative} does not exist.");
        }

        var folders = Directory.GetDirectories(full).Select(Path.GetFileName).Where(n => n is not null && n != ".git").Order(StringComparer.OrdinalIgnoreCase).Select(n => n + "/");
        var files = Directory.GetFiles(full).Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase);
        var entries = folders.Concat(files).ToList();
        if (entries.Count == 0)
        {
            return new ToolResult($"{relative} is empty.", false);
        }

        var shown = entries.Take(MaxListEntries).ToList();
        var text = string.Join('\n', shown) + "\n";
        if (entries.Count > shown.Count)
        {
            text += $"[{entries.Count - shown.Count} more entries not shown]\n";
        }
        return new ToolResult(text, false);
    }

    private static ToolResult FindFiles(CodeWorkspace workspace, string full, string pattern, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(full))
        {
            return ToolResult.Fail($"{workspace.Relative(full)} is not a folder.");
        }

        var regex = GlobRegex(pattern, out var byName);
        var matches = new List<string>();
        var more = 0;
        foreach (var file in workspace.EnumerateFiles(full, cancellationToken))
        {
            var candidate = byName ? Path.GetFileName(file) : Path.GetRelativePath(full, file).Replace(Path.DirectorySeparatorChar, '/');
            if (!regex.IsMatch(candidate))
            {
                continue;
            }
            if (matches.Count < MaxListEntries)
            {
                matches.Add(workspace.Relative(file));
            }
            else
            {
                more++;
            }
        }

        if (matches.Count == 0)
        {
            return new ToolResult($"No files match {pattern}.", false);
        }
        var text = string.Join('\n', matches) + "\n";
        if (more > 0)
        {
            text += $"[{more} more matches not shown; use a narrower pattern]\n";
        }
        return new ToolResult(text, false);
    }

    // ---------------------------------------------------------------- grep

    private static ToolInvocation PrepareGrep(ToolCall call, JsonElement args, CodeWorkspace workspace)
    {
        var pattern = RequireString(args, "pattern");
        var full = workspace.Resolve(GetString(args, "path"));
        var glob = GetString(args, "glob");
        var options = RegexOptions.CultureInvariant | (GetBool(args, "ignore_case") == true ? RegexOptions.IgnoreCase : RegexOptions.None);
        Regex regex;
        try
        {
            regex = new Regex(pattern, options, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException ex)
        {
            throw new CodeToolException($"The pattern is not a valid regular expression: {ex.Message}");
        }
        var max = Math.Clamp(GetInt(args, "max_results") ?? 100, 1, MaxGrepResults);
        var relative = workspace.Relative(full);
        return new ToolInvocation(call, $"Search for {Truncate(pattern, 60)}{(relative == "." ? "" : $" in {relative}")}", "search", ToolPermission.None,
            (_, ct) => Task.Run(() => Search(workspace, full, regex, glob, max, ct), ct))
        { Path = relative };
    }

    private static ToolResult Search(CodeWorkspace workspace, string full, Regex regex, string? glob, int max, CancellationToken cancellationToken)
    {
        IEnumerable<string> files = File.Exists(full) ? [full]
            : Directory.Exists(full) ? workspace.EnumerateFiles(full, cancellationToken)
            : throw new CodeToolException($"{workspace.Relative(full)} does not exist.");
        Regex? filter = null;
        var byName = false;
        if (!string.IsNullOrWhiteSpace(glob))
        {
            filter = GlobRegex(glob, out byName);
        }

        var sb = new StringBuilder();
        var count = 0;
        foreach (var file in files)
        {
            if (filter is not null && !filter.IsMatch(byName ? Path.GetFileName(file) : Path.GetRelativePath(full, file).Replace(Path.DirectorySeparatorChar, '/')))
            {
                continue;
            }
            if (new FileInfo(file).Length > 5_000_000 || LooksBinary(file))
            {
                continue;
            }

            var number = 0;
            try
            {
                foreach (var line in File.ReadLines(file))
                {
                    number++;
                    bool matched;
                    try
                    {
                        matched = regex.IsMatch(line);
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        matched = false;
                    }
                    if (!matched)
                    {
                        continue;
                    }

                    sb.Append(workspace.Relative(file)).Append(':').Append(number).Append(": ").Append(Truncate(line.Trim(), 300)).Append('\n');
                    if (++count == max)
                    {
                        sb.Append($"[Stopped after {max} matching lines; narrow the search or raise max_results.]\n");
                        return new ToolResult(sb.ToString(), false);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            cancellationToken.ThrowIfCancellationRequested();
        }

        return count == 0 ? new ToolResult("No matches.", false) : new ToolResult(sb.ToString(), false);
    }

    // ---------------------------------------------------------------- edit_file / write_file

    private static ToolInvocation PrepareEdit(ToolCall call, JsonElement args, CodeWorkspace workspace)
    {
        var full = workspace.Resolve(RequireString(args, "path"));
        var relative = workspace.Relative(full);
        var oldString = RequireString(args, "old_string", allowEmpty: true);
        var newString = RequireString(args, "new_string", allowEmpty: true);
        var replaceAll = GetBool(args, "replace_all") == true;

        if (!File.Exists(full))
        {
            throw new CodeToolException($"{relative} does not exist. Use write_file to create a file.");
        }
        if (oldString.Length == 0)
        {
            throw new CodeToolException("old_string is empty. Give the exact text to replace, or use write_file to replace the whole file.");
        }
        if (oldString == newString)
        {
            throw new CodeToolException("old_string and new_string are the same; there is nothing to change.");
        }

        var original = File.ReadAllText(full);
        // A file with Windows line breaks: the model almost always writes \n, so match and write in the file's style.
        if (original.Contains("\r\n", StringComparison.Ordinal) && !oldString.Contains('\r'))
        {
            oldString = oldString.Replace("\n", "\r\n");
            newString = newString.Replace("\r\n", "\n").Replace("\n", "\r\n");
        }

        // Exact first; then help with pasted line numbers and indentation slips; otherwise an error showing the closest real lines.
        var plan = EditMatcher.Plan(original, oldString, newString, replaceAll, relative);
        var updated = plan.Updated;
        var occurrences = plan.Occurrences;
        var diff = LineDiff.Compute(original, updated);
        return new ToolInvocation(call, $"Edit {relative}", "pencil-square", ToolPermission.Edit,
            async (_, ct) =>
            {
                // The file may have changed while the user was deciding; refuse rather than overwrite that.
                if (await File.ReadAllTextAsync(full, ct) != original)
                {
                    return ToolResult.Fail($"{relative} changed since the edit was prepared. Read it again and redo the edit.");
                }
                await File.WriteAllTextAsync(full, updated, ct);
                var summary = replaceAll && occurrences > 1 ? $"Replaced {occurrences} occurrences in {relative}." : $"Edited {relative}.";
                var note = plan.Note is null ? "" : $" Note: {plan.Note}";
                return new ToolResult($"{summary} (+{diff.Added} −{diff.Removed} lines){note}", false, diff.ToUnified());
            })
        { Path = relative, Diff = diff };
    }

    private static ToolInvocation PrepareWrite(ToolCall call, JsonElement args, CodeWorkspace workspace)
    {
        var full = workspace.Resolve(RequireString(args, "path"));
        var relative = workspace.Relative(full);
        var content = RequireString(args, "content", allowEmpty: true);
        if (Directory.Exists(full))
        {
            throw new CodeToolException($"{relative} is a folder.");
        }

        var exists = File.Exists(full);
        var original = exists ? File.ReadAllText(full) : "";
        var diff = LineDiff.Compute(original, content);
        return new ToolInvocation(call, exists ? $"Write {relative}" : $"Create {relative}", exists ? "file-earmark-diff" : "file-earmark-plus", ToolPermission.Edit,
            async (_, ct) =>
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
                await File.WriteAllTextAsync(full, content, ct);
                return new ToolResult(exists ? $"Wrote {relative} (+{diff.Added} −{diff.Removed} lines)." : $"Created {relative} ({LineDiff.SplitLines(content).Length} lines).", false, diff.ToUnified());
            })
        { Path = relative, Diff = diff };
    }

    // ---------------------------------------------------------------- MCP tools

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static ToolInvocation PrepareExternal(ToolCall call, JsonElement args, McpTool tool) =>
        new(call, $"{tool.ServerName}: {tool.ToolName}", "plug", ToolPermission.External, (_, ct) => tool.CallAsync(args, ct))
        {
            ExternalTool = tool.QualifiedName,
            Arguments = JsonSerializer.Serialize(args, Indented),
        };

    // ---------------------------------------------------------------- read_skill

    private static ToolInvocation PrepareSkill(ToolCall call, JsonElement args, CodeWorkspace workspace)
    {
        var name = RequireString(args, "name").Trim();
        return new ToolInvocation(call, $"Use skill {name}", "journal-bookmark", ToolPermission.None,
            (_, _) => Task.FromResult(CodeCommands.FindSkill(workspace, name) is { } skill
                ? new ToolResult($"# Skill: {skill.Name}\n\n{skill.Text}", false)
                : ToolResult.Fail($"There is no skill called {name}. Available: {string.Join(", ", CodeCommands.DiscoverSkills(workspace).Select(s => s.Name))}.")));
    }

    // ---------------------------------------------------------------- run_command

    private static ToolInvocation PrepareCommand(ToolCall call, JsonElement args, CodeWorkspace workspace, CodeShell? shell)
    {
        var command = RequireString(args, "command").Trim();
        var seconds = Math.Clamp(GetInt(args, "timeout_seconds") ?? DefaultCommandSeconds, 1, MaxCommandSeconds);
        var title = Truncate(command.ReplaceLineEndings(" "), 80);

        if (GetBool(args, "background") == true)
        {
            if (shell is null)
            {
                throw new CodeToolException("Background commands are not available here; run the command without background.");
            }
            return new ToolInvocation(call, $"Start {title}", "play-circle", ToolPermission.Command,
                async (_, ct) =>
                {
                    var background = shell.Start(command);
                    await background.WaitForExitAsync(BackgroundFirstOutput, ct);
                    var first = await background.ReadNewAsync(TimeSpan.Zero, ct);
                    var shown = first.Length == 0 ? "(no output yet)\n" : ShellCommand.Clip(first, ShellCommand.MaxOutputChars).Text;
                    return background.IsRunning
                        ? new ToolResult($"Started {background.Id} in {(background.Directory == "." ? "the project folder" : background.Directory)}. It is running; read more with command_output id {background.Id}, and stop it when done.\nFirst output:\n{shown}", false)
                        : new ToolResult($"{background.Id} {background.Status} straight away.\n{shown}", background.ExitCode != 0);
                })
            { Command = command };
        }

        return new ToolInvocation(call, $"Run {title}", "terminal", ToolPermission.Command,
            async (onOutput, ct) =>
            {
                ShellResult result;
                string? movedTo = null;
                if (shell is null)
                {
                    result = await ShellCommand.RunAsync(command, workspace.Root, TimeSpan.FromSeconds(seconds), onOutput, ct);
                }
                else
                {
                    var before = shell.WorkingDirectory;
                    result = await shell.RunAsync(command, TimeSpan.FromSeconds(seconds), onOutput, ct);
                    movedTo = shell.WorkingDirectory != before ? shell.DisplayDirectory : null;
                }
                var text = result.Output.Length == 0 ? "(no output)\n" : result.Output;
                if (result.TimedOut)
                {
                    return new ToolResult($"{text}\n[Stopped after {seconds} seconds without finishing. For servers and watchers use background: true.]", true);
                }
                if (result.Cancelled)
                {
                    return new ToolResult($"{text}\n[Stopped by the user.]", true);
                }
                var where = movedTo is null ? "" : $"\n[working directory is now {movedTo}]";
                return new ToolResult($"{text}\n[exit code {result.ExitCode}]{where}", result.ExitCode != 0);
            })
        { Command = command };
    }

    // ---------------------------------------------------------------- command_output

    private static ToolInvocation PrepareOutput(ToolCall call, JsonElement args, CodeShell? shell)
    {
        var id = RequireString(args, "id").Trim();
        var stop = GetBool(args, "stop") == true;
        var wait = TimeSpan.FromSeconds(Math.Clamp(GetInt(args, "wait_seconds") ?? 0, 0, MaxOutputWaitSeconds));
        if (shell?.Find(id) is not { } background)
        {
            var known = shell?.Background.Select(b => $"{b.Id} ({b.Status})").ToArray() ?? [];
            throw new CodeToolException(known.Length == 0
                ? $"There is no background command {id}; none have been started in this session."
                : $"There is no background command {id}. Known: {string.Join(", ", known)}.");
        }

        return new ToolInvocation(call, stop ? $"Stop {background.Id}" : $"Check {background.Id}", stop ? "stop-circle" : "terminal-split", ToolPermission.None,
            async (_, ct) =>
            {
                if (stop)
                {
                    background.Stop();
                }
                var output = await background.ReadNewAsync(stop ? TimeSpan.Zero : wait, ct);
                var shown = output.Length == 0 ? "(nothing new)\n" : ShellCommand.Clip(output, ShellCommand.MaxOutputChars).Text;
                return new ToolResult($"{shown}\n[{background.Id}: {background.Command} — {background.Status}]", false);
            });
    }

    /// <summary>The first word or two of a command ("git status", "dotnet build"): what "always allow" remembers.</summary>
    public static string CommandPrefix(string command)
    {
        var words = command.Trim().Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
        return words.Length switch
        {
            0 => "",
            1 => words[0],
            _ when words[1].StartsWith('-') || words[1].Contains('/') || words[1].Contains('.') => words[0],
            _ => $"{words[0]} {words[1]}",
        };
    }

    /// <summary>
    /// True when <paramref name="command"/> starts with an allowed prefix and chains nothing else after it (no ;, &amp;&amp;,
    /// ||, |, backticks, $( or redirection), so "git status; rm -rf ~" does not ride on an allowed "git status".
    /// </summary>
    public static bool IsCoveredBy(string command, IEnumerable<string> allowedPrefixes)
    {
        var trimmed = command.Trim();
        if (trimmed.IndexOfAny([';', '&', '|', '`', '>', '<', '\n']) >= 0 || trimmed.Contains("$(", StringComparison.Ordinal))
        {
            return false;
        }
        return allowedPrefixes.Any(p => p.Length > 0 && (trimmed == p || trimmed.StartsWith(p + " ", StringComparison.Ordinal)));
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// A glob as a regular expression: ** spans folders, * and ? stay within one name, {a,b} is either. A pattern
    /// without / is matched against file names only (<paramref name="byName"/>), so *.cs finds C# files at any depth.
    /// </summary>
    public static Regex GlobRegex(string pattern, out bool byName)
    {
        var glob = pattern.Trim().Replace('\\', '/');
        if (glob.StartsWith("./", StringComparison.Ordinal))
        {
            glob = glob[2..];
        }
        byName = !glob.Contains('/');

        var sb = new StringBuilder("^");
        var braces = 0;
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            switch (c)
            {
                case '*' when i + 1 < glob.Length && glob[i + 1] == '*':
                    if (i + 2 < glob.Length && glob[i + 2] == '/')
                    {
                        sb.Append("(?:.*/)?");
                        i += 2;
                    }
                    else
                    {
                        sb.Append(".*");
                        i += 1;
                    }
                    break;
                case '*':
                    sb.Append("[^/]*");
                    break;
                case '?':
                    sb.Append("[^/]");
                    break;
                case '{':
                    braces++;
                    sb.Append("(?:");
                    break;
                case '}' when braces > 0:
                    braces--;
                    sb.Append(')');
                    break;
                case ',' when braces > 0:
                    sb.Append('|');
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }
        sb.Append(')', braces).Append('$');
        var options = RegexOptions.CultureInvariant | (OperatingSystem.IsLinux() ? RegexOptions.None : RegexOptions.IgnoreCase);
        return new Regex(sb.ToString(), options);
    }

    private static string Describe(ToolCall call, JsonElement args) =>
        GetString(args, "path") is { Length: > 0 } path ? $"{call.Name} {path}" : call.Name;

    private static bool LooksBinary(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[8192];
            var read = stream.Read(buffer, 0, buffer.Length);
            return Array.IndexOf(buffer, (byte)0, 0, read) >= 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private static string RequireString(JsonElement args, string name, bool allowEmpty = false)
    {
        var value = GetString(args, name);
        if (value is null || (!allowEmpty && value.Trim().Length == 0))
        {
            throw new CodeToolException($"The {name} argument is required.");
        }
        return value;
    }

    /// <summary>Local models are loose with types: a number or boolean written as a string is accepted.</summary>
    private static string? GetString(JsonElement args, string name) =>
        args.TryGetProperty(name, out var value) ? value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => value.GetRawText(),
        } : null;

    private static int? GetInt(JsonElement args, string name) =>
        args.TryGetProperty(name, out var value) ? value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var n) => n,
            JsonValueKind.Number => (int)Math.Clamp(value.GetDouble(), int.MinValue, int.MaxValue),
            JsonValueKind.String when int.TryParse(value.GetString(), out var n) => n,
            _ => null,
        } : null;

    private static bool? GetBool(JsonElement args, string name) =>
        args.TryGetProperty(name, out var value) ? value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var b) => b,
            _ => null,
        } : null;
}
