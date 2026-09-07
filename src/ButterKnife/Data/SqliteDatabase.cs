using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace ButterKnife.Data;

/// <summary>
/// Owns the SQLite connection string, schema creation, forward-only migrations and seed data.
/// Stores call <see cref="OpenAsync"/> per operation; the schema is ensured once per process.
/// Timestamps are stored as ISO-8601 UTC strings so ORDER BY works lexically.
/// </summary>
public sealed class SqliteDatabase
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public SqliteDatabase(IOptions<DatabaseOptions> options, IHostEnvironment? environment = null)
    {
        var builder = new SqliteConnectionStringBuilder(options.Value.ConnectionString);

        if (!string.Equals(builder.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase) &&
            !Path.IsPathRooted(builder.DataSource))
        {
            var root = environment?.ContentRootPath ?? Directory.GetCurrentDirectory();
            builder.DataSource = Path.GetFullPath(builder.DataSource, root);
        }

        var dir = Path.GetDirectoryName(builder.DataSource);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connectionString = builder.ToString();
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        // Per-connection pragma; cascade / set-null on delete depend on it.
        await ExecAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken);

        await EnsureSchemaAsync(connection, cancellationToken);
        return connection;
    }

    public static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    public static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            await ExecAsync(connection, """
                PRAGMA journal_mode = WAL;

                CREATE TABLE IF NOT EXISTS personas (
                    id            TEXT PRIMARY KEY,
                    name          TEXT NOT NULL,
                    description   TEXT NOT NULL DEFAULT '',
                    system_prompt TEXT NOT NULL,
                    is_builtin    INTEGER NOT NULL DEFAULT 0,
                    sort_order    INTEGER NOT NULL DEFAULT 0,
                    created_at    TEXT NOT NULL,
                    updated_at    TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS connections (
                    id             TEXT PRIMARY KEY,
                    name           TEXT NOT NULL,
                    kind           TEXT NOT NULL,
                    base_url       TEXT NOT NULL,
                    api_key        TEXT NULL,
                    default_model  TEXT NULL,
                    context_window INTEGER NULL,
                    created_at     TEXT NOT NULL,
                    updated_at     TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS conversations (
                    id              TEXT PRIMARY KEY,
                    title           TEXT NOT NULL,
                    backend         TEXT NOT NULL, -- connection id (GUID)
                    model           TEXT NOT NULL,
                    persona_id      TEXT NULL REFERENCES personas(id) ON DELETE SET NULL,
                    summary         TEXT NULL,     -- compaction: summary of the first summary_through messages
                    summary_through INTEGER NULL,
                    context_tokens  INTEGER NULL,  -- prompt+completion tokens of the last request, as reported
                    context_window  INTEGER NULL,  -- window of the model used for the last request, if known
                    created_at      TEXT NOT NULL,
                    updated_at      TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS messages (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    conversation_id TEXT NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
                    role            TEXT NOT NULL,
                    content         TEXT NOT NULL,
                    created_at      TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_messages_conversation ON messages(conversation_id, id);

                CREATE TABLE IF NOT EXISTS message_images (
                    id         INTEGER PRIMARY KEY AUTOINCREMENT,
                    message_id INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
                    media_type TEXT NOT NULL,
                    data       BLOB NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_message_images_message ON message_images(message_id);
                """, cancellationToken);

            // Databases created before these columns existed.
            await AddColumnIfMissingAsync(connection, "conversations", "persona_id",
                "TEXT NULL REFERENCES personas(id) ON DELETE SET NULL", cancellationToken);
            await AddColumnIfMissingAsync(connection, "conversations", "summary", "TEXT NULL", cancellationToken);
            await AddColumnIfMissingAsync(connection, "conversations", "summary_through", "INTEGER NULL", cancellationToken);
            await AddColumnIfMissingAsync(connection, "conversations", "context_tokens", "INTEGER NULL", cancellationToken);
            await AddColumnIfMissingAsync(connection, "conversations", "context_window", "INTEGER NULL", cancellationToken);
            await AddColumnIfMissingAsync(connection, "connections", "context_window", "INTEGER NULL", cancellationToken);

            await SeedPersonasAsync(connection, cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>INSERT OR IGNORE by stable id: adds new built-ins, never overwrites edits or re-creates deleted rows' edits.</summary>
    private static async Task SeedPersonasAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var now = Format(DateTimeOffset.UtcNow);
        var order = 0;

        foreach (var seed in DefaultPersonas.All)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO personas (id, name, description, system_prompt, is_builtin, sort_order, created_at, updated_at)
                VALUES ($id, $name, $description, $prompt, 1, $order, $now, $now);
                """;
            cmd.Parameters.AddWithValue("$id", seed.Id.ToString("D"));
            cmd.Parameters.AddWithValue("$name", seed.Name);
            cmd.Parameters.AddWithValue("$description", seed.Description);
            cmd.Parameters.AddWithValue("$prompt", seed.SystemPrompt);
            cmd.Parameters.AddWithValue("$order", order++);
            cmd.Parameters.AddWithValue("$now", now);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>Minimal forward-only migration: databases created before a column existed get it added.</summary>
    private static async Task AddColumnIfMissingAsync(SqliteConnection connection, string table, string column, string definition, CancellationToken cancellationToken)
    {
        await using (var info = connection.CreateCommand())
        {
            info.CommandText = $"PRAGMA table_info({table});";
            await using var reader = await info.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        await ExecAsync(connection, $"ALTER TABLE {table} ADD COLUMN {column} {definition};", cancellationToken);
    }

    private static async Task ExecAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
