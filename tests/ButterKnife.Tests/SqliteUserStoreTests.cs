using ButterKnife.Data;
using ButterKnife.Services;
using Microsoft.Data.Sqlite;

namespace ButterKnife.Tests;

public sealed class SqliteUserStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "butterknife-tests", Guid.NewGuid().ToString("N"));
    private readonly string _path;
    private readonly SqliteDatabase _db;
    private readonly SqliteUserStore _users;
    private readonly SqliteConversationStore _conversations;

    public SqliteUserStoreTests()
    {
        _path = Path.Combine(_dir, "users.db");
        _db = new SqliteDatabase(Microsoft.Extensions.Options.Options.Create(new DatabaseOptions { ConnectionString = $"Data Source={_path}" }));
        _users = new SqliteUserStore(_db);
        _conversations = new SqliteConversationStore(_db);
    }

    [Fact]
    public async Task FreshDatabaseHasAnOwnerWhoIsAnAdminWithoutAPassword()
    {
        var owner = await _users.GetOwnerAsync(CancellationToken.None);

        Assert.Equal("owner", owner.Username);
        Assert.Equal(SettingKeys.DefaultUserDisplayName, owner.DisplayName);
        Assert.True(owner.IsOwner);
        Assert.True(owner.IsAdmin);
        Assert.False(owner.HasPassword);
        Assert.NotEmpty(owner.SecurityStamp);
        Assert.Equal(owner.Id, Assert.Single(await _users.ListAsync(CancellationToken.None)).Id);
        Assert.Equal(owner.Id, (await _users.GetOwnerAsync(CancellationToken.None)).Id); // created once, not per open
    }

    [Fact]
    public async Task SeedsTheOwnerFromTheLegacyDisplayNameAndAdoptsOldChats()
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
                CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL, updated_at TEXT NOT NULL);
                INSERT INTO settings VALUES ('user.display_name', 'Roy', '2026-01-01T00:00:00.0000000+00:00');
                INSERT INTO settings VALUES ('network.pin_hash', 'pbkdf2-sha256$1$a$b', '2026-01-01T00:00:00.0000000+00:00');
                INSERT INTO settings VALUES ('server.listen_on_lan', 'true', '2026-01-01T00:00:00.0000000+00:00');
                INSERT INTO conversations VALUES ('11111111-1111-1111-1111-111111111111', 'old', 'b', 'm', '2026-01-01T00:00:00.0000000+00:00', '2026-01-01T00:00:00.0000000+00:00');
                """;
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
        }

        var db = new SqliteDatabase(Microsoft.Extensions.Options.Options.Create(new DatabaseOptions { ConnectionString = $"Data Source={path}" }));
        var users = new SqliteUserStore(db);
        var conversations = new SqliteConversationStore(db);
        var settings = new SqliteSettingsStore(db);

        var owner = await users.GetOwnerAsync(CancellationToken.None);
        Assert.Equal("Roy", owner.DisplayName);
        Assert.True(owner.IsAdmin);

        var old = await conversations.GetAsync(Guid.Parse("11111111-1111-1111-1111-111111111111"), owner.Id, CancellationToken.None);
        Assert.NotNull(old);
        Assert.Equal(owner.Id, old.UserId);
        Assert.Single(await conversations.ListAsync(owner.Id, CancellationToken.None));

        Assert.Null(await settings.GetAsync(SettingKeys.UserDisplayName, CancellationToken.None)); // moved onto the owner
        Assert.Null(await settings.GetAsync("network.pin_hash", CancellationToken.None)); // the PIN gave way to accounts
        Assert.Equal("true", await settings.GetAsync(SettingKeys.ListenOnLan, CancellationToken.None)); // everything else stays
    }

    [Fact]
    public async Task CreatesUsersWithUniqueUsernamesAndHashedPasswords()
    {
        var ann = await _users.CreateAsync(" ann ", " Ann ", "secret1", isAdmin: false, CancellationToken.None);

        Assert.Equal("ann", ann.Username);
        Assert.Equal("Ann", ann.DisplayName);
        Assert.True(ann.HasPassword);
        Assert.False(ann.IsAdmin);
        Assert.False(ann.IsOwner);
        Assert.Null(ann.LastLoginAt);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _users.CreateAsync("ANN", "Other", "secret2", false, CancellationToken.None)); // taken, any case
        await Assert.ThrowsAsync<ArgumentException>(() => _users.CreateAsync("a b", "Bad", "secret2", false, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => _users.CreateAsync("bob", " ", "secret2", false, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => _users.CreateAsync("bob", "Bob", "123", false, CancellationToken.None));

        await using var connection = new SqliteConnection($"Data Source={_path}");
        await connection.OpenAsync(CancellationToken.None);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT password_hash FROM users WHERE username = 'ann';";
        var stored = (string)(await cmd.ExecuteScalarAsync(CancellationToken.None))!;
        Assert.StartsWith("pbkdf2-sha256$", stored);
        Assert.DoesNotContain("secret1", stored);

        Assert.Equal([SettingKeys.DefaultUserDisplayName, "Ann"], (await _users.ListAsync(CancellationToken.None)).Select(u => u.DisplayName)); // owner first
        Assert.Equal(ann.Id, (await _users.FindByUsernameAsync("Ann", CancellationToken.None))!.Id);
        Assert.Null(await _users.FindByUsernameAsync("nobody", CancellationToken.None));
    }

    [Fact]
    public async Task VerifiesPasswordsAndRejectsEverythingElse()
    {
        var ann = await _users.CreateAsync("ann", "Ann", "secret1", false, CancellationToken.None);

        Assert.Equal(ann.Id, (await _users.VerifyPasswordAsync("Ann", "secret1", CancellationToken.None))!.Id);
        Assert.Null(await _users.VerifyPasswordAsync("ann", "wrong", CancellationToken.None));
        Assert.Null(await _users.VerifyPasswordAsync("nobody", "secret1", CancellationToken.None));
        Assert.Null(await _users.VerifyPasswordAsync("owner", "", CancellationToken.None));
        Assert.Null(await _users.VerifyPasswordAsync("owner", "anything", CancellationToken.None)); // no password yet

        var owner = await _users.GetOwnerAsync(CancellationToken.None);
        await _users.SetPasswordAsync(owner.Id, "ownerpw", signOutEverywhere: false, CancellationToken.None);
        Assert.Equal(owner.Id, (await _users.VerifyPasswordAsync("owner", "ownerpw", CancellationToken.None))!.Id);
        Assert.True((await _users.GetOwnerAsync(CancellationToken.None)).HasPassword);

        await _users.TouchLoginAsync(owner.Id, CancellationToken.None);
        Assert.NotNull((await _users.GetOwnerAsync(CancellationToken.None)).LastLoginAt);
    }

    [Fact]
    public async Task StampsChangeOnAdminResetsAndRoleChangesButNotOnAnOwnPasswordChange()
    {
        var ann = await _users.CreateAsync("ann", "Ann", "secret1", false, CancellationToken.None);

        await _users.SetPasswordAsync(ann.Id, "secret2", signOutEverywhere: false, CancellationToken.None);
        Assert.Equal(ann.SecurityStamp, (await _users.GetAsync(ann.Id, CancellationToken.None))!.SecurityStamp);
        Assert.NotNull(await _users.VerifyPasswordAsync("ann", "secret2", CancellationToken.None));

        await _users.SetPasswordAsync(ann.Id, "secret3", signOutEverywhere: true, CancellationToken.None);
        var reset = (await _users.GetAsync(ann.Id, CancellationToken.None))!;
        Assert.NotEqual(ann.SecurityStamp, reset.SecurityStamp);

        await _users.SetAdminAsync(ann.Id, true, CancellationToken.None);
        var admin = (await _users.GetAsync(ann.Id, CancellationToken.None))!;
        Assert.True(admin.IsAdmin);
        Assert.NotEqual(reset.SecurityStamp, admin.SecurityStamp);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _users.SetPasswordAsync(ann.Id, null, false, CancellationToken.None)); // only the owner may have none
        await Assert.ThrowsAsync<ArgumentException>(() => _users.SetPasswordAsync(ann.Id, "x", false, CancellationToken.None));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _users.SetPasswordAsync(Guid.NewGuid(), "secret", false, CancellationToken.None));

        var owner = await _users.GetOwnerAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _users.SetAdminAsync(owner.Id, false, CancellationToken.None));
        await _users.SetPasswordAsync(owner.Id, "pw1234", false, CancellationToken.None);
        await _users.SetPasswordAsync(owner.Id, null, true, CancellationToken.None);
        Assert.False((await _users.GetOwnerAsync(CancellationToken.None)).HasPassword);
    }

    [Fact]
    public async Task DeletingAUserRemovesTheirChatsOnlyAndNeverTheOwner()
    {
        var owner = await _users.GetOwnerAsync(CancellationToken.None);
        var ann = await _users.CreateAsync("ann", "Ann", "secret1", false, CancellationToken.None);
        var mine = await _conversations.CreateAsync(owner.Id, "mine", Guid.NewGuid(), "m", null, CancellationToken.None);
        var hers = await _conversations.CreateAsync(ann.Id, "hers", Guid.NewGuid(), "m", null, CancellationToken.None);
        await _conversations.AppendMessageAsync(hers.Id, new ChatMessage(ChatRole.User, "hi", [new ChatImage("image/png", [1, 2])]), CancellationToken.None);

        await _users.DeleteAsync(ann.Id, CancellationToken.None);

        Assert.Null(await _users.GetAsync(ann.Id, CancellationToken.None));
        Assert.Null(await _conversations.GetAsync(hers.Id, ann.Id, CancellationToken.None));
        Assert.NotNull(await _conversations.GetAsync(mine.Id, owner.Id, CancellationToken.None));

        await using var connection = new SqliteConnection($"Data Source={_path}");
        await connection.OpenAsync(CancellationToken.None);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT (SELECT COUNT(*) FROM messages) + (SELECT COUNT(*) FROM message_images);";
        Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync(CancellationToken.None))!);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _users.DeleteAsync(owner.Id, CancellationToken.None));
        await _users.DeleteAsync(Guid.NewGuid(), CancellationToken.None); // unknown: nothing to do
        Assert.Single(await _users.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ChatsAreScopedToTheirUser()
    {
        var owner = await _users.GetOwnerAsync(CancellationToken.None);
        var ann = await _users.CreateAsync("ann", "Ann", "secret1", false, CancellationToken.None);
        var mine = await _conversations.CreateAsync(owner.Id, "kittens plan", Guid.NewGuid(), "m", null, CancellationToken.None);
        await _conversations.AppendMessageAsync(mine.Id, new ChatMessage(ChatRole.User, "kittens are great"), CancellationToken.None);

        Assert.Equal(owner.Id, mine.UserId);
        Assert.Null(await _conversations.GetAsync(mine.Id, ann.Id, CancellationToken.None));
        Assert.Empty(await _conversations.ListAsync(ann.Id, CancellationToken.None));
        Assert.Empty(await _conversations.SearchAsync(ann.Id, "kitt", cancellationToken: CancellationToken.None));
        Assert.Equal(2, (await _conversations.SearchAsync(owner.Id, "kitt", cancellationToken: CancellationToken.None)).Count); // the title and the message

        var messageId = (await _conversations.GetAsync(mine.Id, owner.Id, CancellationToken.None))!.Messages[0].Id!.Value;
        var branch = await _conversations.BranchAsync(mine.Id, messageId, "branch", CancellationToken.None);
        Assert.Equal(owner.Id, branch.UserId);
        Assert.Equal(2, (await _conversations.ListAsync(owner.Id, CancellationToken.None)).Count);
        Assert.Empty(await _conversations.ListAsync(ann.Id, CancellationToken.None));
    }

    [Fact]
    public async Task UpdatesProfilesAndRefusesTakenUsernames()
    {
        var ann = await _users.CreateAsync("ann", "Ann", "secret1", false, CancellationToken.None);
        await _users.CreateAsync("bob", "Bob", "secret1", false, CancellationToken.None);

        await _users.UpdateProfileAsync(ann.Id, " annie ", " Annie ", CancellationToken.None);
        var updated = (await _users.GetAsync(ann.Id, CancellationToken.None))!;
        Assert.Equal(("annie", "Annie"), (updated.Username, updated.DisplayName));
        Assert.Equal(ann.SecurityStamp, updated.SecurityStamp); // a rename does not sign anyone out

        await Assert.ThrowsAsync<InvalidOperationException>(() => _users.UpdateProfileAsync(ann.Id, "BOB", "Annie", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => _users.UpdateProfileAsync(ann.Id, "a", "Annie", CancellationToken.None));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _users.UpdateProfileAsync(Guid.NewGuid(), "x1", "X", CancellationToken.None));

        Assert.Contains("letters", SqliteUserStore.ValidateUsername("a b"));
        Assert.Null(SqliteUserStore.ValidateUsername("roy.s_1-x"));
        Assert.Contains("name", SqliteUserStore.ValidateDisplayName("  "));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
