using ButterKnife.Components;
using ButterKnife.Data;
using ButterKnife.Services;

var builder = WebApplication.CreateBuilder(args);

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

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
