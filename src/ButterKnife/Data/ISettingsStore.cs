namespace ButterKnife.Data;

/// <summary>App-wide key/value settings (one row per key), shared by every browser that opens the app.</summary>
public interface ISettingsStore
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Null or whitespace deletes the row, so readers fall back to the default.</summary>
    Task SetAsync(string key, string? value, CancellationToken cancellationToken = default);
}

/// <summary>Known setting keys and their defaults.</summary>
public static class SettingKeys
{
    /// <summary>
    /// From before user accounts: the one display name shown above the user's messages. The schema migration copies
    /// it onto the owner account and removes the row (see SqliteDatabase.EnsureOwnerAsync); nothing writes it now.
    /// </summary>
    public const string UserDisplayName = "user.display_name";

    public const string DefaultUserDisplayName = "User";

    /// <summary>"true" to listen on all interfaces so phones and other devices can reach the app; "false" for localhost only. Applied at startup.</summary>
    public const string ListenOnLan = "server.listen_on_lan";

    /// <summary>"false" to stop Settings → General asking GitHub whether a newer release exists. Unset means on.</summary>
    public const string CheckForUpdates = "updates.check";
}
