using System.Text;

namespace ButterKnife.Services;

/// <summary>
/// Separates reasoning from answer text for models that emit their thinking inline as &lt;think&gt;…&lt;/think&gt;
/// (Qwen3, DeepSeek-R1 through servers that do not split it out). Feed streamed chunks in order; tags may be split
/// across chunks, so a partial "&lt;" run is held back until it can be classified. Text before the first tag and
/// after the closing tag is answer text; everything between is reasoning.
/// </summary>
public sealed class ThinkTagSplitter
{
    private static readonly string[] OpenTags = ["<think>", "<thinking>"];
    private static readonly string[] CloseTags = ["</think>", "</thinking>"];

    private readonly StringBuilder _pending = new();
    private bool _inThink;

    public readonly record struct Piece(string? Text, string? Reasoning);

    /// <summary>Classifies as much of the input as is unambiguous; the rest waits for the next chunk.</summary>
    public IEnumerable<Piece> Feed(string chunk)
    {
        _pending.Append(chunk);
        var buffer = _pending.ToString();
        _pending.Clear();

        var index = 0;
        while (index < buffer.Length)
        {
            var lt = buffer.IndexOf('<', index);
            if (lt < 0)
            {
                yield return Emit(buffer[index..]);
                yield break;
            }

            if (lt > index)
            {
                yield return Emit(buffer[index..lt]);
            }

            var tags = _inThink ? CloseTags : OpenTags;
            var matched = tags.FirstOrDefault(t => string.CompareOrdinal(buffer, lt, t, 0, t.Length) == 0);
            if (matched is not null)
            {
                _inThink = !_inThink;
                index = lt + matched.Length;
                continue;
            }

            // Could the remainder still grow into one of the tags? Then hold it for the next chunk.
            var rest = buffer[lt..];
            if (tags.Any(t => t.StartsWith(rest, StringComparison.Ordinal)))
            {
                _pending.Append(rest);
                yield break;
            }

            yield return Emit("<");
            index = lt + 1;
        }
    }

    /// <summary>End of stream: whatever was held back is ordinary text (or reasoning, if a tag was never closed).</summary>
    public Piece? Flush()
    {
        if (_pending.Length == 0)
        {
            return null;
        }
        var rest = _pending.ToString();
        _pending.Clear();
        return Emit(rest);
    }

    private Piece Emit(string s) => _inThink ? new Piece(null, s) : new Piece(s, null);
}
