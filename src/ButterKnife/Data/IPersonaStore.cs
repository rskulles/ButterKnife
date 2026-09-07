namespace ButterKnife.Data;

public interface IPersonaStore
{
    /// <summary>Built-in personas first (seed order), then user-created ones by name.</summary>
    Task<IReadOnlyList<Persona>> ListAsync(CancellationToken cancellationToken = default);

    Task<Persona?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Persona> CreateAsync(string name, string description, string systemPrompt, CancellationToken cancellationToken = default);

    /// <summary>Built-in personas may be edited; the change survives restarts because seeding never overwrites.</summary>
    Task UpdateAsync(Guid id, string name, string description, string systemPrompt, CancellationToken cancellationToken = default);

    /// <summary>Throws <see cref="InvalidOperationException"/> for built-in personas. Conversations using the persona fall back to none.</summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
