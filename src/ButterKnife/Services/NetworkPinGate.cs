using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using ButterKnife.Data;
using Microsoft.AspNetCore.DataProtection;

namespace ButterKnife.Services;

public enum UnlockOutcome
{
    Unlocked,
    WrongPin,
    LockedOut,
}

/// <summary>
/// Optional PIN for devices other than this computer. The PIN is stored as a salted PBKDF2 hash in the settings
/// table; a device that enters it gets a Data-Protection-signed cookie that carries a "stamp" regenerated whenever
/// the PIN is set or removed, so changing the PIN signs every other device out. Requests from loopback addresses,
/// the unlock page and static files never need it (see <see cref="IsExempt"/>). Guessing is slowed down per
/// address: five wrong tries earn a minute's wait.
/// </summary>
public sealed class NetworkPinGate
{
    public const string CookieName = "butterknife.unlock";
    public const string UnlockPath = "/unlock";
    public const int MinLength = 4;
    public const int MaxLength = 64;
    public static readonly TimeSpan RememberFor = TimeSpan.FromDays(30);

    private const int MaxFailures = 5;
    private const int Pbkdf2Iterations = 100_000;
    private static readonly TimeSpan Lockout = TimeSpan.FromMinutes(1);

    private readonly ISettingsStore _settings;
    private readonly IDataProtector _protector;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly ConcurrentDictionary<IPAddress, Attempts> _attempts = new();
    private volatile bool _loaded;
    private string? _hash;
    private string _stamp = "";

    public NetworkPinGate(ISettingsStore settings, SettingsEvents events, IDataProtectionProvider dataProtection, TimeProvider? time = null)
    {
        _settings = settings;
        _protector = dataProtection.CreateProtector("ButterKnife.NetworkPin.Cookie");
        _time = time ?? TimeProvider.System;
        events.Changed += () => _loaded = false; // a restored backup may carry a different PIN
    }

    /// <summary>Whether a PIN is set. Loads the stored hash on first use and after settings change.</summary>
    public async ValueTask<bool> IsEnabledAsync(CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        return _hash is not null;
    }

    /// <summary>Sets the PIN, or removes it when <paramref name="pin"/> is null. Either way every remembered device has to unlock again.</summary>
    public async Task SetPinAsync(string? pin, CancellationToken cancellationToken)
    {
        string? hash = null;
        string? stamp = null;
        if (pin is not null)
        {
            if (Validate(pin) is { } problem)
            {
                throw new ArgumentException(problem, nameof(pin));
            }
            hash = Hash(pin);
            stamp = RandomNumberGenerator.GetHexString(16);
        }

        await _settings.SetAsync(SettingKeys.NetworkPinHash, hash, cancellationToken);
        await _settings.SetAsync(SettingKeys.NetworkPinStamp, stamp, cancellationToken);
        _hash = hash;
        _stamp = stamp ?? "";
        _loaded = true;
        _attempts.Clear();
    }

    /// <summary>Null when the PIN is acceptable, otherwise what is wrong with it.</summary>
    public static string? Validate(string pin) => pin switch
    {
        _ when pin.Length < MinLength => $"Use at least {MinLength} characters.",
        _ when pin.Length > MaxLength => $"Use at most {MaxLength} characters.",
        _ when pin.Trim().Length != pin.Length => "No spaces at the start or end.",
        _ => null,
    };

    /// <summary>Requests that never need the PIN: this computer, the unlock page itself, and static files (the unlock page needs its stylesheet).</summary>
    public static bool IsExempt(HttpContext context)
    {
        var path = context.Request.Path;
        if (path.StartsWithSegments(UnlockPath) || Path.HasExtension(path.Value))
        {
            return true;
        }
        return context.Connection.RemoteIpAddress is { } ip && IsLoopback(ip);
    }

    public static bool IsLoopback(IPAddress ip) => IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip);

    public bool HasValidCookie(HttpRequest request) => request.Cookies.TryGetValue(CookieName, out var token) && IsValidToken(token);

    public bool IsValidToken(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }
        try
        {
            var parts = _protector.Unprotect(token).Split('|');
            return parts.Length == 2
                && parts[0] == _stamp
                && long.TryParse(parts[1], out var expires)
                && expires > _time.GetUtcNow().ToUnixTimeSeconds();
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>A cookie value that unlocks this device for <see cref="RememberFor"/> under the current PIN.</summary>
    public string IssueToken() => _protector.Protect($"{_stamp}|{_time.GetUtcNow().Add(RememberFor).ToUnixTimeSeconds()}");

    /// <summary>Checks a PIN typed from <paramref name="address"/>; after five wrong tries that address waits a minute.</summary>
    public async Task<UnlockOutcome> TryUnlockAsync(string pin, IPAddress? address, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        var attempts = _attempts.GetOrAdd(address ?? IPAddress.None, _ => new Attempts());
        var now = _time.GetUtcNow();
        lock (attempts)
        {
            if (attempts.LockedUntil > now)
            {
                return UnlockOutcome.LockedOut;
            }
            if (_hash is not null && Verify(pin, _hash))
            {
                attempts.Failures = 0;
                return UnlockOutcome.Unlocked;
            }
            if (++attempts.Failures >= MaxFailures)
            {
                attempts.Failures = 0;
                attempts.LockedUntil = now + Lockout;
            }
            return UnlockOutcome.WrongPin;
        }
    }

    /// <summary>"pbkdf2-sha256$iterations$salt$hash", all base64 but the label and count.</summary>
    public static string Hash(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(pin, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Pbkdf2Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string pin, string stored)
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
            var actual = Rfc2898DeriveBytes.Pbkdf2(pin, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return;
        }
        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            if (_loaded)
            {
                return;
            }
            _hash = await _settings.GetAsync(SettingKeys.NetworkPinHash, cancellationToken);
            _stamp = await _settings.GetAsync(SettingKeys.NetworkPinStamp, cancellationToken) ?? "";
            _loaded = true;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private sealed class Attempts
    {
        public int Failures;
        public DateTimeOffset LockedUntil;
    }
}
