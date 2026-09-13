using ButterKnife.Components;
using ButterKnife.Data;
using ButterKnife.Services;

// Published single-file builds get "download and run" defaults (content root beside the executable, key ring in
// data/keys, port 5175, browser on start); see DesktopLauncher. dotnet run is unaffected.
var builder = WebApplication.CreateBuilder(DesktopLauncher.Options(args));
DesktopLauncher.Configure(builder);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddLlmBackends(builder.Configuration);
builder.Services.AddDataStores(builder.Configuration);
builder.Services.AddSingleton<LanAddressService>(); // "open on your phone" QR code

var app = builder.Build();

// Settings → General → "Reachable on the local network": decides the host part of the listen addresses. Set here,
// before the host starts, so it wins over --urls / ASPNETCORE_URLS; unset leaves the configured addresses alone.
if (await app.Services.GetRequiredService<ISettingsStore>().GetAsync(SettingKeys.ListenOnLan) is { } listenOnLan)
{
    var urls = ListenAddresses.Apply(app.Configuration[WebHostDefaults.ServerUrlsKey], listenOnLan == "true");
    app.Urls.Clear();
    foreach (var url in urls)
    {
        app.Urls.Add(url);
    }
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets(DesktopLauncher.StaticAssetsManifestPath);

// Settings → Data → "Download a backup": a consistent snapshot of the database, streamed as a file and deleted
// once sent. A plain GET so the browser handles the download itself (no circuit round trip for a large file).
app.MapGet("/backup", async (SqliteDatabase database, CancellationToken cancellationToken) =>
{
    if (database.DataSourcePath is null)
    {
        return Results.NotFound();
    }

    var snapshot = Path.Combine(Path.GetTempPath(), $"butterknife-backup-{Guid.NewGuid():N}.db");
    await database.BackupToAsync(snapshot, cancellationToken);
    var stream = new FileStream(snapshot, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
    return Results.File(stream, "application/vnd.sqlite3", $"ButterKnife-backup-{DateTime.Now:yyyy-MM-dd}.db");
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

DesktopLauncher.Run(app);
