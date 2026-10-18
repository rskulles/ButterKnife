using ButterKnife.Services;

namespace ButterKnife.Tests;

public sealed class CodeCommandsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "butterknife-tests", Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string _home;
    private readonly CodeWorkspace _workspace;

    public CodeCommandsTests()
    {
        _root = Path.Combine(_dir, "project");
        _home = Path.Combine(_dir, "home");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_home);
        _workspace = new CodeWorkspace(_root);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void Write(string root, string relative, string text)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void BuiltInsComeFirstAndCannotBeReplaced()
    {
        Write(_root, ".claude/commands/clear.md", "Delete everything.");

        var commands = CodeCommands.Discover(_workspace, _home);

        Assert.Equal(["init", "clear", "compact", "help"], commands.Select(c => c.Name));
        Assert.All(commands, c => Assert.Equal(CodeCommandSource.BuiltIn, c.Source));
    }

    [Fact]
    public void ProjectAndPersonalCommandsAreFoundInBothFolderStyles()
    {
        Write(_root, ".butterknife/commands/review.md", "---\ndescription: \"Review the diff\"\n---\nReview the current git diff for bugs.\n");
        Write(_root, ".claude/commands/git/commit.md", "# Commit\nWrite a commit message for $ARGUMENTS.\n");
        Write(_home, ".claude/commands/review.md", "Personal review, shadowed by the project's.");
        Write(_home, ".claude/commands/standup.md", "Summarise yesterday's commits.");

        var commands = CodeCommands.Discover(_workspace, _home).Where(c => c.Source != CodeCommandSource.BuiltIn).ToList();

        Assert.Equal(["review", "git:commit", "standup"], commands.Select(c => c.Name));
        Assert.Equal("Review the diff", commands[0].Description);
        Assert.Equal("Review the current git diff for bugs.", commands[0].Template);
        Assert.Equal("Commit", commands[1].Description); // first line of the body, heading marks dropped
        Assert.Equal(CodeCommandSource.Project, commands[0].Source);
        Assert.Equal(CodeCommandSource.Personal, commands[2].Source);
    }

    [Fact]
    public void SkillsAreListedByNameAndDescriptionWithTheirPath()
    {
        Write(_root, ".claude/skills/release/SKILL.md", "---\nname: release-notes\ndescription: Write release notes from merged PRs\n---\n# Release notes\nSteps...\n");
        Write(_root, ".butterknife/skills/migrations/SKILL.md", "How to add a database migration safely.\n");
        Directory.CreateDirectory(Path.Combine(_root, ".claude", "skills", "empty")); // no SKILL.md: ignored

        var skills = CodeCommands.DiscoverSkills(_workspace);

        Assert.Equal(["migrations", "release-notes"], skills.Select(s => s.Name));
        Assert.Equal("How to add a database migration safely.", skills[0].Description);
        Assert.Equal(".claude/skills/release/SKILL.md", skills[1].Path);

        var section = CodeCommands.SkillsSection(skills)!;
        Assert.Contains("- release-notes: Write release notes from merged PRs (.claude/skills/release/SKILL.md)", section);
        Assert.Null(CodeCommands.SkillsSection([]));
    }

    [Theory]
    [InlineData("/review", "review", "")]
    [InlineData("/git:commit fix the login bug", "git:commit", "fix the login bug")]
    [InlineData("  /init  ", "init", "")]
    [InlineData("/fix line one\nline two", "fix", "line one\nline two")]
    public void ParsesACommandLine(string input, string name, string arguments)
    {
        Assert.True(CodeCommands.TryParse(input, out var parsedName, out var parsedArguments));
        Assert.Equal((name, arguments), (parsedName, parsedArguments));
    }

    [Theory]
    [InlineData("fix /etc/hosts")]
    [InlineData("/")]
    [InlineData("/ spaced")]
    public void PlainTextIsNotACommand(string input) => Assert.False(CodeCommands.TryParse(input, out _, out _));

    [Fact]
    public void ExpandsArgumentsOrAppendsThem()
    {
        Assert.Equal("Fix issue 42 now.", CodeCommands.Expand("Fix issue $ARGUMENTS now.", "42"));
        Assert.Equal("Review the diff.\n\nfocus on tests", CodeCommands.Expand("Review the diff.", "focus on tests"));
        Assert.Equal("Review the diff.", CodeCommands.Expand("Review the diff.", ""));
    }

    [Fact]
    public void FrontMatterWithoutAClosingFenceIsLeftAsBody()
    {
        var (fields, body) = CodeCommands.ParseFrontMatter("---\ndescription: oops\nno end");

        Assert.Empty(fields);
        Assert.StartsWith("---", body);
    }

    [Fact]
    public void MatchingFiltersByPrefix()
    {
        var matches = CodeCommands.Matching(CodeCommands.BuiltIns, "c");

        Assert.Equal(["clear", "compact"], matches.Select(c => c.Name));
    }
}
