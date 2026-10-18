using System.Text;

namespace ButterKnife.Services;

public enum DiffKind
{
    Context,
    Added,
    Removed,

    /// <summary>Unchanged lines left out between two hunks.</summary>
    Gap,
}

public sealed record DiffLine(DiffKind Kind, string Text, int? OldLine = null, int? NewLine = null);

/// <summary>A line diff ready to show: hunks with a few lines of context, and the counts for a "+3 −1" badge.</summary>
public sealed record LineDiffResult(IReadOnlyList<DiffLine> Lines, int Added, int Removed)
{
    public bool IsEmpty => Added == 0 && Removed == 0;

    /// <summary>The diff as unified-diff body lines ("+", "-", " ", and "@@" between hunks), which is how it is stored.</summary>
    public string ToUnified()
    {
        var sb = new StringBuilder();
        foreach (var line in Lines)
        {
            sb.Append(line.Kind switch
            {
                DiffKind.Added => "+",
                DiffKind.Removed => "-",
                DiffKind.Gap => "@@",
                _ => " ",
            });
            if (line.Kind != DiffKind.Gap)
            {
                sb.Append(line.Text);
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Reads back what <see cref="ToUnified"/> wrote (line numbers are not kept).</summary>
    public static LineDiffResult FromUnified(string unified)
    {
        var lines = new List<DiffLine>();
        int added = 0, removed = 0;
        foreach (var raw in LineDiff.SplitLines(unified))
        {
            if (raw.StartsWith("@@", StringComparison.Ordinal))
            {
                lines.Add(new DiffLine(DiffKind.Gap, ""));
            }
            else if (raw.StartsWith('+'))
            {
                lines.Add(new DiffLine(DiffKind.Added, raw[1..]));
                added++;
            }
            else if (raw.StartsWith('-'))
            {
                lines.Add(new DiffLine(DiffKind.Removed, raw[1..]));
                removed++;
            }
            else
            {
                lines.Add(new DiffLine(DiffKind.Context, raw.Length > 0 ? raw[1..] : ""));
            }
        }
        return new LineDiffResult(lines, added, removed);
    }
}

/// <summary>
/// Line diff for previews of file edits: common head and tail are trimmed, the middle is aligned by longest common
/// subsequence (or, when it is too large for that, shown as a block replaced by another).
/// </summary>
public static class LineDiff
{
    private const int ContextLines = 3;
    private const long MaxCells = 4_000_000;

    public static string[] SplitLines(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }
        var lines = text.Replace("\r\n", "\n").Split('\n');
        return text.EndsWith('\n') ? lines[..^1] : lines;
    }

    public static LineDiffResult Compute(string oldText, string newText)
    {
        var a = SplitLines(oldText);
        var b = SplitLines(newText);

        var head = 0;
        while (head < a.Length && head < b.Length && a[head] == b[head])
        {
            head++;
        }
        var tail = 0;
        while (tail < a.Length - head && tail < b.Length - head && a[a.Length - 1 - tail] == b[b.Length - 1 - tail])
        {
            tail++;
        }

        var all = new List<DiffLine>();
        for (var i = 0; i < head; i++)
        {
            all.Add(new DiffLine(DiffKind.Context, a[i], i + 1, i + 1));
        }
        AlignMiddle(a, b, head, a.Length - tail, head, b.Length - tail, all);
        for (var i = 0; i < tail; i++)
        {
            var ai = a.Length - tail + i;
            var bi = b.Length - tail + i;
            all.Add(new DiffLine(DiffKind.Context, a[ai], ai + 1, bi + 1));
        }

        return new LineDiffResult(Hunks(all), all.Count(l => l.Kind == DiffKind.Added), all.Count(l => l.Kind == DiffKind.Removed));
    }

    private static void AlignMiddle(string[] a, string[] b, int aStart, int aEnd, int bStart, int bEnd, List<DiffLine> output)
    {
        var n = aEnd - aStart;
        var m = bEnd - bStart;
        if ((long)n * m > MaxCells)
        {
            for (var i = aStart; i < aEnd; i++)
            {
                output.Add(new DiffLine(DiffKind.Removed, a[i], i + 1));
            }
            for (var j = bStart; j < bEnd; j++)
            {
                output.Add(new DiffLine(DiffKind.Added, b[j], null, j + 1));
            }
            return;
        }

        // lcs[i, j] = length of the LCS of a[aStart+i..] and b[bStart+j..]
        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = a[aStart + i] == b[bStart + j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        int x = 0, y = 0;
        while (x < n || y < m)
        {
            if (x < n && y < m && a[aStart + x] == b[bStart + y])
            {
                output.Add(new DiffLine(DiffKind.Context, a[aStart + x], aStart + x + 1, bStart + y + 1));
                x++;
                y++;
            }
            else if (x < n && (y == m || lcs[x + 1, y] >= lcs[x, y + 1]))
            {
                // On a tie the removal goes first, so a replaced line reads "-old" then "+new".
                output.Add(new DiffLine(DiffKind.Removed, a[aStart + x], aStart + x + 1));
                x++;
            }
            else
            {
                output.Add(new DiffLine(DiffKind.Added, b[bStart + y], null, bStart + y + 1));
                y++;
            }
        }
    }

    /// <summary>Keeps changed lines plus a few lines of context around them; longer unchanged runs become a gap.</summary>
    private static List<DiffLine> Hunks(List<DiffLine> all)
    {
        var keep = new bool[all.Count];
        for (var i = 0; i < all.Count; i++)
        {
            if (all[i].Kind == DiffKind.Context)
            {
                continue;
            }
            for (var k = Math.Max(0, i - ContextLines); k <= Math.Min(all.Count - 1, i + ContextLines); k++)
            {
                keep[k] = true;
            }
        }

        var result = new List<DiffLine>();
        var skipped = false;
        for (var i = 0; i < all.Count; i++)
        {
            if (keep[i])
            {
                if (skipped && result.Count > 0)
                {
                    result.Add(new DiffLine(DiffKind.Gap, ""));
                }
                skipped = false;
                result.Add(all[i]);
            }
            else
            {
                skipped = true;
            }
        }
        return result;
    }
}
