using System.Net.Http.Headers;
using ButterKnife.Options;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

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

        // One named HttpClient per backend. Named at startup from configuration so IHttpClientFactory can
        // manage handler lifetimes; per-backend settings are applied by the options-aware configurator below.
        var backends = configuration.GetSection(LlmOptions.SectionName).Get<LlmOptions>()?.Backends ?? [];
        foreach (var backend in backends)
        {
            services.AddHttpClient(backend.Name);
        }

        services.AddSingleton<IConfigureOptions<HttpClientFactoryOptions>, ConfigureBackendHttpClients>();
        services.AddSingleton<ILlmClientRegistry, LlmClientRegistry>();
        services.AddSingleton<ModelCatalog>();

        return services;
    }

    /// <summary>Applies BaseUrl, ApiKey and an infinite timeout to the named client for each backend.</summary>
    private sealed class ConfigureBackendHttpClients(IOptions<LlmOptions> options)
        : IConfigureNamedOptions<HttpClientFactoryOptions>
    {
        public void Configure(HttpClientFactoryOptions options) { }

        public void Configure(string? name, HttpClientFactoryOptions factoryOptions)
        {
            var backend = options.Value.Backends
                .FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
            if (backend is null)
            {
                return;
            }

            factoryOptions.HttpClientActions.Add(client =>
            {
                // Relative request URIs ("api/chat") resolve correctly only when the base ends with "/".
                var baseUrl = backend.BaseUrl.EndsWith('/') ? backend.BaseUrl : backend.BaseUrl + "/";
                client.BaseAddress = new Uri(baseUrl, UriKind.Absolute);

                // Generations can run for minutes; cancellation is driven by the caller's token instead.
                client.Timeout = Timeout.InfiniteTimeSpan;

                if (!string.IsNullOrWhiteSpace(backend.ApiKey))
                {
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", backend.ApiKey);
                }
            });
        }
    }
}
