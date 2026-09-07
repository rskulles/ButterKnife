using ButterKnife.Options;
using ButterKnife.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ButterKnife.Tests;

public class DictationOptionsTests
{
    [Fact]
    public void DefaultsAreAutoStopOnAndAutoSendOff()
    {
        var services = new ServiceCollection().AddLogging()
            .AddSingleton<ButterKnife.Data.IConnectionStore>(new NullStore())
            .AddLlmBackends(new ConfigurationBuilder().Build()).BuildServiceProvider();

        var options = services.GetRequiredService<IOptions<DictationOptions>>().Value;

        Assert.True(options.AutoStopOnSilence);
        Assert.False(options.AutoSendAfterTranscription);
        Assert.Equal(1500, options.SilenceDurationMs);
        Assert.Equal(120, options.MaxRecordingSeconds);
    }

    [Fact]
    public void BindsFromConfigurationAndRejectsSillyValues()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Dictation:AutoStopOnSilence"] = "false",
            ["Dictation:AutoSendAfterTranscription"] = "true",
            ["Dictation:SilenceDurationMs"] = "2500",
        }).Build();
        var services = new ServiceCollection().AddLogging().AddSingleton<ButterKnife.Data.IConnectionStore>(new NullStore()).AddLlmBackends(config).BuildServiceProvider();
        var options = services.GetRequiredService<IOptions<DictationOptions>>().Value;
        Assert.Equal((false, true, 2500), (options.AutoStopOnSilence, options.AutoSendAfterTranscription, options.SilenceDurationMs));

        var bad = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Dictation:SilenceDurationMs"] = "50" }).Build();
        var badServices = new ServiceCollection().AddLogging().AddSingleton<ButterKnife.Data.IConnectionStore>(new NullStore()).AddLlmBackends(bad).BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => badServices.GetRequiredService<IOptions<DictationOptions>>().Value);
    }

    private sealed class NullStore : ButterKnife.Data.IConnectionStore
    {
        public Task<IReadOnlyList<ButterKnife.Data.LlmConnection>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ButterKnife.Data.LlmConnection>>([]);
        public Task<ButterKnife.Data.LlmConnection?> GetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<ButterKnife.Data.LlmConnection?>(null);
        public Task<ButterKnife.Data.LlmConnection> CreateAsync(string name, BackendKind kind, string baseUrl, string? apiKey, string? defaultModel, int? contextWindow, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateAsync(Guid id, string name, BackendKind kind, string baseUrl, string? apiKey, string? defaultModel, int? contextWindow, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
