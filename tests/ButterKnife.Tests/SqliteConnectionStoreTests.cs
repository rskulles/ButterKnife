using ButterKnife.Data;
using ButterKnife.Options;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace ButterKnife.Tests;

public sealed class SqliteConnectionStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "butterknife-tests", Guid.NewGuid().ToString("N"));
    private readonly string _path;
    private readonly SqliteConnectionStore _store;

    public SqliteConnectionStoreTests()
    {
        _path = Path.Combine(_dir, "test.db");
        var db = new SqliteDatabase(Microsoft.Extensions.Options.Options.Create(new DatabaseOptions { ConnectionString = $"Data Source={_path}" }));
        _store = new SqliteConnectionStore(db, new EphemeralDataProtectionProvider(), NullLogger<SqliteConnectionStore>.Instance);
    }

    [Fact]
    public async Task RoundTripsAndProtectsApiKeyAtRest()
    {
        var created = await _store.CreateAsync(" Claude ", BackendKind.Anthropic, "https://api.anthropic.com/ ", "sk-ant-secret", "claude-opus-5", CancellationToken.None);

        var loaded = await _store.GetAsync(created.Id, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(("Claude", BackendKind.Anthropic, "https://api.anthropic.com/", "sk-ant-secret", "claude-opus-5"),
            (loaded.Name, loaded.Kind, loaded.BaseUrl, loaded.ApiKey, loaded.DefaultModel));
        Assert.True(loaded.HasApiKey);

        await using var connection = new SqliteConnection($"Data Source={_path}");
        await connection.OpenAsync(CancellationToken.None);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT api_key FROM connections;";
        var raw = (string)(await cmd.ExecuteScalarAsync(CancellationToken.None))!;
        Assert.NotEqual("sk-ant-secret", raw);
        Assert.DoesNotContain("secret", raw);
    }

    [Fact]
    public async Task ListsByNameUpdatesAndDeletes()
    {
        var b = await _store.CreateAsync("beta", BackendKind.Ollama, "http://b:11434", null, null, CancellationToken.None);
        var a = await _store.CreateAsync("Alpha", BackendKind.OpenAiCompatible, "http://a:1234/v1", null, null, CancellationToken.None);

        Assert.Equal(["Alpha", "beta"], (await _store.ListAsync(CancellationToken.None)).Select(c => c.Name));

        await _store.UpdateAsync(b.Id, "Zeta", BackendKind.Ollama, "http://z:11434", "key", "m", CancellationToken.None);
        var updated = await _store.GetAsync(b.Id, CancellationToken.None);
        Assert.Equal(("Zeta", "http://z:11434", "key", "m"), (updated!.Name, updated.BaseUrl, updated.ApiKey, updated.DefaultModel));

        await _store.UpdateAsync(b.Id, "Zeta", BackendKind.Ollama, "http://z:11434", null, null, CancellationToken.None);
        Assert.False((await _store.GetAsync(b.Id, CancellationToken.None))!.HasApiKey);

        await _store.DeleteAsync(a.Id, CancellationToken.None);
        Assert.Single(await _store.ListAsync(CancellationToken.None));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _store.UpdateAsync(a.Id, "x", BackendKind.Ollama, "http://x/", null, null, CancellationToken.None));
    }

    [Fact]
    public async Task RejectsBadInput()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _store.CreateAsync("", BackendKind.Ollama, "http://x/", null, null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.CreateAsync("n", BackendKind.Ollama, "not a url", null, null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.CreateAsync("n", BackendKind.Ollama, "ftp://x/", null, null, CancellationToken.None));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
