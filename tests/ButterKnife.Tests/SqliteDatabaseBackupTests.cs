using ButterKnife.Data;
using Microsoft.Data.Sqlite;

namespace ButterKnife.Tests;

public sealed class SqliteDatabaseBackupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "butterknife-tests", Guid.NewGuid().ToString("N"));

    private SqliteDatabase Open(string file) => new(Microsoft.Extensions.Options.Options.Create(new DatabaseOptions
    {
        ConnectionString = $"Data Source={Path.Combine(_dir, file)}",
    }));

    [Fact]
    public async Task BackupThenRestoreBringsTheOldRowsBack()
    {
        var database = Open("live.db");
        var settings = new SqliteSettingsStore(database);
        await settings.SetAsync(SettingKeys.UserDisplayName, "Before", CancellationToken.None);

        var backup = Path.Combine(_dir, "backup.db");
        await database.BackupToAsync(backup, CancellationToken.None);
        Assert.True(new FileInfo(backup).Length > 0);

        await settings.SetAsync(SettingKeys.UserDisplayName, "After", CancellationToken.None);
        Assert.Equal("After", await settings.GetAsync(SettingKeys.UserDisplayName, CancellationToken.None));

        await database.RestoreFromAsync(backup, CancellationToken.None);

        Assert.Equal("Before", await settings.GetAsync(SettingKeys.UserDisplayName, CancellationToken.None));
        await settings.SetAsync(SettingKeys.UserDisplayName, "Writable again", CancellationToken.None); // the restored file is fully usable
    }

    [Fact]
    public async Task RestoreRejectsFilesThatAreNotButterKnifeDatabases()
    {
        var database = Open("live.db");
        await new SqliteSettingsStore(database).SetAsync(SettingKeys.UserDisplayName, "Keep", CancellationToken.None);

        var text = Path.Combine(_dir, "notes.db");
        await File.WriteAllTextAsync(text, "just some text, long enough to have a header");
        await Assert.ThrowsAsync<InvalidDataException>(() => database.RestoreFromAsync(text, CancellationToken.None));

        var other = Path.Combine(_dir, "other.db");
        await using (var connection = new SqliteConnection($"Data Source={other};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "CREATE TABLE notes (id INTEGER PRIMARY KEY);";
            await cmd.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => database.RestoreFromAsync(other, CancellationToken.None));

        Assert.Equal("Keep", await new SqliteSettingsStore(database).GetAsync(SettingKeys.UserDisplayName, CancellationToken.None));
    }

    [Fact]
    public async Task BackupOverwritesAnExistingFile()
    {
        var database = Open("live.db");
        await new SqliteSettingsStore(database).SetAsync(SettingKeys.UserDisplayName, "x", CancellationToken.None);
        var backup = Path.Combine(_dir, "backup.db");
        await File.WriteAllTextAsync(backup, "stale");

        await database.BackupToAsync(backup, CancellationToken.None);

        var header = new byte[16];
        await using var file = File.OpenRead(backup);
        Assert.Equal(16, await file.ReadAsync(header));
        Assert.Equal("SQLite format 3", System.Text.Encoding.ASCII.GetString(header, 0, 15));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
