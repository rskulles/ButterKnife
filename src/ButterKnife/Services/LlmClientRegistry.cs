using ButterKnife.Options;
using Microsoft.Extensions.Options;

namespace ButterKnife.Services;

public sealed class LlmClientRegistry : ILlmClientRegistry
{
    private readonly Dictionary<string, ILlmClient> _byName;

    public LlmClientRegistry(IOptions<LlmOptions> options, IHttpClientFactory httpClientFactory)
    {
        _byName = new Dictionary<string, ILlmClient>(StringComparer.OrdinalIgnoreCase);

        foreach (var backend in options.Value.Backends)
        {
            ILlmClient client = backend.Kind switch
            {
                BackendKind.Ollama => new OllamaClient(httpClientFactory, backend.Name, backend.DefaultModel),
                BackendKind.OpenAiCompatible => new OpenAiCompatibleClient(httpClientFactory, backend.Name, backend.DefaultModel),
                _ => throw new InvalidOperationException($"Unknown backend kind '{backend.Kind}' for '{backend.Name}'."),
            };

            if (!_byName.TryAdd(backend.Name, client))
            {
                throw new InvalidOperationException($"Duplicate LLM backend name '{backend.Name}'.");
            }
        }

        Clients = _byName.Values.ToArray();
    }

    public IReadOnlyList<ILlmClient> Clients { get; }

    public ILlmClient Get(string backendName) =>
        _byName.TryGetValue(backendName, out var client)
            ? client
            : throw new KeyNotFoundException($"No LLM backend named '{backendName}' is configured.");
}
