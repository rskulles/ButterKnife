using System.Collections.Concurrent;
using Microsoft.AspNetCore.Components;

namespace ButterKnife.Services;

/// <summary>A message being written: its text and the pictures and files waiting to go with it.</summary>
public sealed record Draft(string Text, IReadOnlyList<ChatImage> Images, IReadOnlyList<ChatFile> Files);

/// <summary>
/// Unsent messages per user and per chat or Code session ("chat:{id}", "chat:new", "code:{id}", "code:new"), so
/// leaving a page, opening Settings or reloading does not throw away what was typed or attached. Held in memory only:
/// drafts survive navigation and reloads (and other devices of the same user see them), not a restart of ButterKnife.
/// </summary>
public sealed class DraftStore
{
    /// <summary>Oldest drafts are dropped past this many, so a forgotten attachment cannot grow without bound.</summary>
    public const int MaxDrafts = 200;

    private readonly ConcurrentDictionary<(Guid User, string Key), (Draft Draft, long Saved)> _drafts = new();
    private long _clock;

    public Draft? Get(Guid userId, string key) => _drafts.TryGetValue((userId, key), out var entry) ? entry.Draft : null;

    /// <summary>Remembers the draft (an empty one is forgotten). Cheap when nothing changed, so pages may call it on every render.</summary>
    public void Save(Guid userId, string key, string text, IReadOnlyList<ChatImage> images, IReadOnlyList<ChatFile>? files = null)
    {
        files ??= ChatMessage.NoFiles;
        if (userId == Guid.Empty)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(text) && images.Count == 0 && files.Count == 0)
        {
            _drafts.TryRemove((userId, key), out _);
            return;
        }
        if (_drafts.TryGetValue((userId, key), out var existing) && Same(existing.Draft, text, images, files))
        {
            return;
        }

        _drafts[(userId, key)] = (new Draft(text, images.ToArray(), files.ToArray()), Interlocked.Increment(ref _clock));
        if (_drafts.Count > MaxDrafts)
        {
            foreach (var oldest in _drafts.OrderBy(d => d.Value.Saved).Take(_drafts.Count - MaxDrafts).ToArray())
            {
                _drafts.TryRemove(oldest.Key, out _);
            }
        }
    }

    public void Clear(Guid userId, string key) => _drafts.TryRemove((userId, key), out _);

    private static bool Same(Draft draft, string text, IReadOnlyList<ChatImage> images, IReadOnlyList<ChatFile> files) =>
        draft.Text == text && draft.Images.SequenceEqual(images, ReferenceEqualityComparer.Instance) && draft.Files.SequenceEqual(files, ReferenceEqualityComparer.Instance);
}

/// <summary>
/// The chat or Code page this browser connection was last on (recorded by the sidebar), so Settings' back link returns
/// there instead of to a new chat. One per circuit; a reload of a Settings page starts it empty (back goes to a new chat).
/// </summary>
public sealed class ReturnLocation
{
    /// <summary>A base-relative path such as "chat/{id}" or "code/{id}"; empty for a new chat.</summary>
    public string Path { get; private set; } = "";

    public bool IsCode => Path.Equals("code", StringComparison.OrdinalIgnoreCase) || Path.StartsWith("code/", StringComparison.OrdinalIgnoreCase);

    public void Remember(NavigationManager navigation)
    {
        var relative = navigation.ToBaseRelativePath(navigation.Uri);
        var path = relative.Split('?', '#')[0];
        if (!path.StartsWith("settings", StringComparison.OrdinalIgnoreCase) && !path.StartsWith("connections", StringComparison.OrdinalIgnoreCase))
        {
            Path = path;
        }
    }
}
