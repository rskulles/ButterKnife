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
    public void ProjectSkillsComeFirstThenPersonalThenBuiltIn()
    {
        Write(_root, ".claude/skills/release/SKILL.md", "---\nname: release-notes\ndescription: Write release notes from merged PRs\n---\n# Release notes\nSteps...\n");
        Write(_root, ".butterknife/skills/migrations/SKILL.md", "How to add a database migration safely.\n");
        Write(_home, ".claude/skills/standup/SKILL.md", "---\ndescription: Summarise yesterday's work\n---\nRun git log.\n");
        Directory.CreateDirectory(Path.Combine(_root, ".claude", "skills", "empty")); // no SKILL.md: ignored

        var skills = CodeCommands.DiscoverSkills(_workspace, _home);

        Assert.Equal(["migrations", "release-notes", "standup"], skills.Take(3).Select(s => s.Name));
        Assert.Equal("How to add a database migration safely.", skills[0].Description);
        Assert.Equal((CodeCommandSource.Project, ".claude/skills/release/SKILL.md"), (skills[1].Source, skills[1].Location));
        Assert.Equal((CodeCommandSource.Personal, "~/.claude/skills/standup/SKILL.md"), (skills[2].Source, skills[2].Location));
        Assert.Equal("# Release notes\nSteps...", skills[1].Text);
        Assert.All(skills.Skip(3), s => Assert.Equal(CodeCommandSource.BuiltIn, s.Source));
    }

    [Fact]
    public void BuiltInSkillsShipWithTheApp()
    {
        var builtIns = CodeCommands.BuiltInSkills;

        Assert.Equal(
            ["debug-issue", "explore-project", "fix-failing-tests", "git-commit", "implement-feature", "refactor", "review-changes", "security-review", "write-docs", "write-tests"],
            builtIns.Select(s => s.Name).Order());
        Assert.All(builtIns, s =>
        {
            Assert.InRange(s.Description.Length, 20, CodeCommands.MaxDescriptionChars);
            Assert.DoesNotContain("---", s.Text[..3]); // front matter stripped
            Assert.Contains("#", s.Text);
        });
    }

    [Fact]
    public void AProjectSkillReplacesABuiltInOfTheSameName()
    {
        Write(_root, ".claude/skills/commits/SKILL.md", "---\nname: git-commit\ndescription: Our commit rules\n---\nUse Conventional Commits.\n");

        var skill = CodeCommands.FindSkill(_workspace, "GIT-COMMIT", _home);

        Assert.Equal((CodeCommandSource.Project, "Use Conventional Commits."), (skill!.Source, skill.Text));
        Assert.Single(CodeCommands.DiscoverSkills(_workspace, _home), s => s.Name == "git-commit");
    }

    [Fact]
    public void TheSkillsSectionListsNamesAndDescriptionsOnly()
    {
        var section = CodeCommands.SkillsSection([new CodeSkill("release-notes", "Write release notes", CodeCommandSource.Project, ".claude/skills/r/SKILL.md", "long text")])!;

        Assert.Contains("read_skill", section);
        Assert.Contains("- release-notes: Write release notes\n", section);
        Assert.DoesNotContain("long text", section);
        Assert.Null(CodeCommands.SkillsSection([]));
    }

    [Fact]
    public async Task ReadSkillReturnsTheSkillOrSaysWhatExists()
    {
        var found = await CodeTools.Prepare(new ToolCall("c", CodeTools.ReadSkill, """{"name":"debug-issue"}"""), _workspace).RunAsync(null, CancellationToken.None);
        Assert.False(found.IsError);
        Assert.StartsWith("# Skill: debug-issue", found.Output);

        var missing = await CodeTools.Prepare(new ToolCall("c", CodeTools.ReadSkill, """{"name":"nope"}"""), _workspace).RunAsync(null, CancellationToken.None);
        Assert.True(missing.IsError);
        Assert.Contains("explore-project", missing.Output);
    }

    [Fact]
    public void TheSystemPromptListsTheBuiltInSkills()
    {
        var prompt = CodePrompt.Compose(_workspace, readOnly: false);

        Assert.Contains("call read_skill", prompt);
        Assert.All(CodeCommands.BuiltInSkills, s => Assert.Contains($"- {s.Name}: ", prompt));
        Assert.DoesNotContain(CodeCommands.BuiltInSkills[0].Text, prompt); // only names and descriptions
    }

    [Fact]
    public void ReadSkillIsOfferedInPlanModeToo() =>
        Assert.Contains(CodeTools.Definitions(readOnly: true), t => t.Name == CodeTools.ReadSkill);

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
