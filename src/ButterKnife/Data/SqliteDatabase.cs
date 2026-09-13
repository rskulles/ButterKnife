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
        DataSourcePath = string.Equals(builder.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase) ? null : builder.DataSource;
    }

    /// <summary>Absolute path of the database file, or null for an in-memory database.</summary>
    public string? DataSourcePath { get; }

    /// <summary>Writes a consistent snapshot of the database to <paramref name="path"/> (VACUUM INTO), replacing any file there.</summary>
    public async Task BackupToAsync(string path, CancellationToken cancellationToken)
    {
        File.Delete(path); // VACUUM INTO refuses to overwrite
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "VACUUM INTO $path;";
        cmd.Parameters.AddWithValue("$path", path);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Replaces the database file with the one at <paramref name="path"/> after checking that it is an SQLite file,
    /// passes SQLite's quick check and holds ButterKnife's tables. Pooled connections are dropped and the schema is
    /// re-checked on the next open, so a backup from an older version gets its missing columns added. An operation
    /// running in another circuit at that instant may fail once; callers should tell the user to reload.
    /// </summary>
    public async Task RestoreFromAsync(string path, CancellationToken cancellationToken)
    {
        var target = DataSourcePath ?? throw new InvalidOperationException("An in-memory database cannot be restored.");
        await ValidateBackupAsync(path, cancellationToken);

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            {
                File.Delete(target + suffix);
            }
            File.Copy(path, target);
            _initialized = false;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private static async Task ValidateBackupAsync(string path, CancellationToken cancellationToken)
    {
        var header = new byte[16];
        await using (var file = File.OpenRead(path))
        {
            if (await file.ReadAsync(header, cancellationToken) < header.Length
                || System.Text.Encoding.ASCII.GetString(header, 0, 15) != "SQLite format 3")
            {
                throw new InvalidDataException("That file is not an SQLite database.");
            }
        }

        var readOnly = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        await using var connection = new SqliteConnection(readOnly);
        await connection.OpenAsync(cancellationToken);

        await using (var check = connection.CreateCommand())
        {
            check.CommandText = "PRAGMA quick_check;";
            if (await check.ExecuteScalarAsync(cancellationToken) is not string result || result != "ok")
            {
                throw new InvalidDataException("That database is damaged and cannot be restored.");
            }
        }

        await using (var tables = connection.CreateCommand())
        {
            tables.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name IN ('conversations', 'messages', 'connections');";
            if (Convert.ToInt64(await tables.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 3)
            {
                throw new InvalidDataException("That database was not made by ButterKnife.");
            }
        }
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

                CREATE TABLE IF NOT EXISTS message_files (
                    id         INTEGER PRIMARY KEY AUTOINCREMENT,
                    message_id INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
                    name       TEXT NOT NULL,
                    media_type TEXT NOT NULL,
                    size       INTEGER NOT NULL,
                    text       TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_message_files_message ON message_files(message_id);

                CREATE TABLE IF NOT EXISTS settings (
                    key        TEXT PRIMARY KEY,
                    value      TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                """, cancellationToken);

            // Databases created before these columns existed.
            await AddColumnIfMissingAsync(connection, "conversations", "persona_id",
                "TEXT NULL REFERENCES personas(id) ON DELETE SET NULL", cancellationToken);
            await AddColumnIfMissingAsync(connection, "conversations", "summary", "TEXT NULL", cancellationToken);
            await AddColumnIfMissingAsync(connection, "conversations", "summary_through", "INTEGER NULL", cancellationToken);
            await AddColumnIfMissingAsync(connection, "conversations", "context_tokens", "INTEGER NULL", cancellationToken);
            await AddColumnIfMissingAsync(connection, "conversations", "context_window", "INTEGER NULL", cancellationToken);
            await AddColumnIfMissingAsync(connection, "connections", "context_window", "INTEGER NULL", cancellationToken);
            await AddColumnIfMissingAsync(connection, "messages", "reasoning", "TEXT NULL", cancellationToken);
            await AddColumnIfMissingAsync(connection, "conversations", "temperature", "REAL NULL", cancellationToken);
            await AddColumnIfMissingAsync(connection, "conversations", "max_tokens", "INTEGER NULL", cancellationToken);
            await AddColumnIfMissingAsync(connection, "conversations", "think", "INTEGER NULL", cancellationToken);
            await AddColumnIfMissingAsync(connection, "conversations", "instructions", "TEXT NULL", cancellationToken);
            await AddColumnIfMissingAsync(connection, "messages", "model", "TEXT NULL", cancellationToken);
            await AddColumnIfMissingAsync(connection, "messages", "stats", "TEXT NULL", cancellationToken);
            await AddColumnIfMissingAsync(connection, "conversations", "pinned", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
            await AddColumnIfMissingAsync(connection, "conversations", "archived", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
            await EnsureSearchIndexAsync(connection, cancellationToken);

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

    /// <summary>
    /// Full-text search over message content: an external-content FTS5 table kept in step by triggers, so the text
    /// is stored once. Created after the messages table; a database that predates it gets a one-off rebuild.
    /// </summary>
    private static async Task EnsureSearchIndexAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        bool existed;
        await using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'messages_fts';";
            existed = Convert.ToInt64(await check.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) > 0;
        }

        await ExecAsync(connection, """
            CREATE VIRTUAL TABLE IF NOT EXISTS messages_fts USING fts5(
                content,
                content = 'messages',
                content_rowid = 'id',
                tokenize = 'unicode61 remove_diacritics 2'
            );

            CREATE TRIGGER IF NOT EXISTS messages_fts_ai AFTER INSERT ON messages BEGIN
                INSERT INTO messages_fts(rowid, content) VALUES (new.id, new.content);
            END;

            CREATE TRIGGER IF NOT EXISTS messages_fts_ad AFTER DELETE ON messages BEGIN
                INSERT INTO messages_fts(messages_fts, rowid, content) VALUES ('delete', old.id, old.content);
            END;

            CREATE TRIGGER IF NOT EXISTS messages_fts_au AFTER UPDATE OF content ON messages BEGIN
                INSERT INTO messages_fts(messages_fts, rowid, content) VALUES ('delete', old.id, old.content);
                INSERT INTO messages_fts(rowid, content) VALUES (new.id, new.content);
            END;
            """, cancellationToken);

        if (!existed)
        {
            await ExecAsync(connection, "INSERT INTO messages_fts(messages_fts) VALUES ('rebuild');", cancellationToken);
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
