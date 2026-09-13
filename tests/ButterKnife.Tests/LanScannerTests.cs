using System.Net;
using System.Net.Sockets;
using System.Text;
using ButterKnife.Options;
using ButterKnife.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ButterKnife.Tests;

public sealed class LanScannerTests
{
    private static LanScanner Make(StubHandler handler) => new(new StubClientFactory(handler, "http://unused"), NullLogger<LanScanner>.Instance);

    [Fact]
    public void SubnetAddressesCoverTheBlockWithoutSelfNetworkOrBroadcast()
    {
        var addresses = LanScanner.SubnetAddresses(IPAddress.Parse("10.0.0.242"), 24);

        Assert.Equal(253, addresses.Count);
        Assert.DoesNotContain(IPAddress.Parse("10.0.0.242"), addresses);
        Assert.DoesNotContain(IPAddress.Parse("10.0.0.0"), addresses);
        Assert.DoesNotContain(IPAddress.Parse("10.0.0.255"), addresses);
        Assert.Contains(IPAddress.Parse("10.0.0.1"), addresses);
        Assert.Contains(IPAddress.Parse("10.0.0.254"), addresses);
    }

    [Fact]
    public void SubnetAddressesCapAt24AndRespectSmallerBlocks()
    {
        Assert.Equal(253, LanScanner.SubnetAddresses(IPAddress.Parse("192.168.5.7"), 16).Count);

        var small = LanScanner.SubnetAddresses(IPAddress.Parse("10.0.0.242"), 28); // 10.0.0.240/28: .241 to .254
        Assert.Equal(13, small.Count);
        Assert.Contains(IPAddress.Parse("10.0.0.241"), small);
        Assert.Contains(IPAddress.Parse("10.0.0.254"), small);
        Assert.DoesNotContain(IPAddress.Parse("10.0.0.239"), small);

        Assert.Empty(LanScanner.SubnetAddresses(IPAddress.Parse("8.8.8.8"), 24));
        Assert.Empty(LanScanner.SubnetAddresses(IPAddress.IPv6Loopback, 64));
    }

    [Theory]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.1.1", true)]
    [InlineData("192.169.1.1", false)]
    [InlineData("1.2.3.4", false)]
    public void IsPrivateKnowsTheThreeRanges(string ip, bool expected) => Assert.Equal(expected, LanScanner.IsPrivate(IPAddress.Parse(ip)));

    [Fact]
    public async Task IdentifiesOllama()
    {
        var scanner = Make(StubHandler.Route(new Dictionary<string, (string, string)>
        {
            ["GET /api/tags"] = ("""{"models":[{"name":"llama3:8b"},{"name":"qwen3:4b"}]}""", "application/json"),
        }));

        var found = await scanner.IdentifyAsync("10.0.0.22", 11434, CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(BackendKind.Ollama, found.Kind);
        Assert.Equal("Ollama", found.Name);
        Assert.Equal("http://10.0.0.22:11434", found.BaseUrl);
        Assert.Equal(["llama3:8b", "qwen3:4b"], found.Models);
    }

    [Fact]
    public async Task TellsLmStudioLlamaCppAndGenericServersApart()
    {
        var models = ("""{"data":[{"id":"qwen/qwen3-8b"}]}""", "application/json");

        var lmStudio = await Make(StubHandler.Route(new Dictionary<string, (string, string)>
        {
            ["GET /v1/models"] = models,
            ["GET /api/v0/models"] = ("""{"data":[]}""", "application/json"),
        })).IdentifyAsync("box", 1234, CancellationToken.None);
        Assert.Equal(("LM Studio", "http://box:1234/v1", BackendKind.OpenAiCompatible), (lmStudio!.Name, lmStudio.BaseUrl, lmStudio.Kind));
        Assert.Equal(["qwen/qwen3-8b"], lmStudio.Models);

        var llamaCpp = await Make(StubHandler.Route(new Dictionary<string, (string, string)>
        {
            ["GET /v1/models"] = models,
            ["GET /props"] = ("""{"default_generation_settings":{}}""", "application/json"),
        })).IdentifyAsync("box", 8080, CancellationToken.None);
        Assert.Equal("llama.cpp server", llamaCpp!.Name);

        var generic = await Make(StubHandler.Route(new Dictionary<string, (string, string)> { ["GET /v1/models"] = models }))
            .IdentifyAsync("box", 8000, CancellationToken.None);
        Assert.Equal("OpenAI-compatible server", generic!.Name);
    }

    [Fact]
    public async Task RecognisesWhisperCppAndIgnoresStrangers()
    {
        var whisper = await Make(StubHandler.Route(new Dictionary<string, (string, string)>
        {
            ["GET /"] = ("<html><title>Whisper.cpp Server</title></html>", "text/html"),
        })).IdentifyAsync("box", 8080, CancellationToken.None);
        Assert.Equal(BackendKind.Transcription, whisper!.Kind);
        Assert.Equal("http://box:8080", whisper.BaseUrl);

        var printer = await Make(StubHandler.Route(new Dictionary<string, (string, string)>
        {
            ["GET /"] = ("<html>Printer status</html>", "text/html"),
        })).IdentifyAsync("box", 8080, CancellationToken.None);
        Assert.Null(printer);

        var nothing = await Make(StubHandler.Route(new Dictionary<string, (string, string)>())).IdentifyAsync("box", 1234, CancellationToken.None);
        Assert.Null(nothing);
    }

    [Fact]
    public async Task ProbeSkipsClosedPortsAndIdentifiesOpenOnes()
    {
        // A tiny HTTP server that claims to be Ollama, and a port nothing listens on.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var openPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serve = Task.Run(async () =>
        {
            while (true)
            {
                using var socket = await listener.AcceptTcpClientAsync();
                using var stream = socket.GetStream();
                var buffer = new byte[4096];
                _ = await stream.ReadAsync(buffer);
                var body = """{"models":[{"name":"tiny"}]}""";
                var response = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
            }
        });

        int closedPort;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            closedPort = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }

        var scanner = new LanScanner(new RealClientFactory(), NullLogger<LanScanner>.Instance);
        var reports = new List<ScanProgress>();
        var progress = new SyncProgress(reports.Add);
        var targets = new List<ScanTarget>
        {
            new("127.0.0.1", IPAddress.Loopback, closedPort),
            new("localhost", IPAddress.Loopback, openPort),
        };

        var found = await scanner.ProbeAsync(targets, progress, CancellationToken.None);

        var server = Assert.Single(found);
        Assert.Equal(BackendKind.Ollama, server.Kind);
        Assert.Equal($"http://localhost:{openPort}", server.BaseUrl);
        Assert.Equal(["tiny"], server.Models);
        Assert.Equal(2, reports.Count);
        Assert.Contains(new ScanProgress(2, 2), reports);
        listener.Stop();
        _ = serve;
    }

    private sealed class SyncProgress(Action<ScanProgress> handler) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value)
        {
            lock (handler)
            {
                handler(value);
            }
        }
    }

    private sealed class RealClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new() { Timeout = TimeSpan.FromSeconds(3) };
    }
}
