using System.Text.Json;
using ButterKnife.Services;
using Microsoft.Data.Sqlite;

namespace ButterKnife.Data;

public sealed class SqliteCodeSessionStore(SqliteDatabase db) : ICodeSessionStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public async Task<CodeSession> CreateAsync(Guid userId, string title, string workspace, Guid connectionId, string model, CodePermissionMode mode, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using var connection = await db.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO code_sessions (id, user_id, title, workspace, backend, model, permission_mode, created_at, updated_at)
            VALUES ($id, $user, $title, $workspace, $backend, $model, $mode, $now, $now);
            """;
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        cmd.Parameters.AddWithValue("$user", userId.ToString("D"));
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$workspace", workspace);
        cmd.Parameters.AddWithValue("$backend", connectionId.ToString("D"));
        cmd.Parameters.AddWithValue("$model", model);
        cmd.Parameters.AddWithValue("$mode", mode.ToString());
        cmd.Parameters.AddWithValue("$now", SqliteDatabase.Format(now));
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        return new CodeSession(id, userId, title, workspace, connectionId, model, mode, null, null, null, 0, now, now, []);
    }

    public async Task<CodeSession?> GetAsync(Guid id, Guid userId, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.OpenAsync(cancellationToken);

        CodeSession session;
        await using (var head = connection.CreateCommand())
        {
            head.CommandText = """
                SELECT title, workspace, backend, model, permission_mode, context_tokens, context_window, created_at, updated_at, summary, summary_through
                FROM code_sessions WHERE id = $id AND user_id = $user;
                """;
            head.Parameters.AddWithValue("$id", id.ToString("D"));
            head.Parameters.AddWithValue("$user", userId.ToString("D"));
            await using var reader = await head.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }
            session = new CodeSession(
                id,
                userId,
                reader.GetString(0),
                reader.GetString(1),
                Guid.TryParse(reader.GetString(2), out var connectionId) ? connectionId : Guid.Empty,
                reader.GetString(3),
                Enum.TryParse<CodePermissionMode>(reader.GetString(4), out var mode) ? mode : CodePermissionMode.Ask,
                reader.IsDBNull(5) ? null : (int)reader.GetInt64(5),
                reader.IsDBNull(6) ? null : (int)reader.GetInt64(6),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? 0 : (int)reader.GetInt64(10),
                SqliteDatabase.Parse(reader.GetString(7)),
                SqliteDatabase.Parse(reader.GetString(8)),
                []);
        }

        var entries = new List<CodeEntry>();
        await using (var rows = connection.CreateCommand())
        {
            rows.CommandText = """
                SELECT id, role, content, reasoning, tool_calls, tool_call_id, tool_name, is_error, diff, model, stats, created_at, display
                FROM code_messages WHERE session_id = $id ORDER BY id;
                """;
            rows.Parameters.AddWithValue("$id", id.ToString("D"));
            await using var reader = await rows.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var createdAt = SqliteDatabase.Parse(reader.GetString(11));
                var message = new ChatMessage(Enum.Parse<ChatRole>(reader.GetString(1)), reader.GetString(2))
                {
                    Id = reader.GetInt64(0),
                    CreatedAt = createdAt,
                    Reasoning = reader.IsDBNull(3) ? null : reader.GetString(3),
                    ToolCalls = reader.IsDBNull(4) ? ChatMessage.NoToolCalls : Deserialize<ToolCall[]>(reader.GetString(4)) ?? [],
                    ToolCallId = reader.IsDBNull(5) ? null : reader.GetString(5),
                    ToolName = reader.IsDBNull(6) ? null : reader.GetString(6),
                    IsError = reader.GetInt64(7) != 0,
                    Model = reader.IsDBNull(9) ? null : reader.GetString(9),
                    Stats = reader.IsDBNull(10) ? null : Deserialize<GenerationStats>(reader.GetString(10)),
                };
                entries.Add(new CodeEntry(message.Id!.Value, message, reader.IsDBNull(8) ? null : reader.GetString(8), createdAt) { Display = reader.IsDBNull(12) ? null : reader.GetString(12) });
            }
        }

        return session with { Entries = entries };
    }

    public async Task<IReadOnlyList<CodeSessionSummary>> ListAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, title, workspace, updated_at FROM code_sessions WHERE user_id = $user ORDER BY updated_at DESC;";
        cmd.Parameters.AddWithValue("$user", userId.ToString("D"));

        var result = new List<CodeSessionSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new CodeSessionSummary(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), SqliteDatabase.Parse(reader.GetString(3))));
        }
        return result;
    }

    public async Task<IReadOnlyList<string>> RecentWorkspacesAsync(Guid userId, int limit = 8, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT workspace FROM code_sessions WHERE user_id = $user
            GROUP BY workspace ORDER BY max(updated_at) DESC LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$user", userId.ToString("D"));
        cmd.Parameters.AddWithValue("$limit", limit);

        var result = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetString(0));
        }
        return result;
    }

    public async Task<long> AppendAsync(Guid sessionId, ChatMessage message, string? diff = null, string? display = null, CancellationToken cancellationToken = default)
    {
        var now = SqliteDatabase.Format(DateTimeOffset.UtcNow);
        await using var connection = await db.OpenAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        long id;
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = (SqliteTransaction)tx;
            insert.CommandText = """
                INSERT INTO code_messages (session_id, role, content, reasoning, tool_calls, tool_call_id, tool_name, is_error, diff, model, stats, created_at, display)
                VALUES ($session, $role, $content, $reasoning, $calls, $callId, $toolName, $isError, $diff, $model, $stats, $now, $display);
                SELECT last_insert_rowid();
                """;
            insert.Parameters.AddWithValue("$session", sessionId.ToString("D"));
            insert.Parameters.AddWithValue("$role", message.Role.ToString());
            insert.Parameters.AddWithValue("$content", message.Content);
            insert.Parameters.AddWithValue("$reasoning", (object?)message.Reasoning ?? DBNull.Value);
            insert.Parameters.AddWithValue("$calls", message.HasToolCalls ? JsonSerializer.Serialize(message.ToolCalls, Json) : DBNull.Value);
            insert.Parameters.AddWithValue("$callId", (object?)message.ToolCallId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$toolName", (object?)message.ToolName ?? DBNull.Value);
            insert.Parameters.AddWithValue("$isError", message.IsError ? 1 : 0);
            insert.Parameters.AddWithValue("$diff", (object?)diff ?? DBNull.Value);
            insert.Parameters.AddWithValue("$display", (object?)display ?? DBNull.Value);
            insert.Parameters.AddWithValue("$model", (object?)message.Model ?? DBNull.Value);
            insert.Parameters.AddWithValue("$stats", message.Stats is null ? DBNull.Value : JsonSerializer.Serialize(message.Stats, Json));
            insert.Parameters.AddWithValue("$now", now);
            id = (long)(await insert.ExecuteScalarAsync(cancellationToken))!;
        }

        await using (var touch = connection.CreateCommand())
        {
            touch.Transaction = (SqliteTransaction)tx;
            touch.CommandText = "UPDATE code_sessions SET updated_at = $now WHERE id = $id;";
            touch.Parameters.AddWithValue("$now", now);
            touch.Parameters.AddWithValue("$id", sessionId.ToString("D"));
            await touch.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return id;
    }

    public Task SetTitleAsync(Guid id, string title, CancellationToken cancellationToken = default) =>
        UpdateAsync(id, "title = $value", title.Trim(), cancellationToken);

    public async Task SetModelAsync(Guid id, Guid connectionId, string model, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE code_sessions SET backend = $backend, model = $model WHERE id = $id;";
        cmd.Parameters.AddWithValue("$backend", connectionId.ToString("D"));
        cmd.Parameters.AddWithValue("$model", model);
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task SetModeAsync(Guid id, CodePermissionMode mode, CancellationToken cancellationToken = default) =>
        UpdateAsync(id, "permission_mode = $value", mode.ToString(), cancellationToken);

    public async Task SetContextAsync(Guid id, int? tokens, int? window, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE code_sessions SET context_tokens = $tokens, context_window = $window WHERE id = $id;";
        cmd.Parameters.AddWithValue("$tokens", (object?)tokens ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$window", (object?)window ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetSummaryAsync(Guid id, string? summary, int? through, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE code_sessions SET summary = $summary, summary_through = $through WHERE id = $id;";
        cmd.Parameters.AddWithValue("$summary", (object?)summary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$through", summary is null ? DBNull.Value : (object?)through ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM code_sessions WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task UpdateAsync(Guid id, string assignment, string value, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"UPDATE code_sessions SET {assignment} WHERE id = $id;";
        cmd.Parameters.AddWithValue("$value", value);
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static T? Deserialize<T>(string json) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
