using ButterKnife.Data;
using Microsoft.Data.Sqlite;

namespace ButterKnife.Tests;

public sealed class SqlitePersonaStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "butterknife-tests", Guid.NewGuid().ToString("N"));
    private readonly SqliteDatabase _db;
    private readonly SqlitePersonaStore _store;

    public SqlitePersonaStoreTests()
    {
        _db = new SqliteDatabase(
            Microsoft.Extensions.Options.Options.Create(new DatabaseOptions
            {
                ConnectionString = $"Data Source={Path.Combine(_dir, "test.db")}",
            }));
        _store = new SqlitePersonaStore(_db);
    }

    [Fact]
    public async Task SeedsBuiltInPersonasInOrderAndOnlyOnce()
    {
        var first = await _store.ListAsync(CancellationToken.None);
        var second = await new SqlitePersonaStore(new SqliteDatabase(
            Microsoft.Extensions.Options.Options.Create(new DatabaseOptions
            {
                ConnectionString = $"Data Source={Path.Combine(_dir, "test.db")}",
            }))).ListAsync(CancellationToken.None);

        Assert.Equal(DefaultPersonas.All.Select(p => p.Name), first.Select(p => p.Name));
        Assert.All(first, p => Assert.True(p.IsBuiltIn));
        Assert.Contains(first, p => p.Name == "Programmer");
        Assert.Equal(first.Count, second.Count);
    }

    [Fact]
    public async Task CreatesUpdatesAndDeletesCustomPersona()
    {
        var created = await _store.CreateAsync("  Pirate ", "Talks like a pirate", " Arr. ", CancellationToken.None);

        Assert.False(created.IsBuiltIn);
        Assert.Equal(("Pirate", "Arr."), (created.Name, created.SystemPrompt));

        var list = await _store.ListAsync(CancellationToken.None);
        Assert.Equal("Pirate", list.Last().Name); // custom after built-ins

        await _store.UpdateAsync(created.Id, "Buccaneer", "d", "Yarr.", CancellationToken.None);
        var updated = await _store.GetAsync(created.Id, CancellationToken.None);
        Assert.Equal(("Buccaneer", "d", "Yarr."), (updated!.Name, updated.Description, updated.SystemPrompt));
        Assert.True(updated.UpdatedAt >= updated.CreatedAt);

        await _store.DeleteAsync(created.Id, CancellationToken.None);
        Assert.Null(await _store.GetAsync(created.Id, CancellationToken.None));
    }

    [Fact]
    public async Task BuiltInPersonasCanBeEditedButNotDeleted()
    {
        var doctor = (await _store.ListAsync(CancellationToken.None)).Single(p => p.Name == "Doctor");

        await _store.UpdateAsync(doctor.Id, "Doctor", doctor.Description, "Edited prompt.", CancellationToken.None);
        Assert.Equal("Edited prompt.", (await _store.GetAsync(doctor.Id, CancellationToken.None))!.SystemPrompt);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.DeleteAsync(doctor.Id, CancellationToken.None));
        Assert.NotNull(await _store.GetAsync(doctor.Id, CancellationToken.None));
    }

    [Fact]
    public async Task DeletingPersonaClearsItFromConversations()
    {
        var conversations = new SqliteConversationStore(_db);
        var custom = await _store.CreateAsync("Temp", "", "p", CancellationToken.None);
        var conv = await conversations.CreateAsync("t", Guid.NewGuid(), "m", custom.Id, CancellationToken.None);

        await _store.DeleteAsync(custom.Id, CancellationToken.None);

        Assert.Null((await conversations.GetAsync(conv.Id, CancellationToken.None))!.PersonaId);
    }

    [Fact]
    public async Task RejectsBlankNameOrPrompt()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _store.CreateAsync(" ", "", "p", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.CreateAsync("n", "", " ", CancellationToken.None));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
