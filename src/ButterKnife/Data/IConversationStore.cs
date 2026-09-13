using ButterKnife.Services;

namespace ButterKnife.Data;

/// <summary>Durable conversation storage. Chat history never lives in circuit state alone.</summary>
public interface IConversationStore
{
    Task<Conversation> CreateAsync(string title, Guid connectionId, string model, Guid? personaId, CancellationToken cancellationToken = default);

    Task<Conversation?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Most recently updated first.</summary>
    Task<IReadOnlyList<ConversationSummary>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns the new message's id.</summary>
    Task<long> AppendMessageAsync(Guid conversationId, ChatMessage message, CancellationToken cancellationToken = default);

    /// <summary>Rewrites a message's text (and reasoning), e.g. after continuing a cut-off reply.</summary>
    Task SetMessageContentAsync(Guid conversationId, long messageId, string content, string? reasoning, CancellationToken cancellationToken = default);

    /// <summary>Removes one message (and its images).</summary>
    Task DeleteMessageAsync(Guid conversationId, long messageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the message and everything after it, for edit-and-resend and regenerate. A compaction summary that
    /// covered removed messages is cleared, and the stored context usage is reset since it no longer applies.
    /// </summary>
    Task DeleteMessagesFromAsync(Guid conversationId, long messageId, CancellationToken cancellationToken = default);

    Task SetModelAsync(Guid conversationId, Guid connectionId, string model, CancellationToken cancellationToken = default);

    /// <summary>Renames the conversation. Blank titles are rejected; long ones are trimmed to a sane length.</summary>
    Task SetTitleAsync(Guid conversationId, string title, CancellationToken cancellationToken = default);

    /// <summary>Null clears the persona. The persona's prompt is looked up at request time, so edits apply to later turns.</summary>
    Task SetPersonaAsync(Guid conversationId, Guid? personaId, CancellationToken cancellationToken = default);

    /// <summary>Compaction checkpoint: <paramref name="summary"/> covers the first <paramref name="summaryThrough"/> messages. Null clears it.</summary>
    Task SetSummaryAsync(Guid conversationId, string? summary, int? summaryThrough, CancellationToken cancellationToken = default);

    /// <summary>Last reported context usage and the model's window, for the context meter.</summary>
    Task SetContextUsageAsync(Guid conversationId, int? contextTokens, int? contextWindow, CancellationToken cancellationToken = default);

    /// <summary>
    /// A new conversation with the same connection, model and persona, holding copies of the messages up to and
    /// including <paramref name="throughMessageId"/> (images and reasoning included). The compaction summary comes
    /// along only if it still covers a prefix of the copied messages.
    /// </summary>
    Task<Conversation> BranchAsync(Guid conversationId, long throughMessageId, string title, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
