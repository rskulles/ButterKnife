using System.Diagnostics;
using System.Text;
using ButterKnife.Services;
using Microsoft.AspNetCore.Components;

namespace ButterKnife.Components.Pages;

/// <summary>Mutable UI-side message so streamed tokens can be appended in place.</summary>
/// <remarks>Rendered HTML is cached until the text changes, so throttled re-renders during streaming do not re-parse older messages.</remarks>
internal sealed class ChatTurn(ChatRole role, string text, MarkdownRenderer markdown, IReadOnlyList<ChatImage>? images = null)
{
    private readonly StringBuilder _text = new(text);
    private MarkupString? _html;
    private bool _isStreaming;
    private long _startedAt;
    private long? _firstTokenAt;
    private long? _endedAt;

    public ChatRole Role { get; } = role;
    public IReadOnlyList<ChatImage> Images { get; } = images ?? ChatMessage.NoImages;
    public string Text => _text.ToString();

    /// <summary>Streamed chunks; a close stand-in for tokens until the backend reports real counts.</summary>
    public int Chunks { get; private set; }

    /// <summary>Set when the backend reports usage at the end of the stream.</summary>
    public TokenUsage? Usage { get; set; }

    public TimeSpan? TimeToFirstToken => _firstTokenAt is { } first && _startedAt != 0
        ? Stopwatch.GetElapsedTime(_startedAt, first)
        : null;

    public GenerationStats GetStats() => new(Elapsed, TimeToFirstToken, Chunks, Usage);

    public bool IsStreaming
    {
        get => _isStreaming;
        set
        {
            if (value && !_isStreaming)
            {
                _startedAt = Stopwatch.GetTimestamp();
                _firstTokenAt = null;
                _endedAt = null;
            }
            else if (!value && _isStreaming)
            {
                _endedAt = Stopwatch.GetTimestamp();
            }
            _isStreaming = value;
        }
    }

    public TimeSpan Elapsed => _startedAt == 0
        ? TimeSpan.Zero
        : Stopwatch.GetElapsedTime(_startedAt, _endedAt ?? Stopwatch.GetTimestamp());

    public MarkupString Html => _html ??= new MarkupString(markdown.ToHtml(Text));

    public void Append(string delta)
    {
        _firstTokenAt ??= Stopwatch.GetTimestamp();
        _text.Append(delta);
        _html = null;
        Chunks++;
    }
}
