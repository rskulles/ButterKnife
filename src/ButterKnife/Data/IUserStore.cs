namespace ButterKnife.Data;

/// <summary>User accounts. The owner row is created with the schema, so there is always at least one user.</summary>
public interface IUserStore
{
    /// <summary>The owner first, then by display name.</summary>
    Task<IReadOnlyList<AppUser>> ListAsync(CancellationToken cancellationToken = default);

    Task<AppUser?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Usernames are compared without regard to case.</summary>
    Task<AppUser?> FindByUsernameAsync(string username, CancellationToken cancellationToken = default);

    /// <summary>The account this computer uses without signing in.</summary>
    Task<AppUser> GetOwnerAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a user who can sign in from other devices. Throws <see cref="ArgumentException"/> for a bad username,
    /// display name or password and <see cref="InvalidOperationException"/> when the username is taken.
    /// </summary>
    Task<AppUser> CreateAsync(string username, string displayName, string password, bool isAdmin, CancellationToken cancellationToken = default);

    Task UpdateProfileAsync(Guid id, string username, string displayName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the password; null removes it, which only the owner may do (they use this computer without one, everyone
    /// else needs one to get in). With <paramref name="signOutEverywhere"/> the security stamp changes too, so every
    /// device that user is signed in on has to sign in again.
    /// </summary>
    Task SetPasswordAsync(Guid id, string? password, bool signOutEverywhere, CancellationToken cancellationToken = default);

    /// <summary>The owner is always an administrator. Changes the stamp, so the user's devices sign in again and pick up the new role.</summary>
    Task SetAdminAsync(Guid id, bool isAdmin, CancellationToken cancellationToken = default);

    /// <summary>Removes the user and every chat they own. The owner cannot be deleted.</summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>The user when the username exists, has a password and it matches; otherwise null, whatever went wrong.</summary>
    Task<AppUser?> VerifyPasswordAsync(string username, string password, CancellationToken cancellationToken = default);

    Task TouchLoginAsync(Guid id, CancellationToken cancellationToken = default);
}
