using ButterKnife.Data;
using Microsoft.AspNetCore.Components.Authorization;

namespace ButterKnife.Services;

/// <summary>
/// The user behind this circuit. Blazor takes the principal from the connection that opened the circuit (the cookie,
/// or the owner for this computer, see <see cref="LoginMiddleware"/>); the row is re-read from the store on each call
/// so a changed display name or role shows up without signing in again. Null means the user has since been deleted:
/// pages then send the browser to the login page. The zone is the one stamped on the connection that opened the
/// circuit, so it stays put until the page is reloaded.
/// </summary>
public sealed class CurrentUser(AuthenticationStateProvider authentication, IUserStore users)
{
    public async Task<AppUser?> GetAsync(CancellationToken cancellationToken = default)
    {
        var principal = (await authentication.GetAuthenticationStateAsync()).User;
        return LoginGate.UserId(principal) is { } id ? await users.GetAsync(id, cancellationToken) : null;
    }

    /// <summary>True for the owner using this computer: no sign-in happened, so there is nothing to sign out of.</summary>
    public async Task<bool> IsLocalAsync()
    {
        var principal = (await authentication.GetAuthenticationStateAsync()).User;
        return LoginGate.IsLocal(principal);
    }

    /// <summary>Where this circuit was opened from: this computer, the home network, or somewhere remote.</summary>
    public async Task<NetworkZone> ZoneAsync()
    {
        var principal = (await authentication.GetAuthenticationStateAsync()).User;
        return LoginGate.ZoneOf(principal);
    }

    /// <summary>
    /// Whether this user may change server-wide settings here: an administrator (as the store says now) inside the
    /// house (as the connection says). Remote administrators chat and manage their account only.
    /// </summary>
    public async Task<bool> CanAdministerAsync(CancellationToken cancellationToken = default) =>
        (await GetAsync(cancellationToken))?.IsAdmin == true && await ZoneAsync() != NetworkZone.Remote;
}
