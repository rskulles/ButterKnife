using ButterKnife.Services;

namespace ButterKnife.Data;

public sealed record ConversationSummary(Guid Id, string Title, DateTimeOffset UpdatedAt);

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
}
