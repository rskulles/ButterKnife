using System.Net;
using ButterKnife.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Primitives;

namespace ButterKnife.Services;

/// <summary>
/// Runs after cookie authentication. Requests from this computer that carry no sign-in become the owner. Every
/// signed-in request is stamped with the zone its address falls in (this computer, the home network, remote), which
/// decides whether an administrator may administer. Anyone else without a valid cookie is sent to the login page
/// (page requests: GET accepting HTML, with a return URL) or gets 401 (the SignalR circuit, the backup download,
/// form posts). Static files and the login endpoints pass.
/// </summary>
public sealed class LoginMiddleware(RequestDelegate next, LoginGate gate)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (context.User.Identity?.IsAuthenticated != true && address is { } ip && LoginGate.IsLoopback(ip))
        {
            context.User = await gate.LocalPrincipalAsync(context.RequestAborted);
        }

        if (context.User.Identity?.IsAuthenticated == true)
        {
            LoginGate.StampZone(context.User, address is null ? NetworkZone.Remote : LoginGate.ZoneOf(address));
            await next(context);
            return;
        }

        if (LoginGate.IsExempt(context))
        {
            await next(context);
            return;
        }

        if (HttpMethods.IsGet(context.Request.Method) && AcceptsHtml(context.Request.Headers.Accept))
        {
            var returnUrl = context.Request.Path + context.Request.QueryString;
            context.Response.Redirect($"{LoginGate.LoginPath}?returnUrl={Uri.EscapeDataString(returnUrl)}");
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    }

    private static bool AcceptsHtml(StringValues accept) =>
        accept.Any(value => value?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true);
}

/// <summary>The login page and sign-out: plain HTML and form posts (no circuit, since the circuit itself is behind the sign-in).</summary>
public static class LoginEndpoints
{
    public static void MapLogin(this IEndpointRouteBuilder app)
    {
        app.MapGet(LoginGate.LoginPath, (HttpContext context, string? returnUrl) =>
        {
            var target = SafeReturnUrl(returnUrl);
            return context.User.Identity?.IsAuthenticated == true
                ? Results.LocalRedirect(target) // already signed in, or this computer
                : Page(target, error: null, username: "");
        });

        app.MapPost(LoginGate.LoginPath, async (HttpContext context, LoginGate gate, IUserStore users) =>
        {
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var target = SafeReturnUrl(form["returnUrl"]);
            var username = form["username"].ToString().Trim();
            var (outcome, user) = await gate.TryLoginAsync(username, form["password"].ToString(), context.Connection.RemoteIpAddress, context.RequestAborted);
            switch (outcome)
            {
                case LoginOutcome.SignedIn:
                    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, LoginGate.CreatePrincipal(user!, local: false), new AuthenticationProperties
                    {
                        IsPersistent = true,
                        ExpiresUtc = DateTimeOffset.UtcNow.Add(LoginGate.RememberFor),
                        AllowRefresh = true,
                    });
                    await users.TouchLoginAsync(user!.Id, context.RequestAborted);
                    return Results.LocalRedirect(target);
                case LoginOutcome.LockedOut:
                    return Page(target, "Too many tries. Wait a minute, then try again.", username);
                default:
                    return Page(target, "That username and password do not match.", username);
            }
        });

        app.MapPost(LoginGate.LogoutPath, async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.LocalRedirect(LoginGate.LoginPath); // this computer is sent straight back to the chat from there
        });
    }

    /// <summary>Only a path on this site is followed after signing in; anything else goes to the chat.</summary>
    public static string SafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && returnUrl[0] == '/' && returnUrl.Length > 1 && returnUrl[1] != '/' && returnUrl[1] != '\\'
            ? returnUrl
            : "/";

    private static IResult Page(string returnUrl, string? error, string username)
    {
        var alert = error is null ? "" : $"""<div class="alert alert-danger py-2 small mt-3 mb-0" role="alert">{WebUtility.HtmlEncode(error)}</div>""";
        var html = $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
                <meta charset="utf-8" />
                <meta name="viewport" content="width=device-width, initial-scale=1.0" />
                <title>Sign in · ButterKnife</title>
                <script>
                    (function () {
                        try {
                            const mode = localStorage.getItem("butterknife.theme") || "auto";
                            const dark = window.matchMedia("(prefers-color-scheme: dark)").matches;
                            document.documentElement.setAttribute("data-bs-theme", mode === "auto" ? (dark ? "dark" : "light") : mode);
                        } catch { }
                    })();
                </script>
                <link rel="stylesheet" href="/bootstrap_pulse.min.css" />
                <link rel="icon" type="image/png" href="/favicon.png" />
            </head>
            <body class="bg-body d-flex align-items-center justify-content-center m-0" style="min-height: 100vh">
                <main class="card shadow-sm" style="width: min(22rem, 92vw)">
                    <form method="post" action="{{LoginGate.LoginPath}}" class="card-body">
                        <h1 class="h5 mb-1">ButterKnife</h1>
                        <p class="text-body-secondary small">Sign in to see your chats.</p>
                        <label class="form-label" for="username">Username</label>
                        <input id="username" name="username" class="form-control form-control-lg" autocomplete="username" autocapitalize="none" spellcheck="false" value="{{WebUtility.HtmlEncode(username)}}" {{(username.Length == 0 ? "autofocus" : "")}} required />
                        <label class="form-label mt-3" for="password">Password</label>
                        <input id="password" name="password" type="password" class="form-control form-control-lg" autocomplete="current-password" {{(username.Length == 0 ? "" : "autofocus")}} required />
                        <input type="hidden" name="returnUrl" value="{{WebUtility.HtmlEncode(returnUrl)}}" />
                        {{alert}}
                        <button type="submit" class="btn btn-primary w-100 mt-3">Sign in</button>
                        <p class="form-text mt-3 mb-0">This device stays signed in for 30 days. No account yet? Ask whoever runs this ButterKnife to add you under Settings → Users.</p>
                    </form>
                </main>
            </body>
            </html>
            """;
        return Results.Content(html, "text/html; charset=utf-8", statusCode: error is null ? StatusCodes.Status200OK : StatusCodes.Status401Unauthorized);
    }
}
