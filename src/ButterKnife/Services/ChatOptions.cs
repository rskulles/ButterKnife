namespace ButterKnife.Services;

/// <summary>
/// Per-conversation sampling settings. Null means "the server's default", which is what every request used before
/// these existed. Each client maps them to its wire format (see the clients); a backend that has no equivalent
/// ignores the setting.
/// </summary>
public sealed record ChatOptions(double? Temperature = null, int? MaxTokens = null, bool? Think = null)
{
    public static readonly ChatOptions Default = new();

    public const double MinTemperature = 0;
    public const double MaxTemperature = 2;

    public bool IsDefault => Temperature is null && MaxTokens is null && Think is null;

    /// <summary>Clamped and rounded so a slider cannot store 0.30000000000000004.</summary>
    public static double? NormalizeTemperature(double? value) =>
        value is { } t ? Math.Round(Math.Clamp(t, MinTemperature, MaxTemperature), 2) : null;

    public static int? NormalizeMaxTokens(int? value) => value is > 0 ? value : null;
}
