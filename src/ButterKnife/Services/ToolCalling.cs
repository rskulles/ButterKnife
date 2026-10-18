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
