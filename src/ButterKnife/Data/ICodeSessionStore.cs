using ButterKnife.Services;

namespace ButterKnife.Data;

/// <summary>
/// Code sessions, each belonging to one user. Reads take the user's id and return null / nothing for someone else's;
/// writes take ids the page already loaded under that user.
/// </summary>
public interface ICodeSessionStore
{
    Task<CodeSession> CreateAsync(Guid userId, string title, string workspace, Guid connectionId, string model, CodePermissionMode mode, CancellationToken cancellationToken = default);

    Task<CodeSession?> GetAsync(Guid id, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>The user's sessions, most recently active first.</summary>
    Task<IReadOnlyList<CodeSessionSummary>> ListAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Folders the user has worked in, most recent first, for the folder picker.</summary>
    Task<IReadOnlyList<string>> RecentWorkspacesAsync(Guid userId, int limit = 8, CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends a transcript entry and touches the session; returns the entry's id. <paramref name="display"/> is what the
    /// transcript shows instead of the content (a slash command as typed).
    /// </summary>
    Task<long> AppendAsync(Guid sessionId, ChatMessage message, string? diff = null, string? display = null, CancellationToken cancellationToken = default);

    Task SetTitleAsync(Guid id, string title, CancellationToken cancellationToken = default);

    Task SetModelAsync(Guid id, Guid connectionId, string model, CancellationToken cancellationToken = default);

    Task SetModeAsync(Guid id, CodePermissionMode mode, CancellationToken cancellationToken = default);

    Task SetContextAsync(Guid id, int? tokens, int? window, CancellationToken cancellationToken = default);

    /// <summary>The compaction checkpoint: a summary of the first <paramref name="through"/> entries (null clears it).</summary>
    Task SetSummaryAsync(Guid id, string? summary, int? through, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
