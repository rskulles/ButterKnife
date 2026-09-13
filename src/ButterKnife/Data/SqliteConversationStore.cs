using System.Text.Json;
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
        head.CommandText = "SELECT title, backend, model, persona_id, summary, summary_through, context_tokens, context_window, created_at, updated_at, temperature, max_tokens, think, instructions FROM conversations WHERE id = $id;";
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
        var options = new ChatOptions(
            reader.IsDBNull(10) ? null : reader.GetDouble(10),
            reader.IsDBNull(11) ? null : (int)reader.GetInt64(11),
            reader.IsDBNull(12) ? null : reader.GetInt64(12) != 0);

        var instructions = reader.IsDBNull(13) ? null : reader.GetString(13);

        var messages = await LoadMessagesAsync(connection, id, cancellationToken);
        return new Conversation(id, title, connectionId, model, personaId, summary, summaryThrough, contextTokens, contextWindow, createdAt, updatedAt, messages)
        {
            Options = options,
            Instructions = instructions,
        };
    }

    public async Task<IReadOnlyList<ConversationSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await db.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, title, updated_at, pinned, archived FROM conversations ORDER BY pinned DESC, updated_at DESC;";

        var list = new List<ConversationSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new ConversationSummary(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                SqliteDatabase.Parse(reader.GetString(2)),
                Pinned: reader.GetInt64(3) != 0,
                Archived: reader.GetInt64(4) != 0));
        }
        return list;
    }

    public Task SetPinnedAsync(Guid conversationId, bool pinned, CancellationToken cancellationToken = default) =>
        SetFlagAsync(conversationId, "pinned", pinned, cancellationToken);

    public Task SetArchivedAsync(Guid conversationId, bool archived, CancellationToken cancellationToken = default) =>
        SetFlagAsync(conversationId, "archived", archived, cancellationToken);

    /// <summary>Flags do not count as activity: updated_at is left alone so the order is not disturbed.</summary>
    private async Task SetFlagAsync(Guid conversationId, string column, bool value, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"UPDATE conversations SET {column} = $value WHERE id = $id;"; // column is one of two literals above
        cmd.Parameters.AddWithValue("$value", value ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", conversationId.ToString("D"));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<long> AppendMessageAsync(Guid conversationId, ChatMessage message, CancellationToken cancellationToken = default)
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
                INSERT INTO messages (conversation_id, role, content, reasoning, created_at, model, stats)
                VALUES ($cid, $role, $content, $reasoning, $now, $model, $stats);
                SELECT last_insert_rowid();
                """;
            insert.Parameters.AddWithValue("$cid", conversationId.ToString("D"));
            insert.Parameters.AddWithValue("$role", message.Role.ToString());
            insert.Parameters.AddWithValue("$content", message.Content);
            insert.Parameters.AddWithValue("$reasoning", (object?)message.Reasoning ?? DBNull.Value);
            insert.Parameters.AddWithValue("$model", (object?)message.Model ?? DBNull.Value);
            insert.Parameters.AddWithValue("$stats", (object?)SerializeStats(message.Stats) ?? DBNull.Value);
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

        foreach (var file in message.Files)
        {
            await using var insertFile = connection.CreateCommand();
            insertFile.Transaction = (SqliteTransaction)tx;
            insertFile.CommandText = "INSERT INTO message_files (message_id, name, media_type, size, text) VALUES ($mid, $name, $type, $size, $text);";
            insertFile.Parameters.AddWithValue("$mid", messageId);
            insertFile.Parameters.AddWithValue("$name", file.Name);
            insertFile.Parameters.AddWithValue("$type", file.MediaType);
            insertFile.Parameters.AddWithValue("$size", file.Size);
            insertFile.Parameters.AddWithValue("$text", file.Text);
            await insertFile.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return messageId;
    }

    public async Task SetMessageContentAsync(Guid conversationId, long messageId, string content, string? reasoning, string? model = null, GenerationStats? stats = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE messages SET content = $content, reasoning = $reasoning, model = COALESCE($model, model), stats = COALESCE($stats, stats)
            WHERE conversation_id = $cid AND id = $mid;
            UPDATE conversations SET updated_at = $now WHERE id = $cid;
            """;
        cmd.Parameters.AddWithValue("$cid", conversationId.ToString("D"));
        cmd.Parameters.AddWithValue("$mid", messageId);
        cmd.Parameters.AddWithValue("$content", content);
        cmd.Parameters.AddWithValue("$reasoning", (object?)reasoning ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$model", (object?)model ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$stats", (object?)SerializeStats(stats) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", SqliteDatabase.Format(DateTimeOffset.UtcNow));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Stats are a small JSON blob: they are only ever shown, never queried.</summary>
    private static string? SerializeStats(GenerationStats? stats) => stats is null ? null : JsonSerializer.Serialize(stats, StatsJson);

    private static GenerationStats? DeserializeStats(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<GenerationStats>(json, StatsJson);
        }
        catch (JsonException)
        {
            return null; // a blob from a future version we cannot read; the reply is still fine
        }
    }

    private static readonly JsonSerializerOptions StatsJson = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public async Task DeleteMessageAsync(Guid conversationId, long messageId, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM messages WHERE conversation_id = $cid AND id = $mid;
            UPDATE conversations SET updated_at = $now, context_tokens = NULL WHERE id = $cid;
            """;
        cmd.Parameters.AddWithValue("$cid", conversationId.ToString("D"));
        cmd.Parameters.AddWithValue("$mid", messageId);
        cmd.Parameters.AddWithValue("$now", SqliteDatabase.Format(DateTimeOffset.UtcNow));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        await InvalidateSummaryIfShortAsync(connection, conversationId, cancellationToken);
    }

    public async Task DeleteMessagesFromAsync(Guid conversationId, long messageId, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM messages WHERE conversation_id = $cid AND id >= $mid;
            UPDATE conversations SET updated_at = $now, context_tokens = NULL WHERE id = $cid;
            """;
        cmd.Parameters.AddWithValue("$cid", conversationId.ToString("D"));
        cmd.Parameters.AddWithValue("$mid", messageId);
        cmd.Parameters.AddWithValue("$now", SqliteDatabase.Format(DateTimeOffset.UtcNow));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        await InvalidateSummaryIfShortAsync(connection, conversationId, cancellationToken);
    }

    /// <summary>A summary claims to cover the first N messages; if fewer than N remain it no longer describes the transcript.</summary>
    private static async Task InvalidateSummaryIfShortAsync(SqliteConnection connection, Guid conversationId, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE conversations SET summary = NULL, summary_through = NULL
            WHERE id = $cid AND summary_through IS NOT NULL
              AND summary_through > (SELECT COUNT(*) FROM messages WHERE conversation_id = $cid);
            """;
        cmd.Parameters.AddWithValue("$cid", conversationId.ToString("D"));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
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

    public async Task SetInstructionsAsync(Guid conversationId, string? instructions, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE conversations SET instructions = $instructions WHERE id = $id;";
        cmd.Parameters.AddWithValue("$instructions", string.IsNullOrWhiteSpace(instructions) ? DBNull.Value : instructions.Trim());
        cmd.Parameters.AddWithValue("$id", conversationId.ToString("D"));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetOptionsAsync(Guid conversationId, ChatOptions options, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE conversations SET temperature = $temperature, max_tokens = $maxTokens, think = $think WHERE id = $id;";
        cmd.Parameters.AddWithValue("$temperature", (object?)ChatOptions.NormalizeTemperature(options.Temperature) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$maxTokens", (object?)ChatOptions.NormalizeMaxTokens(options.MaxTokens) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$think", options.Think is { } think ? (think ? 1 : 0) : DBNull.Value);
        cmd.Parameters.AddWithValue("$id", conversationId.ToString("D"));
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

    public async Task<Conversation> BranchAsync(Guid conversationId, long throughMessageId, string title, CancellationToken cancellationToken = default)
    {
        var source = await GetAsync(conversationId, cancellationToken)
            ?? throw new KeyNotFoundException($"Conversation {conversationId} does not exist.");
        var kept = source.Messages.TakeWhile(m => m.Id <= throughMessageId).ToList();
        if (kept.Count == 0 || kept[^1].Id != throughMessageId)
        {
            throw new KeyNotFoundException($"Message {throughMessageId} is not in conversation {conversationId}.");
        }

        var branch = await CreateAsync(title, source.ConnectionId, source.Model, source.PersonaId, cancellationToken);
        if (!source.Options.IsDefault)
        {
            await SetOptionsAsync(branch.Id, source.Options, cancellationToken);
        }
        if (source.Instructions is not null)
        {
            await SetInstructionsAsync(branch.Id, source.Instructions, cancellationToken);
        }
        foreach (var message in kept)
        {
            await AppendMessageAsync(branch.Id, message, cancellationToken);
        }
        if (source.Summary is not null && source.SummaryThrough is { } through && through <= kept.Count)
        {
            await SetSummaryAsync(branch.Id, source.Summary, through, cancellationToken);
        }
        return (await GetAsync(branch.Id, cancellationToken))!;
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

        var files = new Dictionary<long, List<ChatFile>>();
        await using (var fileCmd = connection.CreateCommand())
        {
            fileCmd.CommandText = """
                SELECT f.message_id, f.name, f.media_type, f.size, f.text
                FROM message_files f
                JOIN messages m ON m.id = f.message_id
                WHERE m.conversation_id = $cid
                ORDER BY f.id;
                """;
            fileCmd.Parameters.AddWithValue("$cid", id.ToString("D"));
            await using var fileReader = await fileCmd.ExecuteReaderAsync(cancellationToken);
            while (await fileReader.ReadAsync(cancellationToken))
            {
                var messageId = fileReader.GetInt64(0);
                if (!files.TryGetValue(messageId, out var list))
                {
                    files[messageId] = list = [];
                }
                list.Add(new ChatFile(fileReader.GetString(1), fileReader.GetString(2), (int)fileReader.GetInt64(3), fileReader.GetString(4)));
            }
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, role, content, created_at, reasoning, model, stats FROM messages WHERE conversation_id = $cid ORDER BY id;";
        cmd.Parameters.AddWithValue("$cid", id.ToString("D"));

        var messages = new List<ChatMessage>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var messageId = reader.GetInt64(0);
            messages.Add(new ChatMessage(
                Enum.Parse<ChatRole>(reader.GetString(1)),
                reader.GetString(2),
                images.TryGetValue(messageId, out var list) ? list : null)
            {
                Id = messageId,
                CreatedAt = SqliteDatabase.Parse(reader.GetString(3)),
                Reasoning = reader.IsDBNull(4) ? null : reader.GetString(4),
                Model = reader.IsDBNull(5) ? null : reader.GetString(5),
                Stats = reader.IsDBNull(6) ? null : DeserializeStats(reader.GetString(6)),
                Files = files.TryGetValue(messageId, out var attached) ? attached : ChatMessage.NoFiles,
            });
        }
        return messages;
    }
}
