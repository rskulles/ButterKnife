using Microsoft.Data.Sqlite;

namespace ButterKnife.Data;

public sealed class SqlitePersonaStore(SqliteDatabase db) : IPersonaStore
{
    private const string Columns = "id, name, description, system_prompt, is_builtin, created_at, updated_at";

    public async Task<IReadOnlyList<Persona>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await db.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM personas ORDER BY is_builtin DESC, sort_order, name COLLATE NOCASE;";

        var list = new List<Persona>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(Read(reader));
        }
        return list;
    }

    public async Task<Persona?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM personas WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<Persona> CreateAsync(string name, string description, string systemPrompt, CancellationToken cancellationToken = default)
    {
        Validate(name, systemPrompt);
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using var connection = await db.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO personas (id, name, description, system_prompt, is_builtin, sort_order, created_at, updated_at)
            VALUES ($id, $name, $description, $prompt, 0, 0, $now, $now);
            """;
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        cmd.Parameters.AddWithValue("$name", name.Trim());
        cmd.Parameters.AddWithValue("$description", description.Trim());
        cmd.Parameters.AddWithValue("$prompt", systemPrompt.Trim());
        cmd.Parameters.AddWithValue("$now", SqliteDatabase.Format(now));
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        return new Persona(id, name.Trim(), description.Trim(), systemPrompt.Trim(), IsBuiltIn: false, now, now);
    }

    public async Task UpdateAsync(Guid id, string name, string description, string systemPrompt, CancellationToken cancellationToken = default)
    {
        Validate(name, systemPrompt);

        await using var connection = await db.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE personas SET name = $name, description = $description, system_prompt = $prompt, updated_at = $now
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        cmd.Parameters.AddWithValue("$name", name.Trim());
        cmd.Parameters.AddWithValue("$description", description.Trim());
        cmd.Parameters.AddWithValue("$prompt", systemPrompt.Trim());
        cmd.Parameters.AddWithValue("$now", SqliteDatabase.Format(DateTimeOffset.UtcNow));

        if (await cmd.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            throw new KeyNotFoundException($"Persona {id} does not exist.");
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.OpenAsync(cancellationToken);

        await using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT is_builtin FROM personas WHERE id = $id;";
            check.Parameters.AddWithValue("$id", id.ToString("D"));
            var result = await check.ExecuteScalarAsync(cancellationToken);
            if (result is null)
            {
                return;
            }
            if ((long)result != 0)
            {
                throw new InvalidOperationException("Built-in personas cannot be deleted.");
            }
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM personas WHERE id = $id;"; // conversations.persona_id -> NULL
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void Validate(string name, string systemPrompt)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Persona name is required.", nameof(name));
        }
        if (string.IsNullOrWhiteSpace(systemPrompt))
        {
            throw new ArgumentException("Persona system prompt is required.", nameof(systemPrompt));
        }
    }

    private static Persona Read(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetInt64(4) != 0,
        SqliteDatabase.Parse(reader.GetString(5)),
        SqliteDatabase.Parse(reader.GetString(6)));
}
