using System.Text.Json;
using ButterKnife.Data;
using ButterKnife.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ButterKnife.Tests;

public sealed class McpTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "butterknife-tests", Guid.NewGuid().ToString("N"));
    private readonly SqliteDatabase _db;

    public McpTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new SqliteDatabase(Microsoft.Extensions.Options.Options.Create(new DatabaseOptions { ConnectionString = $"Data Source={Path.Combine(_dir, "test.db")}" }));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static string StubServer => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", "stub-mcp-server.py"));

    private static McpServer Server(string name = "stub", McpTransportKind transport = McpTransportKind.Stdio, string? command = "python3", IReadOnlyList<string>? arguments = null,
        IReadOnlyDictionary<string, string>? environment = null, string? url = null, IReadOnlyDictionary<string, string>? headers = null) =>
        new(Guid.Empty, name, transport, command, arguments ?? [StubServer], environment ?? new Dictionary<string, string>(), null, url, headers ?? new Dictionary<string, string>(), true, default, default);

    private static bool CanRunStub => !OperatingSystem.IsWindows() && File.Exists(StubServer);

    [Theory]
    [InlineData("github", "create_issue", "github__create_issue")]
    [InlineData("my-files", "read.file", "my-files__read_file")]
    public void ToolNamesAreQualifiedSafely(string server, string tool, string expected) => Assert.Equal(expected, McpToolNames.Qualify(server, tool));

    [Fact]
    public void LongToolNamesStayUniqueWithinTheLimit()
    {
        var a = McpToolNames.Qualify("server", new string('a', 80) + "1");
        var b = McpToolNames.Qualify("server", new string('a', 80) + "2");

        Assert.Equal(McpToolNames.MaxLength, a.Length);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void SchemasWithoutTypeOrPropertiesAreCompleted()
    {
        var schema = McpToolNames.ObjectSchema(JsonDocument.Parse("""{"required":[]}""").RootElement);

        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Object, schema.GetProperty("properties").ValueKind);
    }

    [Fact]
    public async Task ServersAreStoredWithTheirSecretsEncrypted()
    {
        var store = new SqliteMcpServerStore(_db, new EphemeralDataProtectionProvider(), NullLogger<SqliteMcpServerStore>.Instance);

        var saved = await store.SaveAsync(Server("github", command: "npx", arguments: ["-y", "@modelcontextprotocol/server-github"], environment: new Dictionary<string, string> { ["GITHUB_TOKEN"] = "secret-token" }));
        var loaded = await store.GetAsync(saved.Id);

        Assert.Equal("secret-token", loaded!.Environment["GITHUB_TOKEN"]);
        Assert.Equal(["-y", "@modelcontextprotocol/server-github"], loaded.Arguments);
        await using var raw = await _db.OpenAsync(CancellationToken.None);
        await using var cmd = raw.CreateCommand();
        cmd.CommandText = "SELECT environment FROM mcp_servers;";
        Assert.DoesNotContain("secret-token", (string)(await cmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task NamesMustBeValidAndUnique()
    {
        var store = new SqliteMcpServerStore(_db, new EphemeralDataProtectionProvider(), NullLogger<SqliteMcpServerStore>.Instance);
        await store.SaveAsync(Server("files"));

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(Server("FILES")));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(Server("has space")));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(Server("remote", McpTransportKind.Http, command: null, url: "ftp://nope")));
    }

    [Fact]
    public void LocalServersRunThroughTheLoginShellWithoutButterKnifesVariables()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://localhost:5175");
        try
        {
            var options = McpHub.StdioOptions(Server(command: "npx", arguments: ["-y", "some server"], environment: new Dictionary<string, string> { ["TOKEN"] = "x" }));

            Assert.Equal(ShellCommand.UnixShell(), options.Command);
            Assert.Equal(["-l", "-c", "exec npx -y 'some server'"], options.Arguments);
            Assert.Null(options.EnvironmentVariables!["ASPNETCORE_URLS"]);
            Assert.Equal("x", options.EnvironmentVariables["TOKEN"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", null);
        }
    }

    [Fact]
    public async Task TheHubListsAndCallsToolsOnARealServer()
    {
        if (!CanRunStub)
        {
            return;
        }
        var store = new SqliteMcpServerStore(_db, new EphemeralDataProtectionProvider(), NullLogger<SqliteMcpServerStore>.Instance);
        var server = await store.SaveAsync(Server());
        await using var hub = new McpHub(store, new McpServerEvents(), NullLoggerFactory.Instance);

        var found = Assert.Single(await hub.GetToolsAsync([server.Id], CancellationToken.None));

        Assert.Null(found.Error);
        Assert.Equal(["stub__echo", "stub__add", "stub__picture", "stub__fail"], found.Tools.Select(t => t.QualifiedName));
        Assert.True(found.EstimatedTokens > 0);
        var tools = found.Tools.ToDictionary(t => t.ToolName);

        var echo = await tools["echo"].CallAsync(JsonDocument.Parse("""{"text":"hello"}""").RootElement, CancellationToken.None);
        Assert.Equal(("hello", false), (echo.Output, echo.IsError));

        var picture = await tools["picture"].CallAsync(JsonDocument.Parse("{}").RootElement, CancellationToken.None);
        Assert.Equal("image/png", Assert.Single(picture.Images).MediaType);

        var fail = await tools["fail"].CallAsync(JsonDocument.Parse("{}").RootElement, CancellationToken.None);
        Assert.True(fail.IsError);
    }

    [Fact]
    public async Task AServerThatCannotStartIsReportedNotThrown()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var store = new SqliteMcpServerStore(_db, new EphemeralDataProtectionProvider(), NullLogger<SqliteMcpServerStore>.Instance);
        await using var hub = new McpHub(store, new McpServerEvents(), NullLoggerFactory.Instance);

        var result = await hub.TestAsync(Server(command: "definitely-not-a-command-butterknife"), CancellationToken.None);

        Assert.Empty(result.Tools);
        Assert.NotNull(result.Error);
    }
    [Fact]
    public void ImportReadsClaudeDesktopAndClaudeCodeConfigs()
    {
        var result = McpImport.Parse("""
            {
              "mcpServers": {
                "file system": { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem", "/tmp"], "env": { "DEBUG": "1" }, "cwd": "/tmp" },
                "remote": { "type": "http", "url": "https://example.test/mcp", "headers": { "Authorization": "Bearer x" } },
                "broken": { "type": "stdio" }
              },
            }
            """);

        Assert.Equal(["file-system", "remote"], result.Servers.Select(s => s.Name));
        var local = result.Servers[0];
        Assert.Equal((McpTransportKind.Stdio, "npx", "/tmp"), (local.Transport, local.Command, local.WorkingDirectory));
        Assert.Equal(["-y", "@modelcontextprotocol/server-filesystem", "/tmp"], local.Arguments);
        Assert.Equal("1", local.Environment["DEBUG"]);
        var remote = result.Servers[1];
        Assert.Equal((McpTransportKind.Http, "https://example.test/mcp", "Bearer x"), (remote.Transport, remote.Url, remote.Headers["Authorization"]));
        Assert.Contains(result.Problems, p => p.StartsWith("broken", StringComparison.Ordinal));
    }

    [Fact]
    public void ImportAcceptsTheInnerMapOrOneServerAndReportsBadJson()
    {
        Assert.Equal(["git"], McpImport.Parse("""{ "git": { "command": "uvx", "args": ["mcp-server-git"] } }""").Servers.Select(s => s.Name));
        Assert.Equal("uvx", Assert.Single(McpImport.Parse("""{ "command": "uvx" }""").Servers).Command);
        Assert.Empty(McpImport.Parse("{ nope").Servers);
        Assert.NotEmpty(McpImport.Parse("{ nope").Problems);
    }

    [Fact]
    public void FormPairsParseAndReportMissingSeparators()
    {
        var env = McpImport.ParsePairs("TOKEN=a=b\n\nDEBUG = 1\n", '=', out var problem);
        Assert.Null(problem);
        Assert.Equal(new Dictionary<string, string> { ["TOKEN"] = "a=b", ["DEBUG"] = "1" }, env);
        Assert.Equal("Authorization: Bearer x", McpImport.FormatPairs(McpImport.ParsePairs("Authorization: Bearer x", ':', out _)!, ':'));
        Assert.Null(McpImport.ParsePairs("no separator", '=', out problem));
        Assert.Contains("KEY=value", problem);
    }
}
