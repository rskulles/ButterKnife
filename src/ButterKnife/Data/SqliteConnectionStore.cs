using System.Security.Cryptography;
using ButterKnife.Options;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;

namespace ButterKnife.Data;

/// <summary>API keys are encrypted with ASP.NET Core Data Protection before they touch the database.</summary>
public sealed class SqliteConnectionStore : IConnectionStore
{
    private const string Columns = "id, name, kind, base_url, api_key, default_model, context_window, created_at, updated_at";

    private readonly SqliteDatabase _db;
    private readonly IDataProtector _protector;
    private readonly ILogger<SqliteConnectionStore> _logger;

    public SqliteConnectionStore(SqliteDatabase db, IDataProtectionProvider dataProtection, ILogger<SqliteConnectionStore> logger)
    {
        _db = db;
        _protector = dataProtection.CreateProtector("ButterKnife.Connections.ApiKey");
        _logger = logger;
    }

    public async Task<IReadOnlyList<LlmConnection>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _db.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM connections ORDER BY name COLLATE NOCASE;";

        var list = new List<LlmConnection>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(Read(reader));
        }
        return list;
    }

    public async Task<LlmConnection?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _db.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM connections WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<LlmConnection> CreateAsync(string name, BackendKind kind, string baseUrl, string? apiKey, string? defaultModel, int? contextWindow, CancellationToken cancellationToken = default)
    {
        Validate(name, baseUrl);
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using var connection = await _db.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO connections (id, name, kind, base_url, api_key, default_model, context_window, created_at, updated_at)
            VALUES ($id, $name, $kind, $url, $key, $model, $ctx, $now, $now);
            """;
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        AddFields(cmd, name, kind, baseUrl, apiKey, defaultModel, contextWindow);
        cmd.Parameters.AddWithValue("$now", SqliteDatabase.Format(now));
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        return new LlmConnection(id, name.Trim(), kind, baseUrl.Trim(), Clean(apiKey), Clean(defaultModel), Positive(contextWindow), now, now);
    }

    public async Task UpdateAsync(Guid id, string name, BackendKind kind, string baseUrl, string? apiKey, string? defaultModel, int? contextWindow, CancellationToken cancellationToken = default)
    {
        Validate(name, baseUrl);

        await using var connection = await _db.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE connections
            SET name = $name, kind = $kind, base_url = $url, api_key = $key, default_model = $model, context_window = $ctx, updated_at = $now
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        AddFields(cmd, name, kind, baseUrl, apiKey, defaultModel, contextWindow);
        cmd.Parameters.AddWithValue("$now", SqliteDatabase.Format(DateTimeOffset.UtcNow));

        if (await cmd.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            throw new KeyNotFoundException($"Connection {id} does not exist.");
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _db.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM connections WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private void AddFields(SqliteCommand cmd, string name, BackendKind kind, string baseUrl, string? apiKey, string? defaultModel, int? contextWindow)
    {
        cmd.Parameters.AddWithValue("$ctx", (object?)Positive(contextWindow) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$name", name.Trim());
        cmd.Parameters.AddWithValue("$kind", kind.ToString());
        cmd.Parameters.AddWithValue("$url", baseUrl.Trim());
        cmd.Parameters.AddWithValue("$key", Clean(apiKey) is { } key ? _protector.Protect(key) : DBNull.Value);
        cmd.Parameters.AddWithValue("$model", (object?)Clean(defaultModel) ?? DBNull.Value);
    }

    private LlmConnection Read(SqliteDataReader reader)
    {
        var id = Guid.Parse(reader.GetString(0));
        string? apiKey = null;
        if (!reader.IsDBNull(4))
        {
            try
            {
                apiKey = _protector.Unprotect(reader.GetString(4));
            }
            catch (CryptographicException ex)
            {
                // Data-protection key ring changed (new machine/user). The key must be re-entered.
                _logger.LogWarning(ex, "Could not decrypt the API key for connection {Id}; treating it as unset", id);
            }
        }

        return new LlmConnection(
            id,
            reader.GetString(1),
            Enum.Parse<BackendKind>(reader.GetString(2)),
            reader.GetString(3),
            apiKey,
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : (int)reader.GetInt64(6),
            SqliteDatabase.Parse(reader.GetString(7)),
            SqliteDatabase.Parse(reader.GetString(8)));
    }

    private static int? Positive(int? value) => value is > 0 ? value : null;

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void Validate(string name, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Connection name is required.", nameof(name));
        }
        if (!Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            throw new ArgumentException("Base URL must be an absolute http(s) URL.", nameof(baseUrl));
        }
    }
}
