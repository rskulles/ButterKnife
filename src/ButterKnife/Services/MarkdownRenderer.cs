using Markdig;

namespace ButterKnife.Services;

/// <summary>
/// Converts model output (markdown) to HTML for rendering as MarkupString.
/// Raw HTML in the source is disabled so a model cannot inject markup or script into the page.
/// </summary>
public sealed class MarkdownRenderer
{
    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()   // tables, task lists, fenced-code attributes, auto-links, etc.
        .UseSoftlineBreakAsHardlineBreak()
        .DisableHtml()
        .Build();

    public string ToHtml(string markdown) =>
        string.IsNullOrEmpty(markdown) ? string.Empty : Markdown.ToHtml(markdown, _pipeline);
}
