using ButterKnife.Components.Shared;
using ButterKnife.Services;

namespace ButterKnife.Tests;

public class ThrobberStatsTests
{
    [Fact]
    public void FormatsLiveBeforeAndAfterFirstToken()
    {
        Assert.Equal("1.2s · waiting for first token…",
            ButterKnifeThrobber.FormatStats(new GenerationStats(TimeSpan.FromSeconds(1.2), null, 0, null), live: true));

        Assert.Equal("first token 800ms · 2.4s · 87 tokens · 54 tok/s",
            ButterKnifeThrobber.FormatStats(new GenerationStats(TimeSpan.FromSeconds(2.4), TimeSpan.FromSeconds(0.8), 87, null), live: true));
    }

    [Fact]
    public void FormatsFinalWithPromptStats()
    {
        var stats = new GenerationStats(TimeSpan.FromSeconds(3.1), TimeSpan.FromSeconds(0.8), 120,
            new TokenUsage(1200, 118, TimeSpan.FromSeconds(0.7), TimeSpan.FromSeconds(2.2)));

        Assert.Equal("first token 800ms · prompt 1.2k tok @ 1714 tok/s · 3.1s · 118 tokens · 54 tok/s",
            ButterKnifeThrobber.FormatStats(stats));
    }

    [Fact]
    public void ShowsSecondsForSlowFirstToken()
    {
        var stats = new GenerationStats(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2.5), 3, null);
        Assert.StartsWith("first token 2.5s · ", ButterKnifeThrobber.FormatStats(stats));
    }

    [Fact]
    public void OmitsPromptRateWhenNothingMeasuresIt()
    {
        var stats = new GenerationStats(TimeSpan.FromSeconds(3), null, 10, new TokenUsage(300, 10));
        Assert.Equal("prompt 300 tok · 3.0s · 10 tokens · 3 tok/s", ButterKnifeThrobber.FormatStats(stats));
    }
}
