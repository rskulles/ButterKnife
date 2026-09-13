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
        // The entry point is a plain Main (no [STAThread]), but the clipboard and context menus need a
        // single-threaded-apartment thread with a Windows Forms message loop, so the tray gets its own.
        var ui = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            Application.EnableVisualStyles();
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.Run(new TrayContext(app));
        })
        {
            Name = "ButterKnife tray",
            IsBackground = false,
        };
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        ui.Join();
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

            _copy = new ToolStripMenuItem("Copy address for phone", null, (_, _) => Guarded(CopyAddress)) { Enabled = false };
            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("Open ButterKnife", null, (_, _) => Guarded(() => DesktopLauncher.OpenBrowser(_localUrl))) { Font = new Font(menu.Font, FontStyle.Bold) });
            menu.Items.Add(_copy);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Quit ButterKnife", null, (_, _) => _app.Lifetime.StopApplication()));

            _icon = new NotifyIcon
            {
                Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application,
                Text = "ButterKnife (starting…)",
                ContextMenuStrip = menu, // right-click; NotifyIcon handles focus so the menu closes when clicking away
                Visible = true,
            };
            // Left click opens the app; right click is the menu.
            _icon.MouseClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    Guarded(() => DesktopLauncher.OpenBrowser(_localUrl));
                }
            };

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

        private void CopyAddress()
        {
            var text = _lanUrl ?? _localUrl;
            Clipboard.SetText(text);
            _icon.ShowBalloonTip(3000, "Copied", $"{text}\nOpen this on a phone on the same Wi-Fi.", ToolTipIcon.Info);
        }

        /// <summary>Menu actions must never take the tray down; report problems as a notification instead.</summary>
        private void Guarded(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                _icon.ShowBalloonTip(5000, "ButterKnife", ex.Message, ToolTipIcon.Error);
            }
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
