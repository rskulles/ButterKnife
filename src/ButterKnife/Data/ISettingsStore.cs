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
    /// <summary>Label shown above the user's own messages in the transcript.</summary>
    public const string UserDisplayName = "user.display_name";

    public const string DefaultUserDisplayName = "User";

    public const int MaxUserDisplayNameLength = 40;
}
