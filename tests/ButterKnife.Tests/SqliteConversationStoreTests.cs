using ButterKnife.Data;
using ButterKnife.Services;
using Microsoft.Data.Sqlite;

namespace ButterKnife.Tests;

public sealed class SqliteConversationStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "butterknife-tests", Guid.NewGuid().ToString("N"));
    private static readonly Guid ConnA = Guid.NewGuid();
    private static readonly Guid ConnB = Guid.NewGuid();

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
        var created = await _store.CreateAsync("Hello there", ConnA, "llama3", null, CancellationToken.None);
        await _store.AppendMessageAsync(created.Id, new ChatMessage(ChatRole.User, "hi"), CancellationToken.None);
        await _store.AppendMessageAsync(created.Id, new ChatMessage(ChatRole.Assistant, "hello **you**"), CancellationToken.None);

        var loaded = await _store.GetAsync(created.Id, CancellationToken.None);

        Assert.True(Directory.Exists(Path.Combine(_dir, "nested")));
        Assert.NotNull(loaded);
        Assert.Equal("Hello there", loaded.Title);
        Assert.Equal(ConnA, loaded.ConnectionId);
        Assert.Equal("llama3", loaded.Model);
        Assert.Equal(
            [(ChatRole.User, "hi"), (ChatRole.Assistant, "hello **you**")],
            loaded.Messages.Select(m => (m.Role, m.Content)));
        Assert.True(loaded.UpdatedAt >= loaded.CreatedAt);
    }

    [Fact]
    public async Task RoundTripsImagesAndCascadesThem()
    {
        var conv = await _store.CreateAsync("img", ConnA, "m", null, CancellationToken.None);
        var bytes = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
        await _store.AppendMessageAsync(conv.Id,
            new ChatMessage(ChatRole.User, "look", [new ChatImage("image/png", bytes), new ChatImage("image/jpeg", [1, 2, 3])]),
            CancellationToken.None);
        await _store.AppendMessageAsync(conv.Id, new ChatMessage(ChatRole.Assistant, "seen"), CancellationToken.None);

        var loaded = await _store.GetAsync(conv.Id, CancellationToken.None);

        Assert.Equal(2, loaded!.Messages.Count);
        Assert.Equal(2, loaded.Messages[0].Images.Count);
        Assert.Equal("image/png", loaded.Messages[0].Images[0].MediaType);
        Assert.Equal(bytes, loaded.Messages[0].Images[0].Data);
        Assert.Equal(new byte[] { 1, 2, 3 }, loaded.Messages[0].Images[1].Data);
        Assert.False(loaded.Messages[1].HasImages);

        await _store.DeleteAsync(conv.Id, CancellationToken.None);

        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_dir, "nested", "test.db")}");
        await connection.OpenAsync(CancellationToken.None);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM message_images;";
        Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync(CancellationToken.None))!);
    }

    [Fact]
    public async Task ListsMostRecentlyUpdatedFirst()
    {
        var older = await _store.CreateAsync("older", ConnB, "m", null, CancellationToken.None);
        await Task.Delay(5, CancellationToken.None);
        var newer = await _store.CreateAsync("newer", ConnB, "m", null, CancellationToken.None);
        await Task.Delay(5, CancellationToken.None);
        await _store.AppendMessageAsync(older.Id, new ChatMessage(ChatRole.User, "bump"), CancellationToken.None);

        var list = await _store.ListAsync(CancellationToken.None);

        Assert.Equal([older.Id, newer.Id], list.Select(c => c.Id));
        Assert.Equal("older", list[0].Title);
    }

    [Fact]
    public async Task DeleteCascadesMessages()
    {
        var conv = await _store.CreateAsync("t", ConnB, "m", null, CancellationToken.None);
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
        var conv = await _store.CreateAsync("t", ConnA, "a", null, CancellationToken.None);

        await _store.SetModelAsync(conv.Id, ConnB, "b", CancellationToken.None);

        var loaded = await _store.GetAsync(conv.Id, CancellationToken.None);
        Assert.Equal((ConnB, "b"), (loaded!.ConnectionId, loaded.Model));
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

        var conv = await _store.CreateAsync("t", ConnB, "m", programmer.Id, CancellationToken.None);
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
    public async Task RoundTripsSummaryAndContextUsage()
    {
        var conv = await _store.CreateAsync("t", ConnA, "m", null, CancellationToken.None);
        Assert.Null((await _store.GetAsync(conv.Id, CancellationToken.None))!.Summary);

        await _store.SetSummaryAsync(conv.Id, "The user asked about X.", 6, CancellationToken.None);
        await _store.SetContextUsageAsync(conv.Id, 3200, 32768, CancellationToken.None);

        var loaded = await _store.GetAsync(conv.Id, CancellationToken.None);
        Assert.Equal(("The user asked about X.", 6, 3200, 32768), (loaded!.Summary, loaded.SummaryThrough, loaded.ContextTokens, loaded.ContextWindow));

        await _store.SetSummaryAsync(conv.Id, null, null, CancellationToken.None);
        loaded = await _store.GetAsync(conv.Id, CancellationToken.None);
        Assert.Null(loaded!.Summary);
        Assert.Null(loaded.SummaryThrough);
    }

    [Fact]
    public async Task RenamesAndValidatesTitle()
    {
        var conv = await _store.CreateAsync("old", ConnA, "m", null, CancellationToken.None);

        await _store.SetTitleAsync(conv.Id, "  New title  ", CancellationToken.None);
        Assert.Equal("New title", (await _store.GetAsync(conv.Id, CancellationToken.None))!.Title);
        Assert.Equal("New title", (await _store.ListAsync(CancellationToken.None)).Single().Title);

        await _store.SetTitleAsync(conv.Id, new string('x', 500), CancellationToken.None);
        Assert.Equal(SqliteConversationStore.MaxTitleLength, (await _store.GetAsync(conv.Id, CancellationToken.None))!.Title.Length);

        await Assert.ThrowsAsync<ArgumentException>(() => _store.SetTitleAsync(conv.Id, "   ", CancellationToken.None));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _store.SetTitleAsync(Guid.NewGuid(), "x", CancellationToken.None));
    }

    [Fact]
    public async Task GetMissingReturnsNull()
    {
        Assert.Null(await _store.GetAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task MessagesCarryIdsAndTimestamps_DeleteFromTruncates_AndClearsAStaleSummary()
    {
        var conv = await _store.CreateAsync("t", Guid.NewGuid(), "m", null, CancellationToken.None);
        var ids = new List<long>();
        foreach (var (role, text) in new[] { (ChatRole.User, "a"), (ChatRole.Assistant, "b"), (ChatRole.User, "c"), (ChatRole.Assistant, "d") })
        {
            ids.Add(await _store.AppendMessageAsync(conv.Id, new ChatMessage(role, text), CancellationToken.None));
        }
        await _store.SetSummaryAsync(conv.Id, "summary of a+b", 2, CancellationToken.None);
        await _store.SetContextUsageAsync(conv.Id, 500, 8000, CancellationToken.None);

        var loaded = (await _store.GetAsync(conv.Id, CancellationToken.None))!;
        Assert.Equal(ids, loaded.Messages.Select(m => m.Id!.Value));
        Assert.All(loaded.Messages, m => Assert.NotNull(m.CreatedAt));
        Assert.True(ids[0] < ids[1] && ids[1] < ids[2] && ids[2] < ids[3]);

        // Regenerate-style truncation from "c": summary still covers a+b, so it survives; context usage resets.
        await _store.DeleteMessagesFromAsync(conv.Id, ids[2], CancellationToken.None);
        loaded = (await _store.GetAsync(conv.Id, CancellationToken.None))!;
        Assert.Equal(["a", "b"], loaded.Messages.Select(m => m.Content));
        Assert.Equal("summary of a+b", loaded.Summary);
        Assert.Null(loaded.ContextTokens);

        // Edit-style truncation from "b": the summary claimed two messages and only one remains, so it is cleared.
        await _store.DeleteMessagesFromAsync(conv.Id, ids[1], CancellationToken.None);
        loaded = (await _store.GetAsync(conv.Id, CancellationToken.None))!;
        Assert.Equal(["a"], loaded.Messages.Select(m => m.Content));
        Assert.Null(loaded.Summary);
        Assert.Null(loaded.SummaryThrough);
    }

    [Fact]
    public async Task ReasoningRoundTrips()
    {
        var conv = await _store.CreateAsync("t", Guid.NewGuid(), "m", null, CancellationToken.None);
        await _store.AppendMessageAsync(conv.Id, new ChatMessage(ChatRole.User, "q"), CancellationToken.None);
        await _store.AppendMessageAsync(conv.Id, new ChatMessage(ChatRole.Assistant, "a") { Reasoning = "because" }, CancellationToken.None);

        var loaded = (await _store.GetAsync(conv.Id, CancellationToken.None))!;
        Assert.Null(loaded.Messages[0].Reasoning);
        Assert.Equal("because", loaded.Messages[1].Reasoning);
    }

    [Fact]
    public async Task SetMessageContentRewritesTextAndReasoning()
    {
        var conv = await _store.CreateAsync("t", Guid.NewGuid(), "m", null, CancellationToken.None);
        var id = await _store.AppendMessageAsync(conv.Id, new ChatMessage(ChatRole.Assistant, "partial") { Reasoning = "r" }, CancellationToken.None);

        await _store.SetMessageContentAsync(conv.Id, id, "partial and the rest", "r more", cancellationToken: CancellationToken.None);

        var loaded = (await _store.GetAsync(conv.Id, CancellationToken.None))!;
        Assert.Equal("partial and the rest", loaded.Messages[0].Content);
        Assert.Equal("r more", loaded.Messages[0].Reasoning);
    }

    [Fact]
    public async Task BranchCopiesMessagesUpToAPoint_WithImagesReasoningAndAFittingSummary()
    {
        var persona = DefaultPersonas.All[0].Id; // must exist: persona_id is a foreign key
        var conv = await _store.CreateAsync("origin", ConnA, "llama3", persona, CancellationToken.None);
        var m1 = await _store.AppendMessageAsync(conv.Id, new ChatMessage(ChatRole.User, "look", [new ChatImage("image/png", [9, 9])]), CancellationToken.None);
        var m2 = await _store.AppendMessageAsync(conv.Id, new ChatMessage(ChatRole.Assistant, "a") { Reasoning = "r" }, CancellationToken.None);
        var m3 = await _store.AppendMessageAsync(conv.Id, new ChatMessage(ChatRole.User, "later"), CancellationToken.None);
        await _store.SetSummaryAsync(conv.Id, "sum", 2, CancellationToken.None);

        var branch = await _store.BranchAsync(conv.Id, m2, "origin (branch)", CancellationToken.None);

        Assert.NotEqual(conv.Id, branch.Id);
        Assert.Equal("origin (branch)", branch.Title);
        Assert.Equal((ConnA, "llama3", persona), (branch.ConnectionId, branch.Model, branch.PersonaId));
        Assert.Equal(["look", "a"], branch.Messages.Select(m => m.Content));
        Assert.Equal([9, 9], branch.Messages[0].Images[0].Data);
        Assert.Equal("r", branch.Messages[1].Reasoning);
        Assert.Equal(("sum", 2), (branch.Summary, branch.SummaryThrough));
        Assert.NotEqual(branch.Messages[0].Id, m1);

        // Branching before the summary's checkpoint drops the summary; the original is untouched.
        var earlier = await _store.BranchAsync(conv.Id, m1, "b2", CancellationToken.None);
        Assert.Single(earlier.Messages);
        Assert.Null(earlier.Summary);
        Assert.Equal(3, (await _store.GetAsync(conv.Id, CancellationToken.None))!.Messages.Count);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _store.BranchAsync(conv.Id, m3 + 100, "x", CancellationToken.None));
    }

    [Fact]
    public async Task DeleteMessageRemovesOnlyThatMessageAndItsImages()
    {
        var conv = await _store.CreateAsync("t", Guid.NewGuid(), "m", null, CancellationToken.None);
        var first = await _store.AppendMessageAsync(conv.Id, new ChatMessage(ChatRole.User, "look", [new ChatImage("image/png", [1, 2])]), CancellationToken.None);
        var second = await _store.AppendMessageAsync(conv.Id, new ChatMessage(ChatRole.Assistant, "nice"), CancellationToken.None);

        await _store.DeleteMessageAsync(conv.Id, first, CancellationToken.None);

        var loaded = (await _store.GetAsync(conv.Id, CancellationToken.None))!;
        Assert.Equal([second], loaded.Messages.Select(m => m.Id!.Value));
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_dir, "nested", "test.db")}");
        await connection.OpenAsync(CancellationToken.None);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM message_images;";
        Assert.Equal(0L, await cmd.ExecuteScalarAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StoresModelAndStatsWithRepliesAndKeepsThemUnlessReplaced()
    {
        var conv = await _store.CreateAsync("t", ConnA, "m", null, CancellationToken.None);
        var stats = new GenerationStats(TimeSpan.FromSeconds(2.5), TimeSpan.FromMilliseconds(300), 40, new TokenUsage(120, 38, TimeSpan.FromMilliseconds(180), TimeSpan.FromSeconds(2)));
        var userId = await _store.AppendMessageAsync(conv.Id, new ChatMessage(ChatRole.User, "hi"), CancellationToken.None);
        var replyId = await _store.AppendMessageAsync(conv.Id, new ChatMessage(ChatRole.Assistant, "hey") { Model = "qwen3", Stats = stats }, CancellationToken.None);

        var loaded = (await _store.GetAsync(conv.Id, CancellationToken.None))!.Messages;
        Assert.Null(loaded[0].Model);
        Assert.Null(loaded[0].Stats);
        Assert.Equal("qwen3", loaded[1].Model);
        Assert.Equal(stats, loaded[1].Stats);

        await _store.SetMessageContentAsync(conv.Id, replyId, "hey there", null, cancellationToken: CancellationToken.None);
        loaded = (await _store.GetAsync(conv.Id, CancellationToken.None))!.Messages;
        Assert.Equal("qwen3", loaded[1].Model);
        Assert.Equal(stats, loaded[1].Stats);

        var more = stats with { Chunks = 60 };
        await _store.SetMessageContentAsync(conv.Id, replyId, "hey there, friend", null, "llama3", more, CancellationToken.None);
        loaded = (await _store.GetAsync(conv.Id, CancellationToken.None))!.Messages;
        Assert.Equal("llama3", loaded[1].Model);
        Assert.Equal(more, loaded[1].Stats);
        Assert.Equal(userId, loaded[0].Id);
    }

    [Fact]
    public async Task PinnedConversationsListFirstAndArchivedOnesAreFlagged()
    {
        var older = await _store.CreateAsync("older", ConnA, "m", null, CancellationToken.None);
        await Task.Delay(10);
        var newer = await _store.CreateAsync("newer", ConnA, "m", null, CancellationToken.None);
        await Task.Delay(10);
        var newest = await _store.CreateAsync("newest", ConnA, "m", null, CancellationToken.None);

        Assert.Equal(["newest", "newer", "older"], (await _store.ListAsync(CancellationToken.None)).Select(c => c.Title));

        await _store.SetPinnedAsync(older.Id, true, CancellationToken.None);
        await _store.SetArchivedAsync(newer.Id, true, CancellationToken.None);
        var list = await _store.ListAsync(CancellationToken.None);

        Assert.Equal(["older", "newest", "newer"], list.Select(c => c.Title));
        Assert.True(list[0].Pinned);
        Assert.False(list[0].Archived);
        Assert.True(list[2].Archived);
        Assert.Equal(older.UpdatedAt, list[0].UpdatedAt); // flags are not activity

        await _store.SetPinnedAsync(older.Id, false, CancellationToken.None);
        await _store.SetArchivedAsync(newer.Id, false, CancellationToken.None);
        Assert.All(await _store.ListAsync(CancellationToken.None), c => Assert.False(c.Pinned || c.Archived));
        Assert.NotNull(await _store.GetAsync(newest.Id, CancellationToken.None));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
