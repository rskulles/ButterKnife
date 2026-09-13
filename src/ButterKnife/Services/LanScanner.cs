using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using ButterKnife.Options;

namespace ButterKnife.Services;

/// <summary>A model server that answered during a scan, ready to become a connection.</summary>
public sealed record FoundServer(string Host, int Port, BackendKind Kind, string Name, string BaseUrl, IReadOnlyList<string> Models);

public readonly record struct ScanProgress(int Done, int Total);

/// <summary>One address and port to try; <see cref="Host"/> is what goes into the base URL (a name when we have one).</summary>
public readonly record struct ScanTarget(string Host, IPAddress Address, int Port);

/// <summary>
/// "Find servers on my network": tries the usual model-server ports on every address of the private /24 (or
/// smaller) block this machine sits in, plus localhost and a couple of conventional names, and asks whatever
/// accepts a connection what it is. Only unauthenticated, read-only identification endpoints are hit; nothing
/// leaves the LAN. A /24 is about a thousand connects, done 64 at a time with a short timeout.
/// </summary>
public sealed class LanScanner(IHttpClientFactory httpClientFactory, ILogger<LanScanner> logger)
{
    public const string HttpClientName = "lan-scan";

    /// <summary>Ollama, LM Studio, llama.cpp server (and whisper.cpp), vLLM.</summary>
    public static readonly IReadOnlyList<int> Ports = [11434, 1234, 8080, 8000];

    public static readonly IReadOnlyList<string> NamedGuesses = ["ollama.local", "lmstudio.local"];

    /// <summary>Never enumerate more than a /24, whatever the interface says.</summary>
    public const int MaxPrefixLength = 24;

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(1);
    private const int MaxParallel = 64;

    public async Task<IReadOnlyList<FoundServer>> ScanAsync(IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        var targets = await TargetsAsync(cancellationToken);
        return await ProbeAsync(targets, progress, cancellationToken);
    }

    /// <summary>Loopback, the conventional names that resolve, then every host address of each private block this machine is on.</summary>
    public async Task<IReadOnlyList<ScanTarget>> TargetsAsync(CancellationToken cancellationToken)
    {
        var targets = new List<ScanTarget>();
        var seen = new HashSet<(IPAddress, int)>();

        void Add(string host, IPAddress address, int port)
        {
            if (seen.Add((address, port)))
            {
                targets.Add(new ScanTarget(host, address, port));
            }
        }

        foreach (var port in Ports)
        {
            Add("localhost", IPAddress.Loopback, port);
        }

        foreach (var name in NamedGuesses)
        {
            if (await ResolveAsync(name, cancellationToken) is { } address)
            {
                foreach (var port in Ports)
                {
                    Add(name, address, port);
                }
            }
        }

        foreach (var (address, prefixLength) in LocalInterfaces())
        {
            foreach (var candidate in SubnetAddresses(address, prefixLength))
            {
                foreach (var port in Ports)
                {
                    Add(candidate.ToString(), candidate, port);
                }
            }
        }

        return targets;
    }

    /// <summary>Connects to every target (bounded parallelism), identifies the ones that answer, and reports progress per target.</summary>
    public async Task<IReadOnlyList<FoundServer>> ProbeAsync(IReadOnlyList<ScanTarget> targets, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        var found = new List<FoundServer>();
        var done = 0;
        using var gate = new SemaphoreSlim(MaxParallel);

        var tasks = targets.Select(async target =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                if (await IsOpenAsync(target.Address, target.Port, cancellationToken)
                    && await IdentifyAsync(target.Host, target.Port, cancellationToken) is { } server)
                {
                    lock (found)
                    {
                        found.Add(server);
                    }
                }
            }
            finally
            {
                gate.Release();
                progress?.Report(new ScanProgress(Interlocked.Increment(ref done), targets.Count));
            }
        });
        await Task.WhenAll(tasks);

        // Named and loopback hits first (they come first in the target list), then by address and port.
        var order = targets.Select((t, i) => (t.Host, t.Port, Index: i)).ToDictionary(t => (t.Host, t.Port), t => t.Index);
        return found.OrderBy(f => order.GetValueOrDefault((f.Host, f.Port), int.MaxValue)).ToArray();
    }

    /// <summary>
    /// Asks an open port what it is: Ollama answers /api/tags; OpenAI-style servers answer /v1/models, and LM Studio
    /// (/api/v0/models) and llama.cpp (/props) can be told apart; whisper.cpp's index page names itself. Null when
    /// nothing recognisable answers (a web server, a printer, something else on that port).
    /// </summary>
    public async Task<FoundServer?> IdentifyAsync(string host, int port, CancellationToken cancellationToken)
    {
        var root = $"http://{host}:{port}";
        using var client = httpClientFactory.CreateClient(HttpClientName);

        try
        {
            if (await GetJsonAsync(client, $"{root}/api/tags", cancellationToken) is { } tags
                && tags.RootElement.TryGetProperty("models", out var ollamaModels) && ollamaModels.ValueKind == JsonValueKind.Array)
            {
                var names = ollamaModels.EnumerateArray().Select(m => m.TryGetProperty("name", out var n) ? n.GetString() : null).OfType<string>().ToArray();
                return new FoundServer(host, port, BackendKind.Ollama, "Ollama", root, names);
            }

            if (await GetJsonAsync(client, $"{root}/v1/models", cancellationToken) is { } models
                && models.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                var ids = data.EnumerateArray().Select(m => m.TryGetProperty("id", out var id) ? id.GetString() : null).OfType<string>().ToArray();
                var name = await GetJsonAsync(client, $"{root}/api/v0/models", cancellationToken) is not null ? "LM Studio"
                    : await GetJsonAsync(client, $"{root}/props", cancellationToken) is not null ? "llama.cpp server"
                    : "OpenAI-compatible server";
                return new FoundServer(host, port, BackendKind.OpenAiCompatible, name, $"{root}/v1", ids);
            }

            if (await GetTextAsync(client, $"{root}/", cancellationToken) is { } index && index.Contains("whisper", StringComparison.OrdinalIgnoreCase))
            {
                return new FoundServer(host, port, BackendKind.Transcription, "Whisper (speech to text)", root, []);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not identify {Host}:{Port}", host, port);
        }
        return null;
    }

    /// <summary>The private IPv4 addresses this machine has, with their prefix lengths.</summary>
    public static IEnumerable<(IPAddress Address, int PrefixLength)> LocalInterfaces()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork && IsPrivate(unicast.Address))
                {
                    yield return (unicast.Address, unicast.PrefixLength);
                }
            }
        }
    }

    /// <summary>
    /// The host addresses in <paramref name="local"/>'s block, capped at a /24 around it, without the network and
    /// broadcast addresses and without the machine itself. Empty for anything but a private IPv4 address.
    /// </summary>
    public static IReadOnlyList<IPAddress> SubnetAddresses(IPAddress local, int prefixLength)
    {
        if (local.AddressFamily != AddressFamily.InterNetwork || !IsPrivate(local))
        {
            return [];
        }

        var prefix = Math.Clamp(prefixLength, MaxPrefixLength, 32);
        var bytes = local.GetAddressBytes();
        var value = (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
        var mask = prefix == 32 ? uint.MaxValue : ~(uint.MaxValue >> prefix);
        var network = value & mask;
        var broadcast = network | ~mask;

        var list = new List<IPAddress>();
        for (var candidate = network + 1; candidate < broadcast; candidate++)
        {
            if (candidate != value)
            {
                list.Add(new IPAddress([(byte)(candidate >> 24), (byte)(candidate >> 16), (byte)(candidate >> 8), (byte)candidate]));
            }
        }
        return list;
    }

    /// <summary>10/8, 172.16/12 and 192.168/16.</summary>
    public static bool IsPrivate(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }
        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }

    private static async Task<bool> IsOpenAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient(address.AddressFamily);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);
        try
        {
            await client.ConnectAsync(address, port, timeout.Token);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false; // refused, timed out, unreachable
        }
    }

    private static async Task<IPAddress?> ResolveAsync(string name, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ResolveTimeout);
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(name, AddressFamily.InterNetwork, timeout.Token);
            return addresses.FirstOrDefault(IsPrivate) ?? addresses.FirstOrDefault();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null; // no such name on this network
        }
    }

    private static async Task<JsonDocument?> GetJsonAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }
        try
        {
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<string?> GetTextAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        return text.Length > 65536 ? text[..65536] : text;
    }
}
