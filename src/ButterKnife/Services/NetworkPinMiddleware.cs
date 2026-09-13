using System.Net;
using Microsoft.Extensions.Primitives;

namespace ButterKnife.Services;

/// <summary>
/// Sends devices that have not entered the PIN to the unlock page. Page requests (GET accepting HTML) are
/// redirected with a return URL; anything else (the SignalR circuit, the backup download, form posts) gets 401.
/// Sits before antiforgery and the endpoints, so it also fronts static assets, which <see cref="NetworkPinGate.IsExempt"/> lets through.
/// </summary>
public sealed class NetworkPinMiddleware(RequestDelegate next, NetworkPinGate gate)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!await gate.IsEnabledAsync(context.RequestAborted) || NetworkPinGate.IsExempt(context) || gate.HasValidCookie(context.Request))
        {
            await next(context);
            return;
        }

        if (HttpMethods.IsGet(context.Request.Method) && AcceptsHtml(context.Request.Headers.Accept))
        {
            var returnUrl = context.Request.Path + context.Request.QueryString;
            context.Response.Redirect($"{NetworkPinGate.UnlockPath}?returnUrl={Uri.EscapeDataString(returnUrl)}");
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    }

    private static bool AcceptsHtml(StringValues accept) =>
        accept.Any(value => value?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true);
}

/// <summary>The unlock page: a plain HTML form (no circuit, since the circuit itself is behind the PIN).</summary>
public static class NetworkPinEndpoints
{
    public static void MapNetworkPin(this IEndpointRouteBuilder app)
    {
        app.MapGet(NetworkPinGate.UnlockPath, async (HttpContext context, NetworkPinGate gate, string? returnUrl) =>
        {
            var target = SafeReturnUrl(returnUrl);
            if (!await gate.IsEnabledAsync(context.RequestAborted) || gate.HasValidCookie(context.Request)
                || (context.Connection.RemoteIpAddress is { } ip && NetworkPinGate.IsLoopback(ip)))
            {
                return Results.LocalRedirect(target);
            }
            return Page(target, error: null);
        });

        app.MapPost(NetworkPinGate.UnlockPath, async (HttpContext context, NetworkPinGate gate) =>
        {
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var target = SafeReturnUrl(form["returnUrl"]);
            var outcome = await gate.TryUnlockAsync(form["pin"].ToString(), context.Connection.RemoteIpAddress, context.RequestAborted);
            switch (outcome)
            {
                case UnlockOutcome.Unlocked:
                    context.Response.Cookies.Append(NetworkPinGate.CookieName, gate.IssueToken(), new CookieOptions
                    {
                        HttpOnly = true,
                        SameSite = SameSiteMode.Lax,
                        Secure = context.Request.IsHttps,
                        Expires = DateTimeOffset.UtcNow.Add(NetworkPinGate.RememberFor),
                        Path = "/",
                        IsEssential = true,
                    });
                    return Results.LocalRedirect(target);
                case UnlockOutcome.LockedOut:
                    return Page(target, "Too many tries. Wait a minute, then try again.");
                default:
                    return Page(target, "That PIN is not right.");
            }
        });
    }

    /// <summary>Only a path on this site is followed after unlocking; anything else goes to the chat.</summary>
    public static string SafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && returnUrl[0] == '/' && returnUrl.Length > 1 && returnUrl[1] != '/' && returnUrl[1] != '\\'
            ? returnUrl
            : "/";

    private static IResult Page(string returnUrl, string? error)
    {
        var alert = error is null ? "" : $"""<div class="alert alert-danger py-2 small mt-3 mb-0" role="alert">{WebUtility.HtmlEncode(error)}</div>""";
        var html = $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
                <meta charset="utf-8" />
                <meta name="viewport" content="width=device-width, initial-scale=1.0" />
                <title>Unlock · ButterKnife</title>
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
                    <form method="post" action="{{NetworkPinGate.UnlockPath}}" class="card-body">
                        <h1 class="h5 mb-1">ButterKnife</h1>
                        <p class="text-body-secondary small">This ButterKnife asks for a PIN from other devices.</p>
                        <label class="form-label" for="pin">PIN</label>
                        <input id="pin" name="pin" type="password" class="form-control form-control-lg" autocomplete="current-password" autofocus required />
                        <input type="hidden" name="returnUrl" value="{{WebUtility.HtmlEncode(returnUrl)}}" />
                        {{alert}}
                        <button type="submit" class="btn btn-primary w-100 mt-3">Unlock</button>
                        <p class="form-text mt-3 mb-0">This device stays unlocked for 30 days.</p>
                    </form>
                </main>
            </body>
            </html>
            """;
        return Results.Content(html, "text/html; charset=utf-8", statusCode: error is null ? StatusCodes.Status200OK : StatusCodes.Status401Unauthorized);
    }
}
