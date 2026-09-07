using ButterKnife.Services;
using Microsoft.Data.Sqlite;

namespace ButterKnife.Data;

public sealed class SqliteConversationStore(SqliteDatabase db) : IConversationStore
{
    public async Task<Conversation> CreateAsync(string title, Guid connectionId, string model, Guid? personaId, CancellationToken cancellationToken = default)
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
        cmd.Parameters.AddWithValue("$backend", connectionId.ToString("D"));
        cmd.Parameters.AddWithValue("$model", model);
        cmd.Parameters.AddWithValue("$persona", (object?)personaId?.ToString("D") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", SqliteDatabase.Format(now));
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        return new Conversation(id, title, connectionId, model, personaId, null, null, null, null, now, now, []);
    }

    public async Task<Conversation?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.OpenAsync(cancellationToken);

        await using var head = connection.CreateCommand();
        head.CommandText = "SELECT title, backend, model, persona_id, summary, summary_through, context_tokens, context_window, created_at, updated_at FROM conversations WHERE id = $id;";
        head.Parameters.AddWithValue("$id", id.ToString("D"));

        await using var reader = await head.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var title = reader.GetString(0);
        // Rows written before connections existed hold a backend name here; they resolve to Guid.Empty (model unavailable).
        var connectionId = Guid.TryParse(reader.GetString(1), out var parsed) ? parsed : Guid.Empty;
        var model = reader.GetString(2);
        Guid? personaId = reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3));
        var summary = reader.IsDBNull(4) ? null : reader.GetString(4);
        int? summaryThrough = reader.IsDBNull(5) ? null : (int)reader.GetInt64(5);
        int? contextTokens = reader.IsDBNull(6) ? null : (int)reader.GetInt64(6);
        int? contextWindow = reader.IsDBNull(7) ? null : (int)reader.GetInt64(7);
        var createdAt = SqliteDatabase.Parse(reader.GetString(8));
        var updatedAt = SqliteDatabase.Parse(reader.GetString(9));

        var messages = await LoadMessagesAsync(connection, id, cancellationToken);
        return new Conversation(id, title, connectionId, model, personaId, summary, summaryThrough, contextTokens, contextWindow, createdAt, updatedAt, messages);
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

        long messageId;
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = (SqliteTransaction)tx;
            insert.CommandText = """
                INSERT INTO messages (conversation_id, role, content, created_at)
                VALUES ($cid, $role, $content, $now);
                SELECT last_insert_rowid();
                """;
            insert.Parameters.AddWithValue("$cid", conversationId.ToString("D"));
            insert.Parameters.AddWithValue("$role", message.Role.ToString());
            insert.Parameters.AddWithValue("$content", message.Content);
            insert.Parameters.AddWithValue("$now", now);
            messageId = (long)(await insert.ExecuteScalarAsync(cancellationToken))!;
        }

        foreach (var image in message.Images)
        {
            await using var insertImage = connection.CreateCommand();
            insertImage.Transaction = (SqliteTransaction)tx;
            insertImage.CommandText = "INSERT INTO message_images (message_id, media_type, data) VALUES ($mid, $type, $data);";
            insertImage.Parameters.AddWithValue("$mid", messageId);
            insertImage.Parameters.AddWithValue("$type", image.MediaType);
            insertImage.Parameters.AddWithValue("$data", image.Data);
            await insertImage.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }

    public async Task SetModelAsync(Guid conversationId, Guid connectionId, string model, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE conversations SET backend = $backend, model = $model WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", conversationId.ToString("D"));
        cmd.Parameters.AddWithValue("$backend", connectionId.ToString("D"));
        cmd.Parameters.AddWithValue("$model", model);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public const int MaxTitleLength = 120;

    public async Task SetTitleAsync(Guid conversationId, string title, CancellationToken cancellationToken = default)
    {
        var clean = (title ?? "").Trim();
        if (clean.Length == 0)
        {
            throw new ArgumentException("Title is required.", nameof(title));
        }
        if (clean.Length > MaxTitleLength)
        {
            clean = clean[..MaxTitleLength].TrimEnd();
        }

        await using var connection = await db.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE conversations SET title = $title WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", conversationId.ToString("D"));
        cmd.Parameters.AddWithValue("$title", clean);
        if (await cmd.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            throw new KeyNotFoundException($"Conversation {conversationId} does not exist.");
        }
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

    public async Task SetSummaryAsync(Guid conversationId, string? summary, int? summaryThrough, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE conversations SET summary = $summary, summary_through = $through WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", conversationId.ToString("D"));
        cmd.Parameters.AddWithValue("$summary", (object?)summary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$through", (object?)summaryThrough ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetContextUsageAsync(Guid conversationId, int? contextTokens, int? contextWindow, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE conversations SET context_tokens = $tokens, context_window = $window WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", conversationId.ToString("D"));
        cmd.Parameters.AddWithValue("$tokens", (object?)contextTokens ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$window", (object?)contextWindow ?? DBNull.Value);
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
        var images = new Dictionary<long, List<ChatImage>>();
        await using (var imageCmd = connection.CreateCommand())
        {
            imageCmd.CommandText = """
                SELECT i.message_id, i.media_type, i.data
                FROM message_images i
                JOIN messages m ON m.id = i.message_id
                WHERE m.conversation_id = $cid
                ORDER BY i.id;
                """;
            imageCmd.Parameters.AddWithValue("$cid", id.ToString("D"));
            await using var imageReader = await imageCmd.ExecuteReaderAsync(cancellationToken);
            while (await imageReader.ReadAsync(cancellationToken))
            {
                var messageId = imageReader.GetInt64(0);
                if (!images.TryGetValue(messageId, out var list))
                {
                    images[messageId] = list = [];
                }
                list.Add(new ChatImage(imageReader.GetString(1), (byte[])imageReader.GetValue(2)));
            }
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, role, content FROM messages WHERE conversation_id = $cid ORDER BY id;";
        cmd.Parameters.AddWithValue("$cid", id.ToString("D"));

        var messages = new List<ChatMessage>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var messageId = reader.GetInt64(0);
            messages.Add(new ChatMessage(
                Enum.Parse<ChatRole>(reader.GetString(1)),
                reader.GetString(2),
                images.TryGetValue(messageId, out var list) ? list : null));
        }
        return messages;
    }
}
