using ButterKnife.Services;

namespace ButterKnife.Data;

/// <summary>Durable conversation storage. Chat history never lives in circuit state alone.</summary>
public interface IConversationStore
{
    Task<Conversation> CreateAsync(string title, string backend, string model, CancellationToken cancellationToken = default);

    Task<Conversation?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Most recently updated first.</summary>
    Task<IReadOnlyList<ConversationSummary>> ListAsync(CancellationToken cancellationToken = default);

    Task AppendMessageAsync(Guid conversationId, ChatMessage message, CancellationToken cancellationToken = default);

    Task SetModelAsync(Guid conversationId, string backend, string model, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
