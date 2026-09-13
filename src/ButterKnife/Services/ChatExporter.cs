using System.Globalization;
using System.Text;
using ButterKnife.Data;

namespace ButterKnife.Services;

/// <summary>Renders a stored conversation as a Markdown document for "Export" (one file, readable anywhere).</summary>
public static class ChatExporter
{
    private const int MaxFileNameLength = 80;

    public static string ToMarkdown(Conversation conversation, string userLabel, string? personaName, DateTimeOffset exportedAt)
    {
        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(conversation.Title.Trim().Length > 0 ? conversation.Title.Trim() : "Chat");
        sb.AppendLine();
        sb.Append("*Exported from ButterKnife on ").Append(When(exportedAt)).Append(". Model: ").Append(conversation.Model);
        if (!string.IsNullOrWhiteSpace(personaName))
        {
            sb.Append(". Persona: ").Append(personaName);
        }
        sb.AppendLine(".*");

        foreach (var message in conversation.Messages)
        {
            if (message.Role == ChatRole.System)
            {
                continue; // never stored, but never exported either
            }

            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
            sb.Append("**").Append(message.Role == ChatRole.User ? userLabel : "Assistant");
            if (message.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(message.Model))
            {
                sb.Append(" (").Append(message.Model).Append(')');
            }
            sb.Append("**");
            if (message.CreatedAt is { } at)
            {
                sb.Append(" · ").Append(When(at));
            }
            sb.AppendLine();
            sb.AppendLine();

            if (message.HasImages)
            {
                sb.Append('*').Append(message.Images.Count == 1 ? "1 image attached" : $"{message.Images.Count} images attached").AppendLine("*");
                sb.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(message.Reasoning))
            {
                sb.AppendLine("<details>");
                sb.AppendLine("<summary>Reasoning</summary>");
                sb.AppendLine();
                sb.AppendLine(message.Reasoning.Trim());
                sb.AppendLine();
                sb.AppendLine("</details>");
                sb.AppendLine();
            }

            sb.AppendLine(message.Content.TrimEnd());
        }

        return sb.ToString();
    }

    /// <summary>"Title.md" with characters no filesystem accepts replaced, trimmed to a sane length.</summary>
    public static string FileName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var cleaned = new string(title.Trim().Select(c => invalid.Contains(c) || char.IsControl(c) ? '-' : c).ToArray()).Trim('-', ' ', '.');
        if (cleaned.Length == 0)
        {
            cleaned = "chat";
        }
        if (cleaned.Length > MaxFileNameLength)
        {
            cleaned = cleaned[..MaxFileNameLength].TrimEnd();
        }
        return cleaned + ".md";
    }

    private static string When(DateTimeOffset at) => at.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
