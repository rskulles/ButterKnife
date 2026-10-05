using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
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

/// <summary>Where a request comes from, judged by its address. Administrators may administer only from inside the house.</summary>
public enum NetworkZone
{
    /// <summary>This computer (a loopback address).</summary>
    Local,

    /// <summary>A private or link-local address: a device on the home network.</summary>
    Lan,

    /// <summary>Anything else: the internet, or an overlay network such as Tailscale (100.64.0.0/10), whose devices may be anywhere.</summary>
    Remote,
}

/// <summary>
/// Who a request belongs to, and where it comes from. Requests from loopback addresses are this computer and act as
/// the owner without signing in (also the recovery path: the owner can always reach Settings → Users from here).
/// Everyone else signs in on the login page and gets a cookie (ASP.NET Core cookie authentication, keys from Data
/// Protection) whose claims carry the user's id and security stamp; a stamp that no longer matches the stored one
/// (password reset by an admin, role changed, user deleted) makes the cookie worthless. Each request is also
/// stamped with its <see cref="NetworkZone"/>, and administering (server-wide settings, the backup) needs the admin
/// role <em>and</em> a zone inside the house. Guessing is slowed down per address: five wrong tries earn a minute's wait.
/// </summary>
public sealed class LoginGate(IUserStore users, TimeProvider? time = null)
{
    public const string LoginPath = "/login";
    public const string LogoutPath = "/logout";
    public const string CookieName = "butterknife.session";
    public const string StampClaim = "butterknife:stamp";
    public const string LocalClaim = "butterknife:local";
    public const string ZoneClaim = "butterknife:zone";
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

    /// <summary>
    /// Loopback is this computer; RFC 1918, link-local and IPv6 unique-local addresses are the home network; everything
    /// else is remote. Tailscale's 100.64.0.0/10 (shared address space) is remote on purpose: those devices may be anywhere.
    /// </summary>
    public static NetworkZone ZoneOf(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }
        if (IPAddress.IsLoopback(ip))
        {
            return NetworkZone.Local;
        }

        var bytes = ip.GetAddressBytes();
        var lan = ip.AddressFamily switch
        {
            AddressFamily.InterNetwork =>
                bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254),
            AddressFamily.InterNetworkV6 =>
                (bytes[0] & 0xFE) == 0xFC                          // fc00::/7 unique local
                || (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80), // fe80::/10 link local
            _ => false,
        };
        return lan ? NetworkZone.Lan : NetworkZone.Remote;
    }

    /// <summary>The zone the middleware stamped on this request; a principal without one (never stamped) counts as remote.</summary>
    public static NetworkZone ZoneOf(ClaimsPrincipal principal) =>
        Enum.TryParse<NetworkZone>(principal.FindFirst(ZoneClaim)?.Value, out var zone) ? zone : NetworkZone.Remote;

    public static bool IsAdmin(ClaimsPrincipal principal) => principal.IsInRole(AdminRole);

    /// <summary>Administrators administer only from inside the house: this computer or a device on the local network.</summary>
    public static bool CanAdminister(ClaimsPrincipal principal) => IsAdmin(principal) && ZoneOf(principal) != NetworkZone.Remote;

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

    /// <summary>
    /// Records where this request came from. Done per request by the middleware, never at sign-in, so a laptop that
    /// leaves the house loses its home standing on its next page load even though the cookie is the same.
    /// </summary>
    public static void StampZone(ClaimsPrincipal principal, NetworkZone zone)
    {
        if (principal.Identity is not ClaimsIdentity identity)
        {
            return;
        }
        foreach (var old in identity.FindAll(ZoneClaim).ToList())
        {
            identity.RemoveClaim(old);
        }
        identity.AddClaim(new Claim(ZoneClaim, zone.ToString()));
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
