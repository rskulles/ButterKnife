using System.Text;
using System.Text.RegularExpressions;

namespace ButterKnife.Services;

/// <summary>An edit worked out: the new file text, how many places changed, and a note for the model when the match needed help.</summary>
public sealed record EditPlan(string Updated, int Occurrences, string? Note);

/// <summary>
/// Finds edit_file's old_string in a file. Exact matches come first. Local models then get some help with the slips
/// they make most: line numbers copied from read_file output, and indentation or trailing spaces that differ (matched
/// line by line, with new_string re-indented to the file). When nothing matches, the error shows the closest lines that
/// are really in the file, numbered, and the first one that differs, because "copy it exactly" alone sends a model that
/// is working from memory round in circles.
/// </summary>
public static partial class EditMatcher
{
    /// <summary>Lines of the closest region shown in a not-found error, at most.</summary>
    public const int MaxShownLines = 40;

    public const string LooseNote = "old_string matched only when indentation and trailing spaces were ignored; new_string was re-indented to match the file. Copy old_string exactly next time.";
    public const string NumbersNote = "The line numbers from read_file output were removed from old_string; leave them out next time.";

    /// <summary>The edit, or a <see cref="CodeToolException"/> that tells the model what to do instead.</summary>
    public static EditPlan Plan(string original, string oldString, string newString, bool replaceAll, string relative)
    {
        if (TryExact(original, oldString, newString, replaceAll, relative) is { } exact)
        {
            return exact;
        }

        // Pasted from read_file output: "    12\tcode". Strip the numbers (from new_string too when it has them).
        if (StripLineNumbers(oldString) is { } unnumbered)
        {
            var newUnnumbered = StripLineNumbers(newString) ?? newString;
            if (TryExact(original, unnumbered, newUnnumbered, replaceAll, relative) is { } plan)
            {
                return plan with { Note = NumbersNote };
            }
            oldString = unnumbered;
            newString = newUnnumbered;
        }

        return TryLoose(original, oldString, newString, replaceAll, relative)
            ?? throw new CodeToolException(NotFound(original, oldString, relative));
    }

    private static EditPlan? TryExact(string original, string oldString, string newString, bool replaceAll, string relative)
    {
        var occurrences = CountOccurrences(original, oldString);
        if (occurrences == 0)
        {
            return null;
        }
        if (occurrences > 1 && !replaceAll)
        {
            throw new CodeToolException($"old_string occurs {occurrences} times in {relative}. Include more surrounding lines to make it unique, or set replace_all to true.");
        }
        var updated = replaceAll ? original.Replace(oldString, newString, StringComparison.Ordinal) : ReplaceFirst(original, oldString, newString);
        return new EditPlan(updated, occurrences, null);
    }

    // ---------------------------------------------------------------- loose, line by line

    private static EditPlan? TryLoose(string original, string oldString, string newString, bool replaceAll, string relative)
    {
        var fileLines = original.Split('\n'); // a CRLF file keeps its '\r' on each line
        var oldLines = TrimBlankEnds(Lines(oldString));
        if (oldLines.Length == 0)
        {
            return null;
        }

        var matches = new List<int>();
        for (var i = 0; i + oldLines.Length <= fileLines.Length; i++)
        {
            if (WindowMatches(fileLines, i, oldLines))
            {
                matches.Add(i);
                i += oldLines.Length - 1; // matches do not overlap
            }
        }
        if (matches.Count == 0)
        {
            return null;
        }
        if (matches.Count > 1 && !replaceAll)
        {
            throw new CodeToolException($"old_string occurs {matches.Count} times in {relative} (ignoring indentation). Include more surrounding lines to make it unique, or set replace_all to true.");
        }

        var newLines = Lines(newString);
        var crlf = original.Contains("\r\n", StringComparison.Ordinal);
        var result = fileLines.ToList();
        foreach (var start in Enumerable.Reverse(matches)) // from the end, so earlier indexes stay valid
        {
            var reindented = Reindent(newLines, oldLines, fileLines, start).ToList();
            if (crlf)
            {
                // Every line of a CRLF file ends in '\r' except the file's last one.
                var endsFile = start + oldLines.Length == fileLines.Length;
                for (var k = 0; k < reindented.Count; k++)
                {
                    if (!(endsFile && k == reindented.Count - 1))
                    {
                        reindented[k] += "\r";
                    }
                }
            }
            result.RemoveRange(start, oldLines.Length);
            result.InsertRange(start, reindented);
        }
        return new EditPlan(string.Join('\n', result), matches.Count, LooseNote);
    }

    private static bool WindowMatches(string[] fileLines, int start, string[] oldLines)
    {
        for (var k = 0; k < oldLines.Length; k++)
        {
            if (fileLines[start + k].Trim() != oldLines[k].Trim())
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Shifts new_string by the difference between the file's indentation and old_string's (from the first non-blank line).</summary>
    private static IEnumerable<string> Reindent(string[] newLines, string[] oldLines, string[] fileLines, int start)
    {
        var first = Array.FindIndex(oldLines, l => l.Trim().Length > 0);
        var oldIndent = Indent(oldLines[first]);
        var fileIndent = Indent(fileLines[start + first].TrimEnd('\r'));
        foreach (var line in newLines)
        {
            if (line.Trim().Length == 0 || oldIndent == fileIndent)
            {
                yield return line.Trim().Length == 0 ? "" : line;
            }
            else if (line.StartsWith(oldIndent, StringComparison.Ordinal))
            {
                yield return fileIndent + line[oldIndent.Length..];
            }
            else
            {
                yield return line; // indented less than old_string's first line: leave it alone
            }
        }
    }

    // ---------------------------------------------------------------- not found: show the closest text

    /// <summary>The not-found message: where the closest lines are, the first that differs, and those lines as they are now.</summary>
    public static string NotFound(string original, string oldString, string relative)
    {
        var fileLines = original.Replace("\r\n", "\n").Split('\n');
        var oldLines = TrimBlankEnds(Lines(oldString));
        var keys = oldLines.Select(l => l.Trim()).ToArray();

        var best = -1;
        var bestScore = 0;
        for (var i = 0; i + Math.Min(keys.Length, fileLines.Length) <= fileLines.Length && keys.Length > 0; i++)
        {
            var score = 0;
            for (var k = 0; k < keys.Length && i + k < fileLines.Length; k++)
            {
                if (keys[k].Length > 0 && fileLines[i + k].Trim() == keys[k])
                {
                    score++;
                }
            }
            if (score > bestScore)
            {
                (best, bestScore) = (i, score);
            }
        }

        var sb = new StringBuilder($"old_string was not found in {relative}.");
        if (best < 0)
        {
            sb.Append(" None of its lines are in the file as written. Read the file again (read_file, or grep for a distinctive word) and copy old_string from that output.");
            return sb.ToString();
        }

        var nonBlank = keys.Count(k => k.Length > 0);
        sb.Append($" The closest text is at lines {best + 1}–{Math.Min(best + keys.Length, fileLines.Length)}, where {bestScore} of its {nonBlank} non-blank lines match.");
        for (var k = 0; k < keys.Length && best + k < fileLines.Length; k++)
        {
            if (fileLines[best + k].Trim() != keys[k])
            {
                sb.Append($"\nThe first difference is at line {best + k + 1}:\n  old_string has: {Clip(oldLines[k].Trim())}\n  the file has:   {Clip(fileLines[best + k].Trim())}");
                break;
            }
        }

        var from = Math.Max(0, best - 2);
        var to = Math.Min(fileLines.Length, best + Math.Min(keys.Length, MaxShownLines) + 2);
        sb.Append($"\nLines {from + 1}–{to} as they are now (copy old_string from here, without the line numbers):\n");
        for (var i = from; i < to; i++)
        {
            sb.Append((i + 1).ToString().PadLeft(6)).Append('\t').Append(fileLines[i]).Append('\n');
        }
        if (keys.Length > MaxShownLines)
        {
            sb.Append($"[old_string is {keys.Length} lines long; use a shorter one: just the lines that change plus a line of context.]\n");
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>old_string without read_file's "number, tab" prefixes, when every non-blank line has one; null otherwise.</summary>
    public static string? StripLineNumbers(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var nonBlank = lines.Where(l => l.Trim().Length > 0).ToArray();
        if (nonBlank.Length == 0 || !nonBlank.All(l => LineNumber().IsMatch(l)))
        {
            return null;
        }
        return string.Join('\n', lines.Select(l => LineNumber().Replace(l, "", 1)));
    }

    private static string[] Lines(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        return text.EndsWith('\n') ? lines[..^1] : lines;
    }

    private static string[] TrimBlankEnds(string[] lines)
    {
        var start = 0;
        var end = lines.Length;
        while (start < end && lines[start].Trim().Length == 0)
        {
            start++;
        }
        while (end > start && lines[end - 1].Trim().Length == 0)
        {
            end--;
        }
        return lines[start..end];
    }

    private static string Indent(string line) => line[..(line.Length - line.TrimStart().Length)];

    private static string Clip(string text) => text.Length <= 160 ? text : text[..160] + "…";

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }

    private static string ReplaceFirst(string text, string oldValue, string newValue)
    {
        var at = text.IndexOf(oldValue, StringComparison.Ordinal);
        return string.Concat(text.AsSpan(0, at), newValue, text.AsSpan(at + oldValue.Length));
    }

    [GeneratedRegex(@"^\s*\d+\t")]
    private static partial Regex LineNumber();
}
