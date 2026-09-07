using ButterKnife.Data;
using ButterKnife.Services;
using Microsoft.Data.Sqlite;

namespace ButterKnife.Tests;

public sealed class SqliteConversationStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "butterknife-tests", Guid.NewGuid().ToString("N"));
    private readonly SqliteDatabase _db;
    private readonly SqliteConversationStore _store;

    public SqliteConversationStoreTests()
    {
        _db = new SqliteDatabase(
            Microsoft.Extensions.Options.Options.Create(new DatabaseOptions
            {
                ConnectionString = $"Data Source={Path.Combine(_dir, "nested", "test.db")}",
            }));
        _store = new SqliteConversationStore(_db);
    }

    [Fact]
    public async Task CreatesDirectoryAndRoundTripsConversation()
    {
        var created = await _store.CreateAsync("Hello there", "Ollama", "llama3", null, CancellationToken.None);
        await _store.AppendMessageAsync(created.Id, new ChatMessage(ChatRole.User, "hi"), CancellationToken.None);
        await _store.AppendMessageAsync(created.Id, new ChatMessage(ChatRole.Assistant, "hello **you**"), CancellationToken.None);

        var loaded = await _store.GetAsync(created.Id, CancellationToken.None);

        Assert.True(Directory.Exists(Path.Combine(_dir, "nested")));
        Assert.NotNull(loaded);
        Assert.Equal("Hello there", loaded.Title);
        Assert.Equal("Ollama", loaded.Backend);
        Assert.Equal("llama3", loaded.Model);
        Assert.Equal(
            [new ChatMessage(ChatRole.User, "hi"), new ChatMessage(ChatRole.Assistant, "hello **you**")],
            loaded.Messages);
        Assert.True(loaded.UpdatedAt >= loaded.CreatedAt);
    }

    [Fact]
    public async Task ListsMostRecentlyUpdatedFirst()
    {
        var older = await _store.CreateAsync("older", "b", "m", null, CancellationToken.None);
        await Task.Delay(5, CancellationToken.None);
        var newer = await _store.CreateAsync("newer", "b", "m", null, CancellationToken.None);
        await Task.Delay(5, CancellationToken.None);
        await _store.AppendMessageAsync(older.Id, new ChatMessage(ChatRole.User, "bump"), CancellationToken.None);

        var list = await _store.ListAsync(CancellationToken.None);

        Assert.Equal([older.Id, newer.Id], list.Select(c => c.Id));
        Assert.Equal("older", list[0].Title);
    }

    [Fact]
    public async Task DeleteCascadesMessages()
    {
        var conv = await _store.CreateAsync("t", "b", "m", null, CancellationToken.None);
        await _store.AppendMessageAsync(conv.Id, new ChatMessage(ChatRole.User, "x"), CancellationToken.None);

        await _store.DeleteAsync(conv.Id, CancellationToken.None);

        Assert.Null(await _store.GetAsync(conv.Id, CancellationToken.None));
        Assert.Empty(await _store.ListAsync(CancellationToken.None));

        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_dir, "nested", "test.db")}");
        await connection.OpenAsync(CancellationToken.None);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM messages;";
        Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync(CancellationToken.None))!);
    }

    [Fact]
    public async Task SetModelUpdatesBackendAndModel()
    {
        var conv = await _store.CreateAsync("t", "Ollama", "a", null, CancellationToken.None);

        await _store.SetModelAsync(conv.Id, "LM Studio", "b", CancellationToken.None);

        var loaded = await _store.GetAsync(conv.Id, CancellationToken.None);
        Assert.Equal(("LM Studio", "b"), (loaded!.Backend, loaded.Model));
    }

    [Fact]
    public async Task AppendToMissingConversationThrows()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _store.AppendMessageAsync(Guid.NewGuid(), new ChatMessage(ChatRole.User, "x"), CancellationToken.None));
    }

    [Fact]
    public async Task RoundTripsAndUpdatesPersona()
    {
        var personas = new SqlitePersonaStore(_db);
        var programmer = (await personas.ListAsync(CancellationToken.None)).Single(p => p.Name == "Programmer");

        var conv = await _store.CreateAsync("t", "b", "m", programmer.Id, CancellationToken.None);
        Assert.Equal(programmer.Id, (await _store.GetAsync(conv.Id, CancellationToken.None))!.PersonaId);

        await _store.SetPersonaAsync(conv.Id, null, CancellationToken.None);

        Assert.Null((await _store.GetAsync(conv.Id, CancellationToken.None))!.PersonaId);
    }

    [Fact]
    public async Task AddsPersonaColumnToPreExistingDatabase()
    {
        var path = Path.Combine(_dir, "legacy.db");
        Directory.CreateDirectory(_dir);
        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync(CancellationToken.None);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE conversations (id TEXT PRIMARY KEY, title TEXT NOT NULL, backend TEXT NOT NULL, model TEXT NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
                CREATE TABLE messages (id INTEGER PRIMARY KEY AUTOINCREMENT, conversation_id TEXT NOT NULL REFERENCES conversations(id) ON DELETE CASCADE, role TEXT NOT NULL, content TEXT NOT NULL, created_at TEXT NOT NULL);
                INSERT INTO conversations VALUES ('11111111-1111-1111-1111-111111111111', 'old', 'b', 'm', '2026-01-01T00:00:00.0000000+00:00', '2026-01-01T00:00:00.0000000+00:00');
                """;
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
        }

        var legacyDb = new SqliteDatabase(
            Microsoft.Extensions.Options.Options.Create(new DatabaseOptions { ConnectionString = $"Data Source={path}" }));
        var legacy = new SqliteConversationStore(legacyDb);

        var old = await legacy.GetAsync(Guid.Parse("11111111-1111-1111-1111-111111111111"), CancellationToken.None);
        Assert.NotNull(old);
        Assert.Null(old.PersonaId);

        var persona = (await new SqlitePersonaStore(legacyDb).ListAsync(CancellationToken.None)).First();
        await legacy.SetPersonaAsync(old.Id, persona.Id, CancellationToken.None);
        Assert.Equal(persona.Id, (await legacy.GetAsync(old.Id, CancellationToken.None))!.PersonaId);
    }

    [Fact]
    public async Task GetMissingReturnsNull()
    {
        Assert.Null(await _store.GetAsync(Guid.NewGuid(), CancellationToken.None));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
