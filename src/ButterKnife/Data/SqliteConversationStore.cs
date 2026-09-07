using System.Globalization;
using ButterKnife.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace ButterKnife.Data;

/// <summary>
/// SQLite-backed store using plain ADO.NET. Schema is created on first use.
/// Timestamps are stored as ISO-8601 UTC strings so ORDER BY works lexically.
/// </summary>
public sealed class SqliteConversationStore : IConversationStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public SqliteConversationStore(IOptions<ConversationStoreOptions> options, IHostEnvironment? environment = null)
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

    public async Task<Conversation> CreateAsync(string title, string backend, string model, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO conversations (id, title, backend, model, created_at, updated_at)
            VALUES ($id, $title, $backend, $model, $now, $now);
            """;
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$backend", backend);
        cmd.Parameters.AddWithValue("$model", model);
        cmd.Parameters.AddWithValue("$now", Format(now));
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        return new Conversation(id, title, backend, model, now, now, []);
    }

    public async Task<Conversation?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);

        await using (var head = connection.CreateCommand())
        {
            head.CommandText = "SELECT title, backend, model, created_at, updated_at FROM conversations WHERE id = $id;";
            head.Parameters.AddWithValue("$id", id.ToString("D"));

            await using var reader = await head.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            var title = reader.GetString(0);
            var backend = reader.GetString(1);
            var model = reader.GetString(2);
            var createdAt = Parse(reader.GetString(3));
            var updatedAt = Parse(reader.GetString(4));

            var messages = await LoadMessagesAsync(connection, id, cancellationToken);
            return new Conversation(id, title, backend, model, createdAt, updatedAt, messages);
        }
    }

    public async Task<IReadOnlyList<ConversationSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, title, updated_at FROM conversations ORDER BY updated_at DESC;";

        var list = new List<ConversationSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new ConversationSummary(Guid.Parse(reader.GetString(0)), reader.GetString(1), Parse(reader.GetString(2))));
        }
        return list;
    }

    public async Task AppendMessageAsync(Guid conversationId, ChatMessage message, CancellationToken cancellationToken = default)
    {
        var now = Format(DateTimeOffset.UtcNow);

        await using var connection = await OpenAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        // Touch the parent first: zero rows means the conversation is gone, which we report as
        // KeyNotFound rather than letting the FK constraint surface as a raw SqliteException.
        await using (var touch = connection.CreateCommand())
        {
            touch.Transaction = (SqliteTransaction)tx;
            touch.CommandText = "UPDATE conversations SET updated_at = $now WHERE id = $cid;";
            touch.Parameters.AddWithValue("$cid", conversationId.ToString("D"));
            touch.Parameters.AddWithValue("$now", now);
            var rows = await touch.ExecuteNonQueryAsync(cancellationToken);
            if (rows == 0)
            {
                throw new KeyNotFoundException($"Conversation {conversationId} does not exist.");
            }
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = (SqliteTransaction)tx;
            insert.CommandText = """
                INSERT INTO messages (conversation_id, role, content, created_at)
                VALUES ($cid, $role, $content, $now);
                """;
            insert.Parameters.AddWithValue("$cid", conversationId.ToString("D"));
            insert.Parameters.AddWithValue("$role", message.Role.ToString());
            insert.Parameters.AddWithValue("$content", message.Content);
            insert.Parameters.AddWithValue("$now", now);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }

    public async Task SetModelAsync(Guid conversationId, string backend, string model, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE conversations SET backend = $backend, model = $model WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", conversationId.ToString("D"));
        cmd.Parameters.AddWithValue("$backend", backend);
        cmd.Parameters.AddWithValue("$model", model);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM conversations WHERE id = $id;"; // messages cascade
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<ChatMessage>> LoadMessagesAsync(SqliteConnection connection, Guid id, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT role, content FROM messages WHERE conversation_id = $cid ORDER BY id;";
        cmd.Parameters.AddWithValue("$cid", id.ToString("D"));

        var messages = new List<ChatMessage>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            messages.Add(new ChatMessage(Enum.Parse<ChatRole>(reader.GetString(0)), reader.GetString(1)));
        }
        return messages;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        // Per-connection pragma; cascade deletes depend on it.
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            await pragma.ExecuteNonQueryAsync(cancellationToken);
        }

        await EnsureSchemaAsync(connection, cancellationToken);
        return connection;
    }

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

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                PRAGMA journal_mode = WAL;

                CREATE TABLE IF NOT EXISTS conversations (
                    id         TEXT PRIMARY KEY,
                    title      TEXT NOT NULL,
                    backend    TEXT NOT NULL,
                    model      TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS messages (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    conversation_id TEXT NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
                    role            TEXT NOT NULL,
                    content         TEXT NOT NULL,
                    created_at      TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_messages_conversation ON messages(conversation_id, id);
                """;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
