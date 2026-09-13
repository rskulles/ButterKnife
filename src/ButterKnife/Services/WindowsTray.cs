#if WINDOWS
using System.Drawing;
using System.Windows.Forms;

namespace ButterKnife.Services;

/// <summary>
/// Windows release build only (net10.0-windows, OutputType WinExe): no console window; a tray icon with Open, Copy
/// address for phone, and Quit; Kestrel runs on the thread pool while the UI thread pumps messages. Quitting from
/// the tray or from Settings → General both go through IHostApplicationLifetime, so either ends the process.
/// </summary>
public static class WindowsTray
{
    public static void Run(WebApplication app)
    {
        var context = new TrayContext(app);
        Application.EnableVisualStyles();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.Run(context);
    }

    private sealed class TrayContext : ApplicationContext
    {
        private readonly WebApplication _app;
        private readonly NotifyIcon _icon;
        private readonly ToolStripMenuItem _copy;
        private string _localUrl = DesktopLauncher.DefaultUrl;
        private string? _lanUrl;

        public TrayContext(WebApplication app)
        {
            _app = app;

            _copy = new ToolStripMenuItem("Copy address for phone", null, (_, _) => Clipboard.SetText(_lanUrl ?? _localUrl)) { Enabled = false };
            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("Open ButterKnife", null, (_, _) => DesktopLauncher.OpenBrowser(_localUrl)) { Font = new Font(menu.Font, FontStyle.Bold) });
            menu.Items.Add(_copy);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Quit ButterKnife", null, (_, _) => _app.Lifetime.StopApplication()));

            _icon = new NotifyIcon
            {
                Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application,
                Text = "ButterKnife (starting…)",
                ContextMenuStrip = menu,
                Visible = true,
            };
            _icon.DoubleClick += (_, _) => DesktopLauncher.OpenBrowser(_localUrl);

            var ui = SynchronizationContext.Current!;
            _app.Lifetime.ApplicationStarted.Register(() => ui.Post(_ => OnStarted(), null));
            _app.Lifetime.ApplicationStopped.Register(() => ui.Post(_ => ExitThread(), null));

            _ = Task.Run(async () =>
            {
                try
                {
                    await _app.RunAsync();
                }
                catch (Exception ex)
                {
                    ui.Post(_ =>
                    {
                        MessageBox.Show(ex.Message, "ButterKnife could not start", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        ExitThread();
                    }, null);
                }
            });
        }

        private void OnStarted()
        {
            var (local, lan) = DesktopLauncher.Addresses(_app);
            _localUrl = local;
            _lanUrl = lan;
            _copy.Enabled = lan is not null;
            _icon.Text = lan is null ? $"ButterKnife at {local}" : $"ButterKnife at {local} (network: {lan})";
            if (Environment.GetEnvironmentVariable(DesktopLauncher.NoBrowserVariable) is null)
            {
                DesktopLauncher.OpenBrowser(local);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _icon.Visible = false;
                _icon.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
#endif
