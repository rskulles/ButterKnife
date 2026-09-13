using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace ButterKnife.Services;

/// <summary>
/// Turns an attached file into text for the model: PDFs through PdfPig (page by page, reading order), everything
/// else decoded as UTF-8 text. Binary files are refused; very long text is cut with a note so one attachment cannot
/// swallow a whole context window.
/// </summary>
public static class DocumentExtractor
{
    /// <summary>About 100k tokens of text; more than any local model's window is likely to hold anyway.</summary>
    public const int MaxChars = 400_000;

    public static readonly IReadOnlySet<string> TextExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".rst", ".csv", ".tsv", ".json", ".jsonl", ".xml", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf",
        ".log", ".html", ".htm", ".css", ".js", ".mjs", ".ts", ".jsx", ".tsx", ".py", ".rb", ".go", ".rs", ".java", ".kt", ".swift",
        ".cs", ".fs", ".vb", ".c", ".h", ".cpp", ".hpp", ".cc", ".m", ".sh", ".bash", ".zsh", ".ps1", ".bat", ".sql", ".tex", ".r",
        ".php", ".pl", ".lua", ".dart", ".scala", ".razor", ".cshtml", ".vue", ".svelte", ".env", ".gitignore", ".editorconfig",
    };

    /// <summary>The picker's accept list: images, PDFs and the text extensions above.</summary>
    public static string AcceptList { get; } = "image/*,.pdf,text/*," + string.Join(",", TextExtensions);

    public static bool IsPdf(string name, string? mediaType) =>
        string.Equals(mediaType, "application/pdf", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    public static bool IsText(string name, string? mediaType) =>
        (mediaType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ?? false)
        || TextExtensions.Contains(Path.GetExtension(name))
        || (string.IsNullOrEmpty(Path.GetExtension(name)) && string.IsNullOrEmpty(mediaType));

    public static bool IsSupported(string name, string? mediaType) => IsPdf(name, mediaType) || IsText(name, mediaType);

    /// <summary>Throws <see cref="InvalidDataException"/> with a user-facing message when the file cannot be read as text.</summary>
    public static ChatFile Extract(string name, string? mediaType, byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            throw new InvalidDataException($"{name} is empty.");
        }

        string text;
        if (IsPdf(name, mediaType))
        {
            text = ExtractPdf(name, bytes);
            mediaType = "application/pdf";
        }
        else if (IsText(name, mediaType))
        {
            text = DecodeText(name, bytes);
            mediaType = string.IsNullOrEmpty(mediaType) ? "text/plain" : mediaType;
        }
        else
        {
            throw new InvalidDataException($"{name}: only text files and PDFs can be attached.");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidDataException($"{name} has no readable text (a scanned PDF needs OCR first).");
        }

        text = text.Replace("\r\n", "\n").Trim();
        if (text.Length > MaxChars)
        {
            text = text[..MaxChars] + $"\n\n[cut here: the file goes on for {text.Length - MaxChars:N0} more characters]";
        }

        return new ChatFile(name, mediaType!, bytes.Length, text);
    }

    private static string ExtractPdf(string name, byte[] bytes)
    {
        try
        {
            using var document = PdfDocument.Open(bytes);
            var sb = new StringBuilder();
            foreach (var page in document.GetPages())
            {
                var pageText = ContentOrderTextExtractor.GetText(page).Trim();
                if (pageText.Length == 0)
                {
                    continue;
                }
                if (sb.Length > 0)
                {
                    sb.Append("\n\n");
                }
                sb.Append(pageText);
                if (sb.Length > MaxChars)
                {
                    break; // the rest would be cut anyway
                }
            }
            return sb.ToString();
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"{name} could not be read as a PDF: {ex.Message}");
        }
    }

    private static string DecodeText(string name, byte[] bytes)
    {
        // Binary files have NUL bytes; text files do not (UTF-16 would, but nothing sends that as a document).
        var probe = Math.Min(bytes.Length, 8192);
        if (Array.IndexOf(bytes, (byte)0, 0, probe) >= 0)
        {
            throw new InvalidDataException($"{name} is not a text file.");
        }

        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
        var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return encoding.GetString(bytes, offset, bytes.Length - offset);
    }

    /// <summary>"12.3 KB · about 900 words" for chips and export.</summary>
    public static string Describe(ChatFile file)
    {
        var words = file.Text.Length == 0 ? 0 : file.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var size = file.Size < 1024 ? $"{file.Size} B" : file.Size < 1024 * 1024 ? $"{file.Size / 1024.0:0.#} KB" : $"{file.Size / (1024.0 * 1024):0.#} MB";
        return $"{size} · about {words:N0} words";
    }
}
