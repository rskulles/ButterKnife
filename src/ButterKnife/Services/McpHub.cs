using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ButterKnife.Data;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ButterKnife.Services;

/// <summary>
/// A tool from an MCP server, ready for the Code tab: offered to the model as <see cref="Definition"/> under
/// <see cref="QualifiedName"/> (<c>server__tool</c>), and run through <see cref="CallAsync"/>.
/// </summary>
public sealed record McpTool(Guid ServerId, string ServerName, string ToolName, string QualifiedName, string Description, ToolDefinition Definition)
{
    /// <summary>Runs the tool with the model's arguments (a JSON object); never throws for a failure the model should see.</summary>
    public Func<JsonElement, CancellationToken, Task<ToolResult>> CallAsync { get; init; } = (_, _) => Task.FromResult(ToolResult.Fail("This tool is not connected."));
}

/// <summary>What a server offers, or why it could not be reached (<see cref="Error"/>).</summary>
public sealed record McpServerTools(McpServer Server, IReadOnlyList<McpTool> Tools, string? Error)
{
    /// <summary>Roughly what the tools' descriptions and schemas add to every request, in tokens.</summary>
    public int EstimatedTokens => Tools.Sum(t => TokenEstimator.Estimate(t.QualifiedName + t.Description + t.Definition.Parameters.GetRawText()));
}

/// <summary>
/// The Code tab's MCP client. Servers are connected the first time a turn needs them and kept running; settings changes
/// (<see cref="McpServerEvents"/>) restart them, and host shutdown stops them. Local (stdio) servers run through the
/// user's login shell, so <c>npx</c>, <c>uvx</c> and friends are found even when ButterKnife was opened from the Dock,
/// without ButterKnife's own environment. A server that fails is reported, never fatal: the turn carries on without it.
/// </summary>
public sealed partial class McpHub : IAsyncDisposable
{
    /// <summary>Longest tool description passed on: small models pay for every word on every request.</summary>
    public const int MaxDescriptionChars = 600;

    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);

    private readonly IMcpServerStore _store;
    private readonly McpServerEvents _events;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConcurrentDictionary<Guid, Lazy<Task<Live>>> _live = new();

    public McpHub(IMcpServerStore store, McpServerEvents events, ILoggerFactory loggerFactory)
    {
        _store = store;
        _events = events;
        _loggerFactory = loggerFactory;
        _events.Changed += OnServersChanged;
    }

    /// <summary>The tools of the given servers (disabled or deleted ones are skipped), connecting any that are not running.</summary>
    public async Task<IReadOnlyList<McpServerTools>> GetToolsAsync(IEnumerable<Guid> serverIds, CancellationToken cancellationToken)
    {
        var result = new List<McpServerTools>();
        foreach (var id in serverIds.Distinct())
        {
            if (await _store.GetAsync(id, cancellationToken) is not { Enabled: true } server)
            {
                continue;
            }
            try
            {
                var live = await _live.GetOrAdd(id, _ => new Lazy<Task<Live>>(() => ConnectAsync(server))).Value.WaitAsync(cancellationToken);
                result.Add(new McpServerTools(server, live.Tools, null));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await DropAsync(id);
                result.Add(new McpServerTools(server, [], Describe(ex)));
            }
        }
        return result;
    }

    /// <summary>Connects to a server as configured (saved or not), lists its tools and disconnects: the Settings Test button.</summary>
    public async Task<McpServerTools> TestAsync(McpServer server, CancellationToken cancellationToken)
    {
        try
        {
            await using var live = await ConnectAsync(server).WaitAsync(cancellationToken);
            return new McpServerTools(server, live.Tools, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new McpServerTools(server, [], Describe(ex));
        }
    }

    // ---------------------------------------------------------------- connections

    private sealed class Live(McpClient client, McpServer server, StderrTail stderr) : IAsyncDisposable
    {
        public McpClient Client { get; } = client;
        public McpServer Server { get; } = server;
        public StderrTail Stderr { get; } = stderr;
        public IReadOnlyList<McpTool> Tools { get; set; } = [];

        public ValueTask DisposeAsync() => Client.DisposeAsync();
    }

    /// <summary>The last few lines a local server printed to stderr: what usually explains why it would not start.</summary>
    private sealed class StderrTail
    {
        private readonly Queue<string> _lines = new();

        public void Add(string line)
        {
            lock (_lines)
            {
                _lines.Enqueue(line);
                while (_lines.Count > 8)
                {
                    _lines.Dequeue();
                }
            }
        }

        public string Text
        {
            get
            {
                lock (_lines)
                {
                    return string.Join('\n', _lines);
                }
            }
        }
    }

    private async Task<Live> ConnectAsync(McpServer server)
    {
        var stderr = new StderrTail();
        IClientTransport transport = server.Transport == McpTransportKind.Stdio
            ? new StdioClientTransport(StdioOptions(server, stderr.Add), _loggerFactory)
            : new HttpClientTransport(new HttpClientTransportOptions
            {
                Name = server.Name,
                Endpoint = new Uri(server.Url!),
                AdditionalHeaders = server.Headers.ToDictionary(),
                TransportMode = HttpTransportMode.AutoDetect,
            }, _loggerFactory);

        using var timeout = new CancellationTokenSource(StartTimeout); // starting and listing the tools together
        McpClient client;
        try
        {
            client = await McpClient.CreateAsync(transport, new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "ButterKnife", Version = UpdateChecker.CurrentVersion.ToString() },
                InitializationTimeout = StartTimeout,
            }, _loggerFactory, timeout.Token);
        }
        catch (Exception ex) when (stderr.Text.Length > 0 && !ex.ToString().Contains(stderr.Text.Split('\n')[^1], StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{ex.Message}\nThe server printed:\n{stderr.Text}", ex);
        }

        var live = new Live(client, server, stderr);
        try
        {
            var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
            live.Tools = tools.Select(t => ToTool(server, t.ProtocolTool, live)).ToArray();
        }
        catch
        {
            await live.DisposeAsync();
            throw;
        }
        return live;
    }

    /// <summary>A local server's process: the command through the login shell (PATH as in Terminal), ButterKnife's own variables removed.</summary>
    public static StdioClientTransportOptions StdioOptions(McpServer server, Action<string>? stderr = null)
    {
        var environment = new Dictionary<string, string?>();
        foreach (var key in System.Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(ShellCommand.IsButterKnifeVariable))
        {
            environment[key] = null; // null removes an inherited variable
        }
        foreach (var (key, value) in server.Environment)
        {
            environment[key] = value;
        }

        var options = new StdioClientTransportOptions
        {
            Name = server.Name,
            EnvironmentVariables = environment,
            WorkingDirectory = server.WorkingDirectory ?? System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            StandardErrorLines = stderr,
            Command = OperatingSystem.IsWindows() ? "cmd.exe" : ShellCommand.UnixShell(),
            Arguments = OperatingSystem.IsWindows()
                ? ["/c", server.Command!, .. server.Arguments] // npx and friends are .cmd files there
                : ["-l", "-c", "exec " + string.Join(' ', new[] { server.Command! }.Concat(server.Arguments).Select(ShellQuote))],
        };
        return options;
    }

    private static string ShellQuote(string value) =>
        value.Length > 0 && SafeShellWord().IsMatch(value) ? value : "'" + value.Replace("'", "'\\''") + "'";

    private void OnServersChanged()
    {
        foreach (var id in _live.Keys)
        {
            _ = DropAsync(id); // the next turn reconnects with the new settings
        }
    }

    private async Task DropAsync(Guid id)
    {
        if (_live.TryRemove(id, out var lazy) && lazy.IsValueCreated)
        {
            try
            {
                await (await lazy.Value).DisposeAsync();
            }
            catch (Exception)
            {
                // it failed to start, or is already gone
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _events.Changed -= OnServersChanged;
        foreach (var id in _live.Keys)
        {
            await DropAsync(id);
        }
    }

    // ---------------------------------------------------------------- tools

    private McpTool ToTool(McpServer server, Tool tool, Live live)
    {
        var name = McpToolNames.Qualify(server.Name, tool.Name);
        var description = (tool.Description ?? tool.Title ?? "").Trim();
        if (description.Length > MaxDescriptionChars)
        {
            description = description[..MaxDescriptionChars].TrimEnd() + "…";
        }
        var full = $"[{server.Name}] {(description.Length > 0 ? description : tool.Name)}";
        return new McpTool(server.Id, server.Name, tool.Name, name, full, new ToolDefinition(name, full, McpToolNames.ObjectSchema(tool.InputSchema)))
        {
            CallAsync = (arguments, ct) => CallAsync(live, tool.Name, arguments, ct),
        };
    }

    private async Task<ToolResult> CallAsync(Live live, string toolName, JsonElement arguments, CancellationToken cancellationToken)
    {
        var args = arguments.ValueKind == JsonValueKind.Object
            ? arguments.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone())
            : new Dictionary<string, object?>();
        try
        {
            var result = await live.Client.CallToolAsync(toolName, args, cancellationToken: cancellationToken);
            return ToResult(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ModelContextProtocol.McpException ex)
        {
            return ToolResult.Fail($"{live.Server.Name}: {ex.Message}"); // the server answered with an error
        }
        catch (Exception ex)
        {
            await DropAsync(live.Server.Id); // the connection is broken; the next turn starts the server again
            var printed = live.Stderr.Text;
            return ToolResult.Fail($"The {live.Server.Name} MCP server failed: {ex.Message}{(printed.Length > 0 ? $"\nIt printed:\n{printed}" : "")}");
        }
    }

    /// <summary>Text blocks become the output, pictures go to <see cref="ToolResult.Images"/>, anything else is named.</summary>
    public static ToolResult ToResult(CallToolResult result)
    {
        var text = new StringBuilder();
        var images = new List<ChatImage>();
        foreach (var block in result.Content)
        {
            switch (block)
            {
                case TextContentBlock t:
                    text.Append(t.Text).Append('\n');
                    break;
                case ImageContentBlock i when ChatImage.SupportedMediaTypes.Contains(i.MimeType):
                    images.Add(new ChatImage(i.MimeType, i.DecodedData.ToArray()));
                    break;
                case ImageContentBlock i:
                    text.Append($"[a picture of type {i.MimeType}, which cannot be shown]\n");
                    break;
                case ResourceLinkBlock link:
                    text.Append($"[resource: {link.Name} {link.Uri}]\n");
                    break;
                case EmbeddedResourceBlock { Resource: TextResourceContents r }:
                    text.Append($"[resource {r.Uri}]\n{r.Text}\n");
                    break;
                default:
                    text.Append($"[{block.Type} content]\n");
                    break;
            }
        }
        if (text.Length == 0 && result.StructuredContent is { } structured)
        {
            text.Append(structured.GetRawText());
        }
        var output = text.Length == 0 ? (images.Count > 0 ? $"{images.Count} picture(s)." : "(no output)") : ShellCommand.Clip(text.ToString().TrimEnd(), ShellCommand.MaxOutputChars).Text;
        return new ToolResult(output, result.IsError == true) { Images = images };
    }

    private static string Describe(Exception ex) => ex is TimeoutException or OperationCanceledException
        ? $"It did not answer within {StartTimeout.TotalSeconds:0} seconds."
        : ex.GetBaseException() is var inner && (inner == ex || ex.Message.Contains(inner.Message, StringComparison.Ordinal)) ? ex.Message
        : $"{ex.Message} ({inner.Message})";

    [GeneratedRegex(@"^[A-Za-z0-9_./:=@%+,-]+$")]
    private static partial Regex SafeShellWord();
}

/// <summary>Tool names and schemas as every backend accepts them.</summary>
public static partial class McpToolNames
{
    /// <summary>OpenAI's limit, the strictest: ^[a-zA-Z0-9_-]{1,64}$.</summary>
    public const int MaxLength = 64;

    public const string Separator = "__";

    /// <summary>"server__tool", with anything else made safe and an over-long name shortened with a short hash so it stays unique.</summary>
    public static string Qualify(string server, string tool)
    {
        var name = Unsafe().Replace($"{server}{Separator}{tool}", "_");
        if (name.Length <= MaxLength)
        {
            return name;
        }
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..6].ToLowerInvariant();
        return name[..(MaxLength - 7)] + "_" + hash;
    }

    /// <summary>Whether a call's name looks like an MCP tool's (for labels of stored calls).</summary>
    public static bool TrySplit(string qualified, out string server, out string tool)
    {
        var at = qualified.IndexOf(Separator, StringComparison.Ordinal);
        server = at > 0 ? qualified[..at] : "";
        tool = at > 0 ? qualified[(at + Separator.Length)..] : "";
        return at > 0 && tool.Length > 0;
    }

    /// <summary>The tool's input schema as a JSON object schema (some servers omit "type" or "properties", which strict backends reject).</summary>
    public static JsonElement ObjectSchema(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();
        }
        if (schema.TryGetProperty("type", out _) && schema.TryGetProperty("properties", out _))
        {
            return schema.Clone();
        }
        var node = System.Text.Json.Nodes.JsonNode.Parse(schema.GetRawText())!.AsObject();
        node["type"] ??= "object";
        node["properties"] ??= new System.Text.Json.Nodes.JsonObject();
        return JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();
    }

    [GeneratedRegex("[^A-Za-z0-9_-]")]
    private static partial Regex Unsafe();
}
