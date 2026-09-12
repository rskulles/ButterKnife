namespace ButterKnife.Data;

public sealed class SqliteSettingsStore(SqliteDatabase db) : ISettingsStore
{
    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $key;";
        cmd.Parameters.AddWithValue("$key", key);
        return await cmd.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async Task SetAsync(string key, string? value, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        if (string.IsNullOrWhiteSpace(value))
        {
            cmd.CommandText = "DELETE FROM settings WHERE key = $key;";
            cmd.Parameters.AddWithValue("$key", key);
        }
        else
        {
            cmd.CommandText = """
                INSERT INTO settings (key, value, updated_at) VALUES ($key, $value, $now)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at;
                """;
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$value", value.Trim());
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        }
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
