namespace ButterKnife.Services;

/// <summary>Resolves the configured backends by name.</summary>
public interface ILlmClientRegistry
{
    IReadOnlyList<ILlmClient> Clients { get; }

    ILlmClient Get(string backendName);
}
