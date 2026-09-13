using ButterKnife.Services;

namespace ButterKnife.Tests;

public class MarkdownRendererTests
{
    private readonly MarkdownRenderer _renderer = new();

    [Fact]
    public void RendersCommonMarkdown()
    {
        var html = _renderer.ToHtml("# Title\n\nSome **bold** and `code`.\n\n```csharp\nvar x = 1;\n```\n\n- a\n- b");

        Assert.Contains("<h1", html);
        Assert.Contains("<strong>bold</strong>", html);
        Assert.Contains("<code>code</code>", html);
        Assert.Contains("<pre><code class=\"language-csharp\">", html);
        Assert.Contains("<li>a</li>", html);
    }

    [Fact]
    public void RendersTables()
    {
        var html = _renderer.ToHtml("| a | b |\n|---|---|\n| 1 | 2 |");

        Assert.Contains("<table class=\"table\">", html);
        Assert.Contains("<td>2</td>", html);
    }

    [Fact]
    public void EscapesRawHtmlFromModelOutput()
    {
        var html = _renderer.ToHtml("hi <script>alert(1)</script> <img src=x onerror=alert(1)>");

        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<img", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void MarksMathAndMermaidForClientSideRendering()
    {
        var html = _renderer.ToHtml("Inline $E = mc^2$ here.\n\n$$\nx^2\n$$\n\n```mermaid\ngraph LR\n  A --> B <script>x</script>\n```\n");

        Assert.Contains("<span class=\"math\">\\(E = mc^2\\)</span>", html);
        Assert.Contains("<div class=\"math\">", html);
        Assert.Contains("<pre class=\"mermaid\">graph LR", html);
        Assert.Contains("A --> B", html);            // diagram source stays text for Mermaid to render in the browser...
        Assert.Contains("&lt;script>x&lt;/script>", html); // ...with tags escaped, so a model cannot inject markup through a diagram
    }

    [Fact]
    public void EmptyInputRendersEmpty()
    {
        Assert.Equal(string.Empty, _renderer.ToHtml(""));
    }
}
