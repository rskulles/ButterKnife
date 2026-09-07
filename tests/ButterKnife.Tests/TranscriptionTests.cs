using System.Net;
using ButterKnife.Data;
using ButterKnife.Options;
using ButterKnife.Services;

namespace ButterKnife.Tests;

public class TranscriptionTests
{
    private static LlmConnection Whisper(string? model = null, string? apiKey = null) =>
        TestConnections.Make("Whisper", BackendKind.Transcription, "http://whisper.test:8000/v1", model, apiKey);

    [Fact]
    public async Task PostsMultipartWithFileAndModel_AndReturnsText()
    {
        var handler = StubHandler.Text("""{"text":"  hello from whisper "}""");
        var client = new TranscriptionClient(new StubClientFactory(handler, "http://unused/"));
        using var audio = new MemoryStream([1, 2, 3, 4]);

        var text = await client.TranscribeAsync(Whisper("Systran/faster-whisper-small", "sk"), audio, "recording.webm", "audio/webm", null, CancellationToken.None);

        Assert.Equal("hello from whisper", text);
        Assert.Equal("http://whisper.test:8000/v1/audio/transcriptions", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
        Assert.StartsWith("multipart/form-data", handler.LastRequest.Content!.Headers.ContentType!.ToString());
        var body = handler.LastRequestBody!;
        Assert.Contains("name=file; filename=recording.webm", body.Replace("\"", ""));
        Assert.Contains("Content-Type: audio/webm", body);
        Assert.Contains("name=model", body.Replace("\"", ""));
        Assert.Contains("Systran/faster-whisper-small", body);
        Assert.Contains("response_format", body);
    }

    [Fact]
    public async Task UsesDefaultModelAndSurfacesHttpErrors()
    {
        var ok = StubHandler.Text("""{"text":"x"}""");
        await new TranscriptionClient(new StubClientFactory(ok, "http://unused/"))
            .TranscribeAsync(Whisper(), new MemoryStream([1]), "r.webm", "audio/webm", null, CancellationToken.None);
        Assert.Contains(TranscriptionClient.DefaultModel, ok.LastRequestBody!);

        var bad = StubHandler.Text("""{"error":"model not found"}""", HttpStatusCode.NotFound);
        var ex = await Assert.ThrowsAsync<LlmException>(() => new TranscriptionClient(new StubClientFactory(bad, "http://unused/"))
            .TranscribeAsync(Whisper(), new MemoryStream([1]), "r.webm", "audio/webm", null, CancellationToken.None));
        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);

        await Assert.ThrowsAsync<InvalidOperationException>(() => new TranscriptionClient(new StubClientFactory(ok, "http://unused/"))
            .TranscribeAsync(TestConnections.Make("Ollama", BackendKind.Ollama, "http://o/"), new MemoryStream([1]), "r.webm", "audio/webm", null, CancellationToken.None));
    }

    [Fact]
    public async Task FallsBackToWhisperCppInferenceOn404_AndRemembersDialect()
    {
        var calls = new List<string>();
        var handler = new StubHandler(async (request, ct) =>
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            calls.Add($"{request.RequestUri!.AbsolutePath} model={body.Replace("\"", "").Contains("name=model")}");
            return request.RequestUri.AbsolutePath == "/inference"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"text":" from whisper.cpp "}""", System.Text.Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("File Not Found (/v1/audio/transcriptions)", System.Text.Encoding.UTF8, "text/plain") };
        });
        var client = new TranscriptionClient(new StubClientFactory(handler, "http://unused/"));
        var connection = TestConnections.Make("whisper.cpp", BackendKind.Transcription, "http://wcpp.test:8080/v1");

        var text = await client.TranscribeAsync(connection, new MemoryStream([1]), "r.wav", "audio/wav", null, CancellationToken.None);
        Assert.Equal("from whisper.cpp", text);
        Assert.Equal(["/v1/audio/transcriptions model=True", "/inference model=False"], calls);

        calls.Clear();
        await client.TranscribeAsync(connection, new MemoryStream([1]), "r.wav", "audio/wav", null, CancellationToken.None);
        Assert.Equal(["/inference model=False"], calls); // remembered: no second 404 round trip
    }

    [Fact]
    public void ServerRootStripsVersionPrefixOnly()
    {
        Assert.Equal("http://h:8080", TranscriptionClient.ServerRoot("http://h:8080/v1"));
        Assert.Equal("http://h:8080", TranscriptionClient.ServerRoot("http://h:8080/v1/"));
        Assert.Equal("http://h:8080", TranscriptionClient.ServerRoot("http://h:8080/"));
        Assert.Equal("http://h/api", TranscriptionClient.ServerRoot("http://h/api"));
    }

    [Fact]
    public async Task ProbeListsModelsOrReturnsEmpty()
    {
        var withModels = StubHandler.Text("""{"data":[{"id":"whisper-1"},{"id":"Systran/faster-whisper-small"}]}""");
        var models = await new TranscriptionClient(new StubClientFactory(withModels, "http://unused/")).ProbeAsync(Whisper(), CancellationToken.None);
        Assert.Equal(["whisper-1", "Systran/faster-whisper-small"], models);

        var notFound = StubHandler.Text("nope", HttpStatusCode.NotFound, "text/plain");
        Assert.Empty(await new TranscriptionClient(new StubClientFactory(notFound, "http://unused/")).ProbeAsync(Whisper(), CancellationToken.None));
    }

    [Fact]
    public async Task ServicePicksFirstTranscriptionConnection_AndRegistrySkipsIt()
    {
        var chat = TestConnections.Make("Ollama", BackendKind.Ollama, "http://o:11434");
        var whisper = Whisper();
        var store = new FakeStore(chat, whisper);
        var handler = StubHandler.Text("""{"text":"dictated"}""");
        var service = new TranscriptionService(store, new TranscriptionClient(new StubClientFactory(handler, "http://unused/")));

        Assert.Equal(whisper.Id, (await service.GetConnectionAsync(CancellationToken.None))!.Id);
        Assert.Equal("dictated", await service.TranscribeAsync(new MemoryStream([1]), "audio/ogg;codecs=opus", CancellationToken.None));
        Assert.Contains("filename=recording.ogg", handler.LastRequestBody!.Replace("\"", ""));

        var registry = new LlmClientRegistry(store, new StubClientFactory(handler, "http://unused/"));
        Assert.Single(await registry.GetClientsAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.GetAsync(whisper.Id, CancellationToken.None));

        var none = new TranscriptionService(new FakeStore(chat), new TranscriptionClient(new StubClientFactory(handler, "http://unused/")));
        Assert.Null(await none.GetConnectionAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => none.TranscribeAsync(new MemoryStream([1]), "audio/webm", CancellationToken.None));
    }

    private sealed class FakeStore(params LlmConnection[] items) : IConnectionStore
    {
        public Task<IReadOnlyList<LlmConnection>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<LlmConnection>>(items);
        public Task<LlmConnection?> GetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(items.FirstOrDefault(c => c.Id == id));
        public Task<LlmConnection> CreateAsync(string name, BackendKind kind, string baseUrl, string? apiKey, string? defaultModel, int? contextWindow, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateAsync(Guid id, string name, BackendKind kind, string baseUrl, string? apiKey, string? defaultModel, int? contextWindow, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
