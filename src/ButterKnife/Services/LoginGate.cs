using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using ButterKnife.Data;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace ButterKnife.Services;

public enum LoginOutcome
{
    SignedIn,
    WrongCredentials,
    LockedOut,
}

/// <summary>
/// Who a request belongs to. Requests from loopback addresses are this computer and act as the owner without
/// signing in (also the recovery path: the owner can always reach Settings → Users from here). Everyone else signs
/// in on the login page and gets a cookie (ASP.NET Core cookie authentication, keys from Data Protection) whose
/// claims carry the user's id and security stamp; a stamp that no longer matches the stored one (password reset by
/// an admin, role changed, user deleted) makes the cookie worthless. Guessing is slowed down per address: five wrong
/// tries earn a minute's wait.
/// </summary>
public sealed class LoginGate(IUserStore users, TimeProvider? time = null)
{
    public const string LoginPath = "/login";
    public const string LogoutPath = "/logout";
    public const string CookieName = "butterknife.session";
    public const string StampClaim = "butterknife:stamp";
    public const string LocalClaim = "butterknife:local";
    public const string AdminRole = "admin";
    public static readonly TimeSpan RememberFor = TimeSpan.FromDays(30);

    private const int MaxFailures = 5;
    private static readonly TimeSpan Lockout = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<IPAddress, Attempts> _attempts = new();

    /// <summary>Requests that never need a sign-in: the login and logout endpoints and static files (the login page needs its stylesheet).</summary>
    public static bool IsExempt(HttpContext context)
    {
        var path = context.Request.Path;
        return path.StartsWithSegments(LoginPath) || path.StartsWithSegments(LogoutPath) || Path.HasExtension(path.Value);
    }

    public static bool IsLoopback(IPAddress ip) => IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip);

    public static bool IsAdmin(ClaimsPrincipal principal) => principal.IsInRole(AdminRole);

    public static bool IsLocal(ClaimsPrincipal principal) => principal.HasClaim(LocalClaim, "1");

    public static Guid? UserId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    /// <summary>The claims a signed-in (or local) user carries: id, username, role, stamp, and whether the sign-in was implicit.</summary>
    public static ClaimsPrincipal CreatePrincipal(AppUser user, bool local)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString("D")),
            new(ClaimTypes.Name, user.Username),
            new(StampClaim, user.SecurityStamp),
        };
        if (user.IsAdmin)
        {
            claims.Add(new Claim(ClaimTypes.Role, AdminRole));
        }
        if (local)
        {
            claims.Add(new Claim(LocalClaim, "1"));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme, ClaimTypes.Name, ClaimTypes.Role));
    }

    /// <summary>The owner, as this computer's requests see themselves.</summary>
    public async Task<ClaimsPrincipal> LocalPrincipalAsync(CancellationToken cancellationToken) =>
        CreatePrincipal(await users.GetOwnerAsync(cancellationToken), local: true);

    /// <summary>Whether a cookie's user still exists with the same stamp.</summary>
    public async Task<bool> IsCurrentAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        if (UserId(principal) is not { } id)
        {
            return false;
        }
        var user = await users.GetAsync(id, cancellationToken);
        return user is not null && user.SecurityStamp == principal.FindFirst(StampClaim)?.Value;
    }

    /// <summary>Checks a username and password typed from <paramref name="address"/>; after five wrong tries that address waits a minute.</summary>
    public async Task<(LoginOutcome Outcome, AppUser? User)> TryLoginAsync(string username, string password, IPAddress? address, CancellationToken cancellationToken)
    {
        var attempts = _attempts.GetOrAdd(address ?? IPAddress.None, _ => new Attempts());
        var now = _time.GetUtcNow();
        lock (attempts)
        {
            if (attempts.LockedUntil > now)
            {
                return (LoginOutcome.LockedOut, null);
            }
        }

        var user = await users.VerifyPasswordAsync(username, password, cancellationToken);
        lock (attempts)
        {
            if (user is not null)
            {
                attempts.Failures = 0;
                return (LoginOutcome.SignedIn, user);
            }
            if (++attempts.Failures >= MaxFailures)
            {
                attempts.Failures = 0;
                attempts.LockedUntil = now + Lockout;
            }
            return (LoginOutcome.WrongCredentials, null);
        }
    }

    private sealed class Attempts
    {
        public int Failures;
        public DateTimeOffset LockedUntil;
    }
}
