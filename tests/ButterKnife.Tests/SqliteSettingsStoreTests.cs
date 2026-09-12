using ButterKnife.Data;

namespace ButterKnife.Tests;

public sealed class SqliteSettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "butterknife-tests", Guid.NewGuid().ToString("N"));
    private readonly SqliteSettingsStore _store;

    public SqliteSettingsStoreTests()
    {
        _store = new SqliteSettingsStore(new SqliteDatabase(
            Microsoft.Extensions.Options.Options.Create(new DatabaseOptions
            {
                ConnectionString = $"Data Source={Path.Combine(_dir, "test.db")}",
            })));
    }

    [Fact]
    public async Task MissingKeyIsNull_SetUpsertsTrimmed_BlankDeletes()
    {
        Assert.Null(await _store.GetAsync(SettingKeys.UserDisplayName, CancellationToken.None));

        await _store.SetAsync(SettingKeys.UserDisplayName, "  Roy  ", CancellationToken.None);
        Assert.Equal("Roy", await _store.GetAsync(SettingKeys.UserDisplayName, CancellationToken.None));

        await _store.SetAsync(SettingKeys.UserDisplayName, "Roy S.", CancellationToken.None);
        Assert.Equal("Roy S.", await _store.GetAsync(SettingKeys.UserDisplayName, CancellationToken.None));

        await _store.SetAsync(SettingKeys.UserDisplayName, "   ", CancellationToken.None);
        Assert.Null(await _store.GetAsync(SettingKeys.UserDisplayName, CancellationToken.None));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
