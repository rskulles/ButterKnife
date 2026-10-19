using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;

namespace ButterKnife.Data;

/// <summary>
/// MCP servers in SQLite. Environment variables and headers are JSON encrypted with ASP.NET Core Data Protection, like
/// connection API keys: when the key ring changes they read back empty and must be entered again.
/// </summary>
public sealed class SqliteMcpServerStore(SqliteDatabase db, IDataProtectionProvider dataProtection, ILogger<SqliteMcpServerStore> logger) : IMcpServerStore
{
    private const string Columns = "id, name, transport, command, arguments, environment, working_directory, url, headers, enabled, created_at, updated_at";

    private readonly IDataProtector _protector = dataProtection.CreateProtector("ButterKnife.Mcp.Secrets");

    public async Task<IReadOnlyList<McpServer>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await db.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM mcp_servers ORDER BY name COLLATE NOCASE;";
        var list = new List<McpServer>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(Read(reader));
        }
        return list;
    }

    public async Task<McpServer?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM mcp_servers WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<McpServer> SaveAsync(McpServer server, CancellationToken cancellationToken = default)
    {
        var name = server.Name.Trim();
        if (!McpServer.IsValidName(name))
        {
            throw new ArgumentException($"\"{name}\" is not a valid name: use 1–{McpServer.MaxNameLength} letters, digits, '-' or '_' (it prefixes the server's tool names).");
        }
        if (server.Transport == McpTransportKind.Stdio && string.IsNullOrWhiteSpace(server.Command))
        {
            throw new ArgumentException("A local server needs a command to run.");
        }
        var urlOk = Uri.TryCreate(server.Url, UriKind.Absolute, out var url) && url.Scheme is "http" or "https";
        if (server.Transport == McpTransportKind.Http && !urlOk)
        {
            throw new ArgumentException("A remote server needs an http:// or https:// address.");
        }

        var now = DateTimeOffset.UtcNow;
        var isNew = server.Id == Guid.Empty;
        var saved = server with
        {
            Id = isNew ? Guid.NewGuid() : server.Id,
            Name = name,
            Command = server.Command?.Trim(),
            WorkingDirectory = string.IsNullOrWhiteSpace(server.WorkingDirectory) ? null : server.WorkingDirectory.Trim(),
            Url = server.Url?.Trim(),
            CreatedAt = isNew ? now : server.CreatedAt,
            UpdatedAt = now,
        };

        await using var connection = await db.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = isNew
            ? """
              INSERT INTO mcp_servers (id, name, transport, command, arguments, environment, working_directory, url, headers, enabled, created_at, updated_at)
              VALUES ($id, $name, $transport, $command, $arguments, $environment, $cwd, $url, $headers, $enabled, $created, $updated);
              """
            : """
              UPDATE mcp_servers SET name = $name, transport = $transport, command = $command, arguments = $arguments,
                  environment = $environment, working_directory = $cwd, url = $url, headers = $headers, enabled = $enabled, updated_at = $updated
              WHERE id = $id;
              """;
        cmd.Parameters.AddWithValue("$id", saved.Id.ToString("D"));
        cmd.Parameters.AddWithValue("$name", saved.Name);
        cmd.Parameters.AddWithValue("$transport", saved.Transport.ToString());
        cmd.Parameters.AddWithValue("$command", (object?)saved.Command ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$arguments", JsonSerializer.Serialize(saved.Arguments));
        cmd.Parameters.AddWithValue("$environment", (object?)Protect(saved.Environment) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cwd", (object?)saved.WorkingDirectory ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$url", (object?)saved.Url ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$headers", (object?)Protect(saved.Headers) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$enabled", saved.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$created", SqliteDatabase.Format(saved.CreatedAt));
        cmd.Parameters.AddWithValue("$updated", SqliteDatabase.Format(saved.UpdatedAt));
        try
        {
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // constraint: the name is unique
        {
            throw new ArgumentException($"There is already a server called \"{saved.Name}\".");
        }
        return saved;
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM mcp_servers WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private McpServer Read(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        Enum.TryParse<McpTransportKind>(reader.GetString(2), out var transport) ? transport : McpTransportKind.Stdio,
        reader.IsDBNull(3) ? null : reader.GetString(3),
        JsonSerializer.Deserialize<string[]>(reader.GetString(4)) ?? [],
        Unprotect(reader.IsDBNull(5) ? null : reader.GetString(5)),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        Unprotect(reader.IsDBNull(8) ? null : reader.GetString(8)),
        reader.GetInt64(9) != 0,
        SqliteDatabase.Parse(reader.GetString(10)),
        SqliteDatabase.Parse(reader.GetString(11)));

    private string? Protect(IReadOnlyDictionary<string, string> values) =>
        values.Count == 0 ? null : _protector.Protect(JsonSerializer.Serialize(values));

    private IReadOnlyDictionary<string, string> Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return new Dictionary<string, string>();
        }
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(_protector.Unprotect(stored)) ?? [];
        }
        catch (CryptographicException ex)
        {
            logger.LogWarning(ex, "Could not decrypt MCP server secrets (the key ring changed); they must be entered again");
            return new Dictionary<string, string>();
        }
    }
}
