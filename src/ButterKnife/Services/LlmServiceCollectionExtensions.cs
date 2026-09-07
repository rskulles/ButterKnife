using ButterKnife.Data;
using ButterKnife.Options;

namespace ButterKnife.Services;

public static class LlmServiceCollectionExtensions
{
    public static IServiceCollection AddLlmBackends(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<LlmOptions>()
            .Bind(configuration.GetSection(LlmOptions.SectionName))
            .Validate(o => o.Backends.All(b => !string.IsNullOrWhiteSpace(b.Name)), "Every Llm backend needs a Name.")
            .Validate(o => o.Backends.All(b => Uri.TryCreate(b.BaseUrl, UriKind.Absolute, out _)), "Every Llm backend needs an absolute BaseUrl.")
            .ValidateOnStart();

        services.AddOptions<DictationOptions>()
            .Bind(configuration.GetSection(DictationOptions.SectionName))
            .Validate(o => o.SilenceDurationMs >= 300 && o.MaxRecordingSeconds >= 5, "Dictation: SilenceDurationMs must be ≥ 300 and MaxRecordingSeconds ≥ 5.")
            .ValidateOnStart();

        // One shared client for all HTTP-based connections; requests carry absolute URIs and per-connection auth.
        // Generations can run for minutes; cancellation is driven by the caller's token instead of a timeout.
        services.AddHttpClient(LlmClientBase.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan);

        services.AddSingleton<ILlmClientRegistry, LlmClientRegistry>();
        services.AddSingleton<ModelCatalog>();
        services.AddSingleton<MarkdownRenderer>();
        services.AddSingleton<ConversationCompactor>();
        services.AddSingleton<TranscriptionClient>();
        services.AddSingleton<TranscriptionService>();
        services.AddHostedService<ConnectionSeeder>();

        return services;
    }
}
