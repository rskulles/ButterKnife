using ButterKnife.Services;

namespace ButterKnife.Data;

/// <summary>A sidebar row in Code mode.</summary>
public sealed record CodeSessionSummary(Guid Id, string Title, string Workspace, DateTimeOffset UpdatedAt);

/// <summary>One stored transcript entry: the message as the model sees it, plus the diff an edit made (display only).</summary>
public sealed record CodeEntry(long Id, ChatMessage Message, string? Diff, DateTimeOffset CreatedAt)
{
    /// <summary>For a prompt sent as a slash command: what the user typed ("/review tests"); the message holds the expansion the model got.</summary>
    public string? Display { get; init; }
}

/// <summary>
/// A Code session: a project folder, the model working in it, how much it may do unasked, and the transcript.
/// <see cref="Summary"/> stands in for the first <see cref="SummaryThrough"/> entries on the wire after a compaction
/// (see CodeCompactor); the entries themselves are never changed.
/// </summary>
public sealed record CodeSession(
    Guid Id,
    Guid UserId,
    string Title,
    string Workspace,
    Guid ConnectionId,
    string Model,
    CodePermissionMode Mode,
    int? ContextTokens,
    int? ContextWindow,
    string? Summary,
    int SummaryThrough,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<CodeEntry> Entries);

/// <summary>In-process signal that the Code session list changed, so the sidebar can refresh across circuits.</summary>
public sealed class CodeSessionEvents
{
    public event Action? Changed;

    public void NotifyChanged() => Changed?.Invoke();
}
