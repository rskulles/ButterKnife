using ButterKnife.Services;

namespace ButterKnife.Data;

/// <summary>
/// A search result: a message (with a snippet whose matches are wrapped in <see cref="SearchHit.MarkStart"/> and
/// <see cref="SearchHit.MarkEnd"/>) or, when <see cref="MessageId"/> is null, a conversation whose title matched.
/// </summary>
public sealed record SearchHit(Guid ConversationId, string Title, long? MessageId, ChatRole? Role, string Snippet, DateTimeOffset At)
{
    public const char MarkStart = '\u0001';
    public const char MarkEnd = '\u0002';
}

/// <summary>A sidebar row. Pinned conversations sort first; archived ones are folded away but otherwise ordinary.</summary>
public sealed record ConversationSummary(Guid Id, string Title, DateTimeOffset UpdatedAt, bool Pinned = false, bool Archived = false);

public sealed record Conversation(
    Guid Id,
    string Title,
    Guid ConnectionId,
    string Model,
    Guid? PersonaId,
    string? Summary,
    int? SummaryThrough,
    int? ContextTokens,
    int? ContextWindow,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ChatMessage> Messages)
{
    /// <summary>Sampling settings for this conversation; <see cref="ChatOptions.Default"/> means the server's defaults.</summary>
    public ChatOptions Options { get; init; } = ChatOptions.Default;

    /// <summary>Extra instructions sent with every request in this conversation, on top of the persona's prompt; null for none.</summary>
    public string? Instructions { get; init; }
}
