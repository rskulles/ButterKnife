using ButterKnife.Services;

namespace ButterKnife.Data;

/// <summary>Durable conversation storage. Chat history never lives in circuit state alone.</summary>
public interface IConversationStore
{
    Task<Conversation> CreateAsync(string title, Guid connectionId, string model, Guid? personaId, CancellationToken cancellationToken = default);

    Task<Conversation?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Most recently updated first.</summary>
    Task<IReadOnlyList<ConversationSummary>> ListAsync(CancellationToken cancellationToken = default);

    Task AppendMessageAsync(Guid conversationId, ChatMessage message, CancellationToken cancellationToken = default);

    Task SetModelAsync(Guid conversationId, Guid connectionId, string model, CancellationToken cancellationToken = default);

    /// <summary>Null clears the persona. The persona's prompt is looked up at request time, so edits apply to later turns.</summary>
    Task SetPersonaAsync(Guid conversationId, Guid? personaId, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
