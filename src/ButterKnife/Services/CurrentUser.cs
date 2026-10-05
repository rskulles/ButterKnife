using ButterKnife.Data;
using Microsoft.AspNetCore.Components.Authorization;

namespace ButterKnife.Services;

/// <summary>
/// The user behind this circuit. Blazor takes the principal from the connection that opened the circuit (the cookie,
/// or the owner for this computer, see <see cref="LoginMiddleware"/>); the row is re-read from the store on each call
/// so a changed display name or role shows up without signing in again. Null means the user has since been deleted:
/// pages then send the browser to the login page.
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
}
