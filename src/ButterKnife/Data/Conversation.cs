using ButterKnife.Services;

namespace ButterKnife.Data;

public sealed record ConversationSummary(Guid Id, string Title, DateTimeOffset UpdatedAt);

public sealed record Conversation(
    Guid Id,
    string Title,
    string Backend,
    string Model,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ChatMessage> Messages);
