using System.Text.RegularExpressions;

namespace ButterKnife.Data;

/// <summary>How ButterKnife reaches an MCP server.</summary>
public enum McpTransportKind
{
    /// <summary>A program ButterKnife starts on this computer and talks to over stdin/stdout.</summary>
    Stdio,

    /// <summary>A server at a URL (Streamable HTTP, or the older SSE transport; detected automatically).</summary>
    Http,
}

/// <summary>
/// An MCP server the Code tab can use. Stdio servers run <see cref="Command"/> with <see cref="Arguments"/> through the
/// user's login shell; HTTP servers are reached at <see cref="Url"/> with <see cref="Headers"/>. <see cref="Environment"/>
/// and <see cref="Headers"/> often hold secrets and are stored encrypted. <see cref="Name"/> prefixes the server's tool
/// names (<c>name__tool</c>), so it is limited to letters, digits, '-' and '_'.
/// </summary>
public sealed partial record McpServer(
    Guid Id,
    string Name,
    McpTransportKind Transport,
    string? Command,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    string? WorkingDirectory,
    string? Url,
    IReadOnlyDictionary<string, string> Headers,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public const int MaxNameLength = 32;

    public static bool IsValidName(string name) => ValidName().IsMatch(name);

    /// <summary>A server name made from any text (an imported config's key): invalid characters become '-', trimmed to the limit.</summary>
    public static string CleanName(string name)
    {
        var cleaned = InvalidNameChars().Replace(name.Trim(), "-").Trim('-');
        if (cleaned.Length > MaxNameLength)
        {
            cleaned = cleaned[..MaxNameLength].Trim('-');
        }
        return cleaned.Length == 0 ? "server" : cleaned;
    }

    /// <summary>One line for the settings table: the command line or the URL.</summary>
    public string Summary => Transport == McpTransportKind.Stdio
        ? string.Join(' ', new[] { Command ?? "" }.Concat(Arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)))
        : Url ?? "";

    [GeneratedRegex("^[A-Za-z0-9_-]{1,32}$")]
    private static partial Regex ValidName();

    [GeneratedRegex("[^A-Za-z0-9_-]+")]
    private static partial Regex InvalidNameChars();
}

/// <summary>In-process signal that MCP servers were added, changed or removed (running ones restart with the new settings).</summary>
public sealed class McpServerEvents
{
    public event Action? Changed;

    public void NotifyChanged() => Changed?.Invoke();
}

public interface IMcpServerStore
{
    Task<IReadOnlyList<McpServer>> ListAsync(CancellationToken cancellationToken = default);

    Task<McpServer?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Adds (Guid.Empty id) or updates a server; returns it as stored. Throws for an invalid or duplicate name.</summary>
    Task<McpServer> SaveAsync(McpServer server, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
