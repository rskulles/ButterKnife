using System.Net;
using ButterKnife.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ButterKnife.Tests;

public sealed class UpdateCheckerTests
{
    [Theory]
    [InlineData("0.2.0", "v0.3.0", true)]
    [InlineData("0.2.0", "0.2.1", true)]
    [InlineData("0.2.0", "v0.2.0", false)]
    [InlineData("0.2", "v0.2.0", false)]
    [InlineData("0.2.0", "v0.1.9", false)]
    [InlineData("1.0.0", "v0.9.0", false)]
    [InlineData("0.9.0", "v1.0", true)]
    public void IsNewerComparesNormalisedVersions(string current, string tag, bool expected) =>
        Assert.Equal(expected, UpdateChecker.IsNewer(Version.Parse(current), UpdateChecker.ParseTag(tag)!));

    [Theory]
    [InlineData("v0.3.0", "0.3.0.0")]
    [InlineData("0.3", "0.3.0.0")]
    [InlineData(" V1.2.3 ", "1.2.3.0")]
    public void ParseTagAcceptsCommonShapes(string tag, string expected) => Assert.Equal(Version.Parse(expected), UpdateChecker.ParseTag(tag));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("v")]
    public void ParseTagRejectsNonVersions(string? tag) => Assert.Null(UpdateChecker.ParseTag(tag));

    [Fact]
    public async Task CheckReadsTheLatestReleaseAndCachesIt()
    {
        var calls = 0;
        var handler = new StubHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"tag_name":"v99.1.0","html_url":"https://github.com/rskulles/ButterKnife/releases/tag/v99.1.0"}""", System.Text.Encoding.UTF8, "application/json"),
            });
        });
        var checker = new UpdateChecker(new StubClientFactory(handler, "https://api.github.com"), NullLogger<UpdateChecker>.Instance);

        var first = await checker.CheckAsync(CancellationToken.None);
        var second = await checker.CheckAsync(CancellationToken.None);

        Assert.Null(first.Error);
        Assert.Equal(new Version(99, 1, 0, 0), first.Latest);
        Assert.True(first.IsNewer);
        Assert.Equal("https://github.com/rskulles/ButterKnife/releases/tag/v99.1.0", first.Url);
        Assert.Same(first, second);
        Assert.Equal(1, calls);
        Assert.Equal(UpdateChecker.LatestReleaseUrl, handler.LastRequest!.RequestUri!.ToString());
        Assert.Contains("ButterKnife", handler.LastRequest.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task CheckReportsFailuresWithoutThrowingOrCaching()
    {
        var calls = 0;
        var handler = new StubHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("rate limited") });
        });
        var checker = new UpdateChecker(new StubClientFactory(handler, "https://api.github.com"), NullLogger<UpdateChecker>.Instance);

        var first = await checker.CheckAsync(CancellationToken.None);
        var second = await checker.CheckAsync(CancellationToken.None);

        Assert.NotNull(first.Error);
        Assert.Contains("403", first.Error);
        Assert.False(first.IsNewer);
        Assert.Null(first.Latest);
        Assert.Equal(2, calls);
        Assert.NotNull(second.Error);
    }

    [Fact]
    public async Task CheckTreatsAnUnreadableTagAsAnError()
    {
        var checker = new UpdateChecker(new StubClientFactory(StubHandler.Text("""{"tag_name":"nightly"}"""), "https://api.github.com"), NullLogger<UpdateChecker>.Instance);

        var result = await checker.CheckAsync(CancellationToken.None);

        Assert.Contains("nightly", result.Error);
        Assert.False(result.IsNewer);
    }
}
