namespace ButterKnife.Data;

/// <summary>
/// Someone who uses this ButterKnife. The owner is the account this computer uses without signing in (requests from
/// loopback addresses); everyone else signs in with a username and password from another device. Chats belong to one
/// user. <see cref="SecurityStamp"/> is a random value that changes when an administrator resets the password or the
/// role changes, so sessions carrying an older stamp are signed out.
/// </summary>
public sealed record AppUser(
    Guid Id,
    string Username,
    string DisplayName,
    bool IsAdmin,
    bool IsOwner,
    bool HasPassword,
    string SecurityStamp,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt);

/// <summary>In-process signal that a user was added, changed or removed, so open pages in other circuits pick it up.</summary>
public sealed class UserEvents
{
    public event Action? Changed;

    public void NotifyChanged() => Changed?.Invoke();
}
