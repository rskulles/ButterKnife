using ButterKnife.Services;
using Microsoft.Data.Sqlite;

namespace ButterKnife.Data;

public sealed class SqliteConversationStore(SqliteDatabase db) : IConversationStore
{
    public async Task<Conversation> CreateAsync(string title, string backend, string model, Guid? personaId, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using var connection = await db.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO conversations (id, title, backend, model, persona_id, created_at, updated_at)
            VALUES ($id, $title, $backend, $model, $persona, $now, $now);
            """;
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$backend", backend);
        cmd.Parameters.AddWithValue("$model", model);
        cmd.Parameters.AddWithValue("$persona", (object?)personaId?.ToString("D") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", SqliteDatabase.Format(now));
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        return new Conversation(id, title, backend, model, personaId, now, now, []);
    }

    public async Task<Conversation?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.OpenAsync(cancellationToken);

        await using var head = connection.CreateCommand();
        head.CommandText = "SELECT title, backend, model, persona_id, created_at, updated_at FROM conversations WHERE id = $id;";
        head.Parameters.AddWithValue("$id", id.ToString("D"));

        await using var reader = await head.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var title = reader.GetString(0);
        var backend = reader.GetString(1);
        var model = reader.GetString(2);
        Guid? personaId = reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3));
        var createdAt = SqliteDatabase.Parse(reader.GetString(4));
        var updatedAt = SqliteDatabase.Parse(reader.GetString(5));

        var messages = await LoadMessagesAsync(connection, id, cancellationToken);
        return new Conversation(id, title, backend, model, personaId, createdAt, updatedAt, messages);
    }

    public async Task<IReadOnlyList<ConversationSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await db.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, title, updated_at FROM conversations ORDER BY updated_at DESC;";

        var list = new List<ConversationSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new ConversationSummary(Guid.Parse(reader.GetString(0)), reader.GetString(1), SqliteDatabase.Parse(reader.GetString(2))));
        }
        return list;
    }

    public async Task AppendMessageAsync(Guid conversationId, ChatMessage message, CancellationToken cancellationToken = default)
    {
        var now = SqliteDatabase.Format(DateTimeOffset.UtcNow);

        await using var connection = await db.OpenAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        // Touch the parent first: zero rows means the conversation is gone, which we report as
        // KeyNotFound rather than letting the FK constraint surface as a raw SqliteException.
        await using (var touch = connection.CreateCommand())
        {
            touch.Transaction = (SqliteTransaction)tx;
            touch.CommandText = "UPDATE conversations SET updated_at = $now WHERE id = $cid;";
            touch.Parameters.AddWithValue("$cid", conversationId.ToString("D"));
            touch.Parameters.AddWithValue("$now", now);
            if (await touch.ExecuteNonQueryAsync(cancellationToken) == 0)
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
        await using var connection = await db.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE conversations SET backend = $backend, model = $model WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", conversationId.ToString("D"));
        cmd.Parameters.AddWithValue("$backend", backend);
        cmd.Parameters.AddWithValue("$model", model);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetPersonaAsync(Guid conversationId, Guid? personaId, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE conversations SET persona_id = $persona WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", conversationId.ToString("D"));
        cmd.Parameters.AddWithValue("$persona", (object?)personaId?.ToString("D") ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.OpenAsync(cancellationToken);
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
}
