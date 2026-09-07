namespace ButterKnife.Data;

/// <summary>A named system prompt. Built-in personas are seeded and cannot be deleted; users may add their own.</summary>
public sealed record Persona(
    Guid Id,
    string Name,
    string Description,
    string SystemPrompt,
    bool IsBuiltIn,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
