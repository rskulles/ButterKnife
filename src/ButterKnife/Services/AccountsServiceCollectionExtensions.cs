using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace ButterKnife.Services;

public static class AccountsServiceCollectionExtensions
{
    /// <summary>User accounts: cookie authentication for other devices, the login gate, and the per-circuit current user.</summary>
    public static IServiceCollection AddAccounts(this IServiceCollection services)
    {
        services.AddSingleton<LoginGate>();
        services.AddScoped<CurrentUser>();

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = LoginGate.CookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; // plain http on the LAN is the normal case
                options.Cookie.IsEssential = true;
                options.ExpireTimeSpan = LoginGate.RememberFor;
                options.SlidingExpiration = true;
                options.LoginPath = LoginGate.LoginPath;
                options.LogoutPath = LoginGate.LogoutPath;

                // A cookie outlives the account it was issued for only until the next request: a stamp that no
                // longer matches (password reset by an admin, role change, deleted user) signs the device out.
                options.Events.OnValidatePrincipal = async context =>
                {
                    var gate = context.HttpContext.RequestServices.GetRequiredService<LoginGate>();
                    if (context.Principal is null || !await gate.IsCurrentAsync(context.Principal, context.HttpContext.RequestAborted))
                    {
                        context.RejectPrincipal();
                        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    }
                };
            });

        return services;
    }
}
