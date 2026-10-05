using System.Security.Cryptography;

namespace ButterKnife.Services;

/// <summary>
/// Salted PBKDF2-SHA256 for user passwords, stored as "pbkdf2-sha256$iterations$salt$hash" (base64 for the last
/// two). Short passwords are allowed on purpose: this guards a chat app on a home network, and a four-digit code
/// typed on a phone is what people will actually use; the login endpoint slows guessing down per address.
/// </summary>
public static class PasswordHasher
{
    public const int MinLength = 4;
    public const int MaxLength = 64;
    private const int Iterations = 100_000;

    /// <summary>Null when the password is acceptable, otherwise what is wrong with it.</summary>
    public static string? Validate(string password) => password switch
    {
        _ when password.Length < MinLength => $"Use at least {MinLength} characters.",
        _ when password.Length > MaxLength => $"Use at most {MaxLength} characters.",
        _ when password.Trim().Length != password.Length => "No spaces at the start or end.",
        _ => null,
    };

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out var iterations) || iterations <= 0)
        {
            return false;
        }
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
