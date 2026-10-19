using System.Text.Json;
using ButterKnife.Data;

namespace ButterKnife.Services;

/// <summary>Servers read from pasted configuration, and what could not be read.</summary>
public sealed record McpImportResult(IReadOnlyList<McpServer> Servers, IReadOnlyList<string> Problems);

/// <summary>
/// Reads the <c>mcpServers</c> JSON that Claude Desktop (<c>claude_desktop_config.json</c>), Claude Code
/// (<c>.mcp.json</c>) and most other MCP clients use: a map of name to <c>command</c>/<c>args</c>/<c>env</c>/<c>cwd</c> for
/// a local server, or <c>url</c>/<c>headers</c> (with <c>type</c> http or sse) for a remote one. The whole file, the
/// inner map, or a single server object all work.
/// </summary>
public static class McpImport
{
    public static McpImportResult Parse(string json)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            root = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return new McpImportResult([], [$"That is not valid JSON: {ex.Message}"]);
        }
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new McpImportResult([], ["Expected a JSON object with an \"mcpServers\" entry."]);
        }

        var entries = new List<(string Name, JsonElement Value)>();
        if (root.TryGetProperty("mcpServers", out var map) && map.ValueKind == JsonValueKind.Object)
        {
            entries.AddRange(map.EnumerateObject().Select(p => (p.Name, p.Value)));
        }
        else if (IsServer(root))
        {
            entries.Add(("server", root));
        }
        else
        {
            entries.AddRange(root.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Object).Select(p => (p.Name, p.Value)));
        }

        var servers = new List<McpServer>();
        var problems = new List<string>();
        foreach (var (name, value) in entries)
        {
            if (Read(McpServer.CleanName(name), value) is { } server)
            {
                servers.Add(server);
            }
            else
            {
                problems.Add($"{name}: no \"command\" or \"url\".");
            }
        }
        if (entries.Count == 0)
        {
            problems.Add("No servers found: expected { \"mcpServers\": { \"name\": { \"command\": … } } }.");
        }
        return new McpImportResult(servers, problems);
    }

    private static bool IsServer(JsonElement value) =>
        value.TryGetProperty("command", out _) || value.TryGetProperty("url", out _);

    private static McpServer? Read(string name, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var now = DateTimeOffset.UtcNow;
        var url = String(value, "url") ?? String(value, "serverUrl");
        var command = String(value, "command");
        if (url is not null && command is null)
        {
            return new McpServer(Guid.Empty, name, McpTransportKind.Http, null, [], Map(value, "env"), null, url, Map(value, "headers"), true, now, now);
        }
        if (command is null)
        {
            return null;
        }
        var arguments = value.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array
            ? args.EnumerateArray().Select(a => a.ValueKind == JsonValueKind.String ? a.GetString()! : a.GetRawText()).ToList()
            : [];
        return new McpServer(Guid.Empty, name, McpTransportKind.Stdio, command, arguments, Map(value, "env"), String(value, "cwd"), null,
            new Dictionary<string, string>(), true, now, now);
    }

    private static string? String(JsonElement value, string property) =>
        value.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.GetString()) ? p.GetString()!.Trim() : null;

    private static Dictionary<string, string> Map(JsonElement value, string property)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (value.TryGetProperty(property, out var map) && map.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in map.EnumerateObject())
            {
                result[entry.Name] = entry.Value.ValueKind == JsonValueKind.String ? entry.Value.GetString()! : entry.Value.GetRawText();
            }
        }
        return result;
    }

    // ---------------------------------------------------------------- the settings form's text boxes

    /// <summary>One argument per line (blank lines dropped), so arguments with spaces need no quoting.</summary>
    public static List<string> ParseLines(string? text) =>
        (text ?? "").Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();

    /// <summary>
    /// <c>KEY=value</c> (or, with <paramref name="separator"/> ':', <c>Name: value</c>) per line. Returns null and names the
    /// line when one has no separator or an empty key.
    /// </summary>
    public static Dictionary<string, string>? ParsePairs(string? text, char separator, out string? problem)
    {
        problem = null;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in ParseLines(text))
        {
            var at = line.IndexOf(separator);
            var key = at > 0 ? line[..at].Trim() : "";
            if (key.Length == 0)
            {
                problem = $"\"{line.Trim()}\" needs the form {(separator == '=' ? "KEY=value" : "Name: value")}.";
                return null;
            }
            result[key] = line[(at + 1)..].Trim();
        }
        return result;
    }

    public static string FormatPairs(IReadOnlyDictionary<string, string> pairs, char separator) =>
        string.Join('\n', pairs.Select(p => separator == '=' ? $"{p.Key}={p.Value}" : $"{p.Key}: {p.Value}"));
}
