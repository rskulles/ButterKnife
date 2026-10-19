using System.Text.Json;

namespace ButterKnife.Services;

/// <summary>
/// A function the model may call: name, what it does, and a JSON Schema object for its arguments. Every backend
/// receives the same schema in its own envelope (Ollama and OpenAI-compatible "function" tools, Anthropic tools).
/// </summary>
public sealed record ToolDefinition(string Name, string Description, JsonElement Parameters)
{
    /// <summary>Builds the schema from a JSON string, which is how the tool catalogue writes them.</summary>
    public static ToolDefinition Create(string name, string description, string parametersJson)
    {
        using var document = JsonDocument.Parse(parametersJson);
        return new ToolDefinition(name, description, document.RootElement.Clone());
    }
}

/// <summary>
/// A call the model asked for. <see cref="Arguments"/> is the raw JSON object text as the model wrote it (local models
/// sometimes write invalid JSON; the caller reports that back instead of failing). <see cref="Id"/> ties the result to
/// the call: OpenAI and Anthropic send one, Ollama may not, and the client then makes one up.
/// </summary>
public sealed record ToolCall(string Id, string Name, string Arguments)
{
    public static string NewId() => "call_" + Guid.NewGuid().ToString("N")[..20];

    /// <summary>The arguments as a JSON object, or null when the text is not one.</summary>
    public JsonElement? TryParseArguments()
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(Arguments) ? "{}" : Arguments);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Pictures returned by tools (read_file on an image). Anthropic takes them inside the tool result; OpenAI-compatible
/// servers and Ollama accept images only on user messages, so for them the pictures move, on the wire only, into one
/// user message right after the run of tool results they came from.
/// </summary>
public static class ToolImages
{
    public const string Caption = "The pictures returned by the tool calls above:";

    /// <summary>The messages with tool-result images moved into a following user message; the same list when there are none.</summary>
    public static IReadOnlyList<ChatMessage> MoveToUserMessages(IReadOnlyList<ChatMessage> messages)
    {
        if (!messages.Any(m => m.Role == ChatRole.Tool && m.HasImages))
        {
            return messages;
        }

        var result = new List<ChatMessage>(messages.Count + 1);
        var pending = new List<ChatImage>();
        var sources = new List<string>();

        void Flush()
        {
            if (pending.Count == 0)
            {
                return;
            }
            result.Add(new ChatMessage(ChatRole.User, $"{Caption} {string.Join(", ", sources)}.", pending.ToArray()));
            pending.Clear();
            sources.Clear();
        }

        foreach (var message in messages)
        {
            if (message.Role == ChatRole.Tool)
            {
                if (message.HasImages)
                {
                    pending.AddRange(message.Images);
                    sources.Add($"{message.ToolName ?? "tool"} ({message.Images.Count} picture{(message.Images.Count == 1 ? "" : "s")})");
                    result.Add(message with { Images = ChatMessage.NoImages });
                }
                else
                {
                    result.Add(message);
                }
                continue;
            }
            Flush();
            result.Add(message);
        }
        Flush();
        return result;
    }
}
