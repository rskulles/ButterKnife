using System.Text;
using System.Text.RegularExpressions;

namespace ButterKnife.Services;

/// <summary>
/// Asks the model that answered for a short conversation title after the first exchange, replacing the title cut
/// from the prompt. Best effort: any failure returns null and the placeholder title stays.
/// </summary>
public sealed partial class ChatTitler(ILlmClientRegistry registry, ILogger<ChatTitler> logger)
{
    public const int MaxLength = 60;

    /// <summary>How much of the prompt and reply the model sees; a title needs the gist, not the whole text.</summary>
    private const int ExcerptLength = 1500;

    /// <summary>A model that keeps talking is not going to produce a title; stop reading after this much.</summary>
    private const int MaxRawLength = 400;

    /// <summary>The request opens with this line; the stub server recognises it (see tools/stub-llm-server.py).</summary>
    public const string Instruction = "Write a title of at most six words for the conversation below. Reply with the title only: no quotes, no punctuation at the end, no explanation.";

    public async Task<string?> SuggestAsync(Guid connectionId, string model, string prompt, string reply, CancellationToken cancellationToken)
    {
        try
        {
            var client = await registry.GetAsync(connectionId, cancellationToken);
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, "You write short, specific titles for chat conversations."),
                new(ChatRole.User, $"{Instruction}\n\nUser:\n{Excerpt(prompt)}\n\nAssistant:\n{Excerpt(reply)}"),
            };

            var raw = new StringBuilder();
            var splitter = new ThinkTagSplitter(); // inline <think> blocks are not part of the title
            await foreach (var delta in client.StreamChatAsync(model, messages, cancellationToken))
            {
                if (delta.Text is not { } text)
                {
                    continue;
                }
                foreach (var piece in splitter.Feed(text))
                {
                    if (piece.Text is { } t)
                    {
                        raw.Append(t);
                    }
                }
                if (raw.Length > MaxRawLength)
                {
                    break;
                }
            }
            if (splitter.Flush() is { Text: { } tail })
            {
                raw.Append(tail);
            }

            return Clean(raw.ToString());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not get a title from {Model}", model);
            return null;
        }
    }

    /// <summary>
    /// The first non-empty line of what the model said, without a "Title:" prefix, Markdown emphasis or heading
    /// marks, surrounding quotes or a trailing full stop, trimmed to <see cref="MaxLength"/>. Null when nothing usable is left.
    /// </summary>
    public static string? Clean(string raw)
    {
        var line = raw.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (line is null)
        {
            return null;
        }

        line = TitlePrefix().Replace(line, "");
        line = line.Trim(' ', '*', '_', '#', '`', '"', '\'', '“', '”', '‘', '’', '«', '»');
        line = line.TrimEnd('.', '!', ':', ' ');
        if (line.Length == 0)
        {
            return null;
        }

        return line.Length <= MaxLength ? line : line[..MaxLength].TrimEnd() + "…";
    }

    private static string Excerpt(string text) => text.Length <= ExcerptLength ? text : text[..ExcerptLength] + "…";

    [GeneratedRegex(@"^\s*(\**\s*title\s*\**\s*:\s*)", RegexOptions.IgnoreCase)]
    private static partial Regex TitlePrefix();
}
