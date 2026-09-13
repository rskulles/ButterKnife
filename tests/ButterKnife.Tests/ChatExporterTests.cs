using ButterKnife.Data;
using ButterKnife.Services;

namespace ButterKnife.Tests;

public sealed class ChatExporterTests
{
    [Fact]
    public void RendersTitleMetadataSpeakersReasoningAndImageNotes()
    {
        var at = new DateTimeOffset(2026, 9, 13, 10, 30, 0, TimeSpan.Zero);
        var conversation = new Conversation(Guid.NewGuid(), "Word Frequencies", Guid.NewGuid(), "qwen3", null, null, null, null, null, at, at,
        [
            new ChatMessage(ChatRole.User, "Count words?", [new ChatImage("image/png", [1, 2, 3])]) { Id = 1, CreatedAt = at },
            new ChatMessage(ChatRole.Assistant, "Use `Counter`.\n\n```python\nfrom collections import Counter\n```\n") { Id = 2, CreatedAt = at.AddMinutes(1), Reasoning = "They want a quick answer." },
        ]);

        var markdown = ChatExporter.ToMarkdown(conversation, "Skully", "Programmer", at.AddDays(1));

        Assert.StartsWith("# Word Frequencies\n", markdown);
        Assert.Contains("Model: qwen3. Persona: Programmer.", markdown);
        Assert.Contains("**Skully** · ", markdown);
        Assert.Contains("*1 image attached*", markdown);
        Assert.Contains("**Assistant** · ", markdown);
        Assert.Contains("<summary>Reasoning</summary>\n\nThey want a quick answer.\n", markdown);
        Assert.Contains("```python\nfrom collections import Counter\n```", markdown);
        Assert.EndsWith("```\n", markdown);
        Assert.Equal(2, markdown.Split("\n---\n").Length - 1);
    }

    [Fact]
    public void OmitsPersonaAndReasoningWhenAbsent()
    {
        var conversation = new Conversation(Guid.NewGuid(), "  ", Guid.NewGuid(), "m", null, null, null, null, null, DateTimeOffset.Now, DateTimeOffset.Now,
            [new ChatMessage(ChatRole.Assistant, "Hi")]);

        var markdown = ChatExporter.ToMarkdown(conversation, "User", null, DateTimeOffset.Now);

        Assert.StartsWith("# Chat\n", markdown);
        Assert.DoesNotContain("Persona", markdown);
        Assert.DoesNotContain("<details>", markdown);
        Assert.Contains("**Assistant**\n\nHi\n", markdown);
    }

    [Theory]
    [InlineData("Word Frequencies", "Word Frequencies.md")]
    [InlineData("What is 2/3 of 9? \"Quick\"", "What is 2-3 of 9- -Quick.md")]
    [InlineData("   ", "chat.md")]
    [InlineData("...", "chat.md")]
    public void FileNameIsSafe(string title, string expected) => Assert.Equal(expected, ChatExporter.FileName(title));

    [Fact]
    public void FileNameIsTrimmed()
    {
        var name = ChatExporter.FileName(new string('x', 200));
        Assert.Equal(83, name.Length);
        Assert.EndsWith(".md", name);
    }
}
