using System.Net;
using System.Text.Json;
using ButterKnife.Options;
using ButterKnife.Services;

namespace ButterKnife.Tests;

public sealed class ImageGenerationTests
{
    [Theory]
    [InlineData("/image a red bicycle", true, "a red bicycle")]
    [InlineData("  /IMG  a cat  ", true, "a cat")]
    [InlineData("/imagine dragons", true, "dragons")]
    [InlineData("/image", true, "")]
    [InlineData("/images of cats", false, "")]
    [InlineData("image a cat", false, "")]
    [InlineData("tell me about /image", false, "")]
    public void CommandParsing(string input, bool isCommand, string prompt)
    {
        Assert.Equal(isCommand, ImageCommand.TryParse(input, out var parsed));
        Assert.Equal(prompt, parsed);
    }

    [Fact]
    public void CommandParsingPullsStylesOutOfTheText()
    {
        Assert.True(ImageCommand.TryParse("/image a lighthouse --lora watercolor:0.8 at dusk --style sketch", out var prompt, out var styles));
        Assert.Equal("a lighthouse at dusk", prompt);
        Assert.Equal([new ImageStyle("watercolor", 0.8), new ImageStyle("sketch", 1.0)], styles);

        Assert.True(ImageCommand.TryParse("/image plain", out prompt, out styles));
        Assert.Equal("plain", prompt);
        Assert.Empty(styles);

        Assert.True(ImageCommand.TryParse("/image --lora=oil the sea", out prompt, out styles));
        Assert.Equal("the sea", prompt);
        Assert.Equal("oil", Assert.Single(styles).Name);
    }

    [Fact]
    public void CommandParsingPullsStrengthOutOfTheText()
    {
        Assert.True(ImageCommand.TryParse("/image make it night --strength 0.4 --lora sketch", out var prompt, out var styles, out var strength));
        Assert.Equal("make it night", prompt);
        Assert.Equal(0.4, strength);
        Assert.Equal("sketch", Assert.Single(styles).Name);

        Assert.True(ImageCommand.TryParse("/image plain --strength=7", out prompt, out _, out strength));
        Assert.Equal("plain", prompt);
        Assert.Equal(1.0, strength); // clamped

        Assert.True(ImageCommand.TryParse("/image no flag", out _, out _, out strength));
        Assert.Null(strength);
    }

    [Fact]
    public async Task GenerateSendsTheSourcePictureAndStrengthWithoutASize()
    {
        var handler = StubHandler.Text("{\"created\":1,\"data\":[{\"b64_json\":\"AQID\",\"seed\":1}],\"crayoncloud\":{\"source\":{\"width\":512,\"height\":384,\"strength\":0.4}}}");
        var client = new ImageGenerationClient(new StubClientFactory(handler, "http://crayon.test:8765/v1"));
        var connection = TestConnections.Make("Crayon Cloud", BackendKind.ImageGeneration, "http://crayon.test:8765/v1");
        var source = new ChatImage("image/png", [0x89, 0x50, 0x4E, 0x47]);

        var result = await client.GenerateAsync(connection, new ImageRequest("make it night", Source: source, Strength: 0.4), CancellationToken.None);

        var body = JsonDocument.Parse(handler.LastRequestBody!).RootElement;
        Assert.Equal(source.DataUrl, body.GetProperty("image").GetString());
        Assert.Equal(0.4, body.GetProperty("strength").GetDouble());
        Assert.False(body.TryGetProperty("size", out _)); // the picture sets the shape
        Assert.Equal(0.4, result.SourceStrength);

        await client.GenerateAsync(connection, new ImageRequest("plain"), CancellationToken.None);
        body = JsonDocument.Parse(handler.LastRequestBody!).RootElement;
        Assert.Equal("1024x1024", body.GetProperty("size").GetString());
        Assert.False(body.TryGetProperty("image", out _));
        Assert.False(body.TryGetProperty("strength", out _));
    }

    [Fact]
    public async Task GenerateSendsStylesAsLoras()
    {
        var handler = StubHandler.Text("{\"created\":1,\"data\":[{\"b64_json\":\"AQID\",\"seed\":1}]}");
        var client = new ImageGenerationClient(new StubClientFactory(handler, "http://crayon.test:8765/v1"));
        var connection = TestConnections.Make("Crayon Cloud", BackendKind.ImageGeneration, "http://crayon.test:8765/v1");

        await client.GenerateAsync(connection, new ImageRequest("x", Styles: [new ImageStyle("watercolor", 0.8)]), CancellationToken.None);
        var loras = JsonDocument.Parse(handler.LastRequestBody!).RootElement.GetProperty("loras");
        Assert.Equal("watercolor", loras[0].GetProperty("name").GetString());
        Assert.Equal(0.8, loras[0].GetProperty("scale").GetDouble());

        await client.GenerateAsync(connection, new ImageRequest("x"), CancellationToken.None);
        Assert.False(JsonDocument.Parse(handler.LastRequestBody!).RootElement.TryGetProperty("loras", out _));
    }

    [Fact]
    public async Task ListStylesReadsTheLorasRouteAndToleratesServersWithoutIt()
    {
        var connection = TestConnections.Make("Crayon Cloud", BackendKind.ImageGeneration, "http://crayon.test:8765/v1");
        var with = new ImageGenerationClient(new StubClientFactory(StubHandler.Text("""{"object":"list","data":[{"name":"watercolor"},{"name":"sketch"}]}"""), "http://crayon.test:8765/v1"));
        Assert.Equal(["watercolor", "sketch"], await with.ListStylesAsync(connection, CancellationToken.None));

        var without = new ImageGenerationClient(new StubClientFactory(StubHandler.Text("not found", HttpStatusCode.NotFound), "http://crayon.test:8765/v1"));
        Assert.Empty(await without.ListStylesAsync(connection, CancellationToken.None));
    }

    [Fact]
    public void ImageConnectionsAreNotChatBackends()
    {
        Assert.False(LlmClientFactory.IsChatBackend(BackendKind.ImageGeneration));
        Assert.True(LlmClientFactory.IsChatBackend(BackendKind.Ollama));
    }

    [Fact]
    public async Task GeneratePostsTheOpenAiShapeAndReadsThePngBack()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        var handler = StubHandler.Text("{\"created\":1,\"data\":[{\"b64_json\":\"" + Convert.ToBase64String(png) + "\",\"seed\":42}],\"crayoncloud\":{\"model\":\"z-image-turbo\",\"seconds\":9.5}}");
        var client = new ImageGenerationClient(new StubClientFactory(handler, "http://crayon.test:8765/v1"));
        var connection = TestConnections.Make("Crayon Cloud", BackendKind.ImageGeneration, "http://crayon.test:8765/v1", apiKey: "secret");

        var result = await client.GenerateAsync(connection, new ImageRequest("a cloud raining rgb", 768, 512, Steps: 6, Seed: 42), CancellationToken.None);

        Assert.Equal(png, result.Png);
        Assert.Equal(42, result.Seed);
        Assert.Equal(9.5, result.Seconds);
        Assert.Equal("z-image-turbo", result.Model);
        Assert.Equal("http://crayon.test:8765/v1/images/generations", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("Bearer secret", handler.LastRequest.Headers.Authorization!.ToString());
        var body = JsonDocument.Parse(handler.LastRequestBody!).RootElement;
        Assert.Equal("a cloud raining rgb", body.GetProperty("prompt").GetString());
        Assert.Equal("768x512", body.GetProperty("size").GetString());
        Assert.Equal("b64_json", body.GetProperty("response_format").GetString());
        Assert.Equal(6, body.GetProperty("steps").GetInt32());
        Assert.Equal(42, body.GetProperty("seed").GetInt32());
        Assert.Equal(1, body.GetProperty("n").GetInt32());
        Assert.False(body.TryGetProperty("model", out _));
    }

    [Fact]
    public async Task GenerateSurfacesServerErrorsAndEmptyAnswers()
    {
        var failing = new ImageGenerationClient(new StubClientFactory(StubHandler.Text("""{"detail":"generation failed: out of memory"}""", HttpStatusCode.InternalServerError), "http://crayon.test:8765/v1"));
        var connection = TestConnections.Make("Crayon Cloud", BackendKind.ImageGeneration, "http://crayon.test:8765/v1");
        var error = await Assert.ThrowsAsync<LlmException>(() => failing.GenerateAsync(connection, new ImageRequest("x"), CancellationToken.None));
        Assert.Contains("out of memory", error.Message);

        var empty = new ImageGenerationClient(new StubClientFactory(StubHandler.Text("""{"created":1,"data":[]}"""), "http://crayon.test:8765/v1"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => empty.GenerateAsync(connection, new ImageRequest("x"), CancellationToken.None));

        var wrongKind = TestConnections.Make("Ollama", BackendKind.Ollama, "http://ollama.test:11434");
        await Assert.ThrowsAsync<InvalidOperationException>(() => empty.GenerateAsync(wrongKind, new ImageRequest("x"), CancellationToken.None));
    }

    [Fact]
    public async Task ProbeListsModels()
    {
        var client = new ImageGenerationClient(new StubClientFactory(StubHandler.Text("""{"object":"list","data":[{"id":"z-image-turbo"}]}"""), "http://crayon.test:8765/v1"));
        var connection = TestConnections.Make("Crayon Cloud", BackendKind.ImageGeneration, "http://crayon.test:8765/v1");

        Assert.Equal(["z-image-turbo"], await client.ProbeAsync(connection, CancellationToken.None));
    }
}
