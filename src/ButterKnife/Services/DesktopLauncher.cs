using System.Diagnostics;
using Microsoft.AspNetCore.DataProtection;

namespace ButterKnife.Services;

/// <summary>
/// "Download and run" behaviour for the published single-file build. A bundle is recognised by the entry assembly
/// having no <c>Location</c>; in that mode the folder holding the executable is the content root (so wwwroot and
/// data/ sit beside it whatever the working directory), Data Protection keys live in data/keys so stored API keys
/// survive moving the folder, the default port avoids macOS AirPlay on 5000, and the browser opens on start.
/// Development (dotnet run) is untouched.
/// </summary>
public static class DesktopLauncher
{
    public const string DefaultUrl = "http://localhost:5175";

    /// <summary>Set BUTTERKNIFE_NO_BROWSER=1 to keep the browser closed, e.g. when running as a service.</summary>
    public const string NoBrowserVariable = "BUTTERKNIFE_NO_BROWSER";

    /// <summary>Set by the macOS menu bar helper: if that process disappears, the server stops itself rather than run orphaned.</summary>
    public const string ParentPidVariable = "BUTTERKNIFE_PARENT_PID";

    // IL3000 warns that Location is empty inside a single-file bundle. That is the documented behaviour this relies on.
#pragma warning disable IL3000
    public static bool IsBundled { get; } = string.IsNullOrEmpty(System.Reflection.Assembly.GetEntryAssembly()?.Location);
#pragma warning restore IL3000

    /// <summary>
    /// Inside a macOS .app the executable sits in Contents/MacOS; tools/make-macos-app.sh puts everything else in
    /// Contents/Resources. A signed bundle must never be written to, so data goes to Application Support instead.
    /// </summary>
    public static bool IsMacAppBundle { get; } = IsBundled && OperatingSystem.IsMacOS()
        && AppContext.BaseDirectory.TrimEnd('/').EndsWith("/Contents/MacOS", StringComparison.Ordinal);

    /// <summary>Where wwwroot and appsettings.json live: Contents/Resources in a .app, else beside the executable.</summary>
    public static string? ContentRoot => !IsBundled ? null
        : IsMacAppBundle ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources"))
        : AppContext.BaseDirectory;

    /// <summary>Database, key ring and logs: ~/Library/Application Support/ButterKnife in a .app, else ./data beside the executable.</summary>
    public static string? DataDirectory => !IsBundled ? null
        : IsMacAppBundle ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "ButterKnife")
        : Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>The static-assets manifest is looked up beside the executable by default; in a .app it is in Resources.</summary>
    public static string? StaticAssetsManifestPath => IsMacAppBundle ? Path.Combine(ContentRoot!, "ButterKnife.staticwebassets.endpoints.json") : null;

    public static WebApplicationOptions Options(string[] args) => new()
    {
        Args = args,
        ContentRootPath = ContentRoot,
    };

    public static void Configure(WebApplicationBuilder builder)
    {
        if (!IsBundled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(builder.Configuration[WebHostDefaults.ServerUrlsKey]))
        {
            builder.Configuration[WebHostDefaults.ServerUrlsKey] = DefaultUrl;
        }

        var data = DataDirectory!;
        Directory.CreateDirectory(data);

#if WINDOWS
        // No console in the Windows build: keep the log in a file so problems can still be diagnosed.
        Directory.CreateDirectory(Path.Combine(data, "logs"));
        Console.SetOut(new StreamWriter(Path.Combine(data, "logs", "butterknife.log"), append: true) { AutoFlush = true });
        Console.SetError(Console.Out);
#endif
        if (builder.Configuration["Database:ConnectionString"] is null or "Data Source=data/butterknife.db")
        {
            builder.Configuration["Database:ConnectionString"] = $"Data Source={Path.Combine(data, "butterknife.db")}";
        }

        var keys = builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(data, "keys")));
        if (OperatingSystem.IsWindows())
        {
            keys.ProtectKeysWithDpapi();
        }
    }

    /// <summary>The address to open locally (wildcard hosts become localhost) and, if reachable, the first LAN address.</summary>
    public static (string Local, string? Lan) Addresses(WebApplication app)
    {
        var first = app.Urls.FirstOrDefault() ?? DefaultUrl;
        var local = new UriBuilder(first);
        if (local.Host is "0.0.0.0" or "*" or "+" or "[::]" or "::")
        {
            local.Host = "localhost";
        }
        var lan = app.Services.GetRequiredService<LanAddressService>().ForPage(local.Uri);
        return (local.Uri.ToString(), lan.ListensOnLan && lan.Urls.Count > 0 ? lan.Urls[0].ToString() : null);
    }

    public static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // No default browser (or headless); the address is logged and shown in the tray/menu bar anyway.
        }
    }

    /// <summary>
    /// Runs the host. Windows release builds pump a tray icon on the UI thread instead (see WindowsTray); everywhere
    /// else the bundle logs where it is and opens the browser once listening (the macOS menu bar helper and anyone
    /// scripting the server set BUTTERKNIFE_NO_BROWSER and open it themselves).
    /// </summary>
    public static void Run(WebApplication app)
    {
#if WINDOWS
        if (IsBundled)
        {
            WindowsTray.Run(app);
            return;
        }
#endif
        if (IsBundled)
        {
            WatchParent(app);
            app.Lifetime.ApplicationStarted.Register(() =>
            {
                var (local, lan) = Addresses(app);
                app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ButterKnife")
                    .LogInformation("ButterKnife is running at {Url}{Lan}. Press Ctrl+C to stop.", local, lan is null ? "" : $" (on your network: {lan})");
                if (Environment.GetEnvironmentVariable(NoBrowserVariable) is null)
                {
                    OpenBrowser(local);
                }
            });
        }
        app.Run();
    }

    private static void WatchParent(WebApplication app)
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable(ParentPidVariable), out var parentPid))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            while (await timer.WaitForNextTickAsync(app.Lifetime.ApplicationStopping))
            {
                bool alive;
                try
                {
                    using var parent = Process.GetProcessById(parentPid);
                    alive = !parent.HasExited;
                }
                catch (ArgumentException)
                {
                    alive = false;
                }

                if (!alive)
                {
                    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ButterKnife")
                        .LogInformation("The menu bar helper (pid {Pid}) is gone; stopping.", parentPid);
                    app.Lifetime.StopApplication();
                    return;
                }
            }
        });
    }
}
