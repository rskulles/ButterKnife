using System.Text;
using System.Text.RegularExpressions;

namespace ButterKnife.Services;

public enum CodeCommandSource
{
    /// <summary>Handled by the page itself (/init, /clear, /compact, /help).</summary>
    BuiltIn,

    /// <summary>A Markdown file in the project folder.</summary>
    Project,

    /// <summary>A Markdown file in the user's home folder, available in every project.</summary>
    Personal,
}

/// <summary>A slash command: its name (without the slash), a one-line description, and for file commands the prompt template.</summary>
public sealed record CodeCommand(string Name, string Description, CodeCommandSource Source, string? Template = null, string? FilePath = null);

/// <summary>
/// A skill: the model sees name and description, and calls read_skill for <see cref="Text"/> when a task matches.
/// <see cref="Location"/> says where it came from, for /help (a path, or "built-in").
/// </summary>
public sealed record CodeSkill(string Name, string Description, CodeCommandSource Source, string Location, string Text);

/// <summary>
/// Slash commands and skills for the Code tab, in the shapes Claude Code uses so existing ones work unchanged:
/// commands are Markdown files under <c>.butterknife/commands</c> or <c>.claude/commands</c> (project, then the same
/// folders in the home directory), named after the file (a sub-folder becomes a "folder:name" prefix), whose text is
/// the prompt with <c>$ARGUMENTS</c> replaced by whatever follows the command. Skills are folders with a
/// <c>SKILL.md</c> under <c>.butterknife/skills</c> or <c>.claude/skills</c> in the project or the home directory, plus
/// the built-in ones shipped in the assembly (<c>Skills/</c>); only their names and descriptions go in the system
/// prompt, and the model calls read_skill when a task calls for one, which keeps a small model's context small.
/// Optional YAML front matter supplies name and description.
/// </summary>
public static partial class CodeCommands
{
    public const string Init = "init";
    public const string Clear = "clear";
    public const string Compact = "compact";
    public const string Help = "help";

    public const int MaxCommands = 100;
    public const int MaxSkills = 30;
    public const int MaxDescriptionChars = 120;
    public const int MaxTemplateChars = 20_000;

    public static readonly string[] CommandFolders = [".butterknife/commands", ".claude/commands"];
    public static readonly string[] SkillFolders = [".butterknife/skills", ".claude/skills"];

    /// <summary>What /init asks the model to do: an ordinary turn, so writing the file goes through the usual edit permission.</summary>
    public const string InitPrompt = """
        Explore this project and write an AGENTS.md file at the root of the project folder for coding agents working here
        later. If AGENTS.md (or CLAUDE.md) already exists, read it and improve it instead of starting over.

        Find out, by listing files and reading the build files, README and a few representative sources:
        - how to build, run, test (including a single test) and lint or format the project, as exact commands;
        - how the code is laid out: the main folders and what lives in each, and the entry points;
        - conventions a newcomer would get wrong: language and framework versions, naming, error handling, test style,
          anything the configuration enforces.

        Keep it short and specific to this project: commands and facts, no generic advice, no list of every file.
        """;

    public static readonly IReadOnlyList<CodeCommand> BuiltIns =
    [
        new(Init, "Explore the project and write an AGENTS.md for future sessions", CodeCommandSource.BuiltIn),
        new(Clear, "Start a new session in the same folder", CodeCommandSource.BuiltIn),
        new(Compact, "Summarise the conversation so far to free up the context window", CodeCommandSource.BuiltIn),
        new(Help, "List the commands and skills available here", CodeCommandSource.BuiltIn),
    ];

    /// <summary>
    /// Built-ins, then the project's command files, then personal ones from the home folder. A name is taken by the first
    /// to claim it, so a project cannot replace /clear and a project command wins over a personal one.
    /// </summary>
    public static IReadOnlyList<CodeCommand> Discover(CodeWorkspace workspace, string? home = null)
    {
        var commands = new List<CodeCommand>(BuiltIns);
        var taken = new HashSet<string>(BuiltIns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);

        void AddFrom(string root, CodeCommandSource source)
        {
            foreach (var folder in CommandFolders)
            {
                var directory = System.IO.Path.Combine(root, folder);
                if (!Directory.Exists(directory))
                {
                    continue;
                }
                foreach (var file in SafeFiles(directory, "*.md"))
                {
                    if (commands.Count >= BuiltIns.Count + MaxCommands)
                    {
                        return;
                    }
                    var relative = System.IO.Path.GetRelativePath(directory, file).Replace(System.IO.Path.DirectorySeparatorChar, '/');
                    var name = relative[..^".md".Length].Replace('/', ':');
                    if (!ValidName().IsMatch(name) || !taken.Add(name) || ReadText(file) is not { } text)
                    {
                        continue;
                    }
                    var (fields, body) = ParseFrontMatter(text);
                    commands.Add(new CodeCommand(name, Describe(fields, body), source, Clip(body.Trim(), MaxTemplateChars), file));
                }
            }
        }

        AddFrom(workspace.Root, CodeCommandSource.Project);
        if ((home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) is { Length: > 0 } homeFolder && Directory.Exists(homeFolder))
        {
            AddFrom(homeFolder, CodeCommandSource.Personal);
        }
        return commands;
    }

    /// <summary>
    /// Every skill available in this folder: the project's, then personal ones from the home directory, then the
    /// built-ins. A name is taken by the first to claim it, so a project can replace a built-in by giving its own skill
    /// the same name. Capped at <see cref="MaxSkills"/>; folders without a SKILL.md are ignored.
    /// </summary>
    public static IReadOnlyList<CodeSkill> DiscoverSkills(CodeWorkspace workspace, string? home = null)
    {
        var skills = new List<CodeSkill>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(CodeSkill skill)
        {
            if (skills.Count < MaxSkills && taken.Add(skill.Name))
            {
                skills.Add(skill);
            }
        }

        foreach (var skill in SkillsIn(workspace.Root, CodeCommandSource.Project, workspace.Relative))
        {
            Add(skill);
        }
        if ((home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) is { Length: > 0 } homeFolder && Directory.Exists(homeFolder))
        {
            foreach (var skill in SkillsIn(homeFolder, CodeCommandSource.Personal, f => "~/" + System.IO.Path.GetRelativePath(homeFolder, f).Replace(System.IO.Path.DirectorySeparatorChar, '/')))
            {
                Add(skill);
            }
        }
        foreach (var skill in BuiltInSkills)
        {
            Add(skill);
        }
        return skills;
    }

    /// <summary>A skill by name (case-insensitive), as <see cref="DiscoverSkills"/> resolves it; null when there is none.</summary>
    public static CodeSkill? FindSkill(CodeWorkspace workspace, string name, string? home = null) =>
        DiscoverSkills(workspace, home).FirstOrDefault(s => string.Equals(s.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<CodeSkill> SkillsIn(string root, CodeCommandSource source, Func<string, string> location)
    {
        foreach (var folder in SkillFolders)
        {
            var directory = System.IO.Path.Combine(root, folder);
            if (!Directory.Exists(directory))
            {
                continue;
            }
            string[] skillFolders;
            try
            {
                skillFolders = Directory.GetDirectories(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var skillFolder in skillFolders.Order(StringComparer.OrdinalIgnoreCase))
            {
                var file = System.IO.Path.Combine(skillFolder, "SKILL.md");
                if (File.Exists(file) && ReadText(file) is { } text)
                {
                    yield return Parse(text, System.IO.Path.GetFileName(skillFolder), source, location(file));
                }
            }
        }
    }

    private static CodeSkill Parse(string text, string folderName, CodeCommandSource source, string location)
    {
        var (fields, body) = ParseFrontMatter(text);
        var name = fields.TryGetValue("name", out var n) && n.Length > 0 ? n : folderName;
        return new CodeSkill(name, Describe(fields, body), source, location, Clip(body.Trim(), MaxTemplateChars));
    }

    /// <summary>The skills shipped with ButterKnife (embedded <c>Skills/&lt;name&gt;/SKILL.md</c>, see the csproj), read once.</summary>
    public static IReadOnlyList<CodeSkill> BuiltInSkills => LazyBuiltIns.Value;

    private const string ResourcePrefix = "ButterKnife.Skills/";

    private static readonly Lazy<IReadOnlyList<CodeSkill>> LazyBuiltIns = new(() =>
    {
        var assembly = typeof(CodeCommands).Assembly;
        var skills = new List<CodeSkill>();
        foreach (var resource in assembly.GetManifestResourceNames().Where(r => r.StartsWith(ResourcePrefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream is null)
            {
                continue;
            }
            using var reader = new StreamReader(stream);
            var folderName = resource[ResourcePrefix.Length..].Split('/', '\\')[0];
            skills.Add(Parse(reader.ReadToEnd(), folderName, CodeCommandSource.BuiltIn, "built-in"));
        }
        return skills;
    });

    /// <summary>The system prompt section listing the skills, or null when there are none.</summary>
    public static string? SkillsSection(IReadOnlyList<CodeSkill> skills)
    {
        if (skills.Count == 0)
        {
            return null;
        }
        var sb = new StringBuilder("Skills: step-by-step guides for common tasks. When the user's request matches one, call read_skill with its name first and follow it (the project's own instructions still come first).\n");
        foreach (var skill in skills)
        {
            sb.Append("- ").Append(skill.Name).Append(": ").Append(skill.Description).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>"/name rest of the line" → name and arguments. False for text that does not start with a slash and a name.</summary>
    public static bool TryParse(string input, out string name, out string arguments)
    {
        var match = CommandLine().Match(input.TrimStart());
        name = match.Success ? match.Groups["name"].Value : "";
        arguments = match.Success ? match.Groups["args"].Value.Trim() : "";
        return match.Success;
    }

    /// <summary>The command's prompt for these arguments: $ARGUMENTS replaced, or the arguments appended when the template has no placeholder.</summary>
    public static string Expand(string template, string arguments)
    {
        if (template.Contains("$ARGUMENTS", StringComparison.Ordinal))
        {
            return template.Replace("$ARGUMENTS", arguments, StringComparison.Ordinal);
        }
        return arguments.Length == 0 ? template : $"{template}\n\n{arguments}";
    }

    /// <summary>Commands whose name starts with what was typed after the slash, built-ins first, for the autocomplete list.</summary>
    public static IReadOnlyList<CodeCommand> Matching(IReadOnlyList<CodeCommand> commands, string typed) =>
        commands.Where(c => c.Name.StartsWith(typed, StringComparison.OrdinalIgnoreCase)).Take(12).ToArray();

    /// <summary>
    /// Splits off YAML-style front matter ("---" lines around "key: value" pairs). Only flat string values are read;
    /// anything else is ignored, which is all the name and description fields need.
    /// </summary>
    public static (IReadOnlyDictionary<string, string> Fields, string Body) ParseFrontMatter(string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var normalised = text.Replace("\r\n", "\n");
        if (!normalised.StartsWith("---\n", StringComparison.Ordinal))
        {
            return (fields, normalised);
        }
        var end = normalised.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0)
        {
            return (fields, normalised);
        }
        foreach (var line in normalised[4..end].Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0 || char.IsWhiteSpace(line[0]))
            {
                continue;
            }
            var value = line[(colon + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
            {
                value = value[1..^1];
            }
            fields[line[..colon].Trim()] = value;
        }
        var bodyStart = normalised.IndexOf('\n', end + 4);
        return (fields, bodyStart < 0 ? "" : normalised[(bodyStart + 1)..]);
    }

    /// <summary>The front matter's description, else the body's first line of prose (heading marks dropped), clipped.</summary>
    private static string Describe(IReadOnlyDictionary<string, string> fields, string body)
    {
        if (fields.TryGetValue("description", out var description) && description.Length > 0)
        {
            return Clip(description, MaxDescriptionChars);
        }
        var first = body.Split('\n').Select(l => l.Trim().TrimStart('#').Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        return Clip(first, MaxDescriptionChars);
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max].TrimEnd() + "…";

    private static IEnumerable<string> SafeFiles(string directory, string pattern)
    {
        try
        {
            return Directory.GetFiles(directory, pattern, SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string? ReadText(string path)
    {
        try
        {
            return new FileInfo(path).Length > 200_000 ? null : File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.:-]*$")]
    private static partial Regex ValidName();

    [GeneratedRegex(@"^/(?<name>[A-Za-z0-9][A-Za-z0-9_.:-]*)(?:\s+(?<args>[\s\S]*))?$")]
    private static partial Regex CommandLine();
}
