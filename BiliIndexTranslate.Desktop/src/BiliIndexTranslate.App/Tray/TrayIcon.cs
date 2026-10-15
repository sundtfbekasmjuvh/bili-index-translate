using System.Drawing;
using System.Windows.Forms;

namespace BiliIndexTranslate.App.Tray;

/// <summary>
/// Notification-area icon and its compact menu. Uses WinForms NotifyIcon (WPF has none).
/// The icon switches to a muted variant while automatic translation is paused.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _autoItem;
    private readonly ToolStripMenuItem _appItem;
    private readonly Icon _normal;
    private readonly Icon _paused;
    private Func<string?>? _currentAppProvider;

    public TrayIcon()
    {
        _normal = LoadIcon("app.ico");
        _paused = LoadIcon("app-paused.ico");

        _menu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };
        _autoItem = new ToolStripMenuItem("Translate selections automatically") { CheckOnClick = true };
        _autoItem.CheckedChanged += (_, _) => AutoTranslateToggled?.Invoke(_autoItem.Checked);
        _appItem = new ToolStripMenuItem("Don't translate in this app") { CheckOnClick = true, Enabled = false };
        _appItem.Click += (_, _) => { if (_appItem.Tag is string app) ExcludeAppToggled?.Invoke(app, _appItem.Checked); };

        var clipboard = new ToolStripMenuItem("Translate clipboard text");
        clipboard.Click += (_, _) => TranslateClipboardRequested?.Invoke();
        var settings = new ToolStripMenuItem("Settings…");
        settings.Click += (_, _) => SettingsRequested?.Invoke();
        var diagnostics = new ToolStripMenuItem("Diagnostics…");
        diagnostics.Click += (_, _) => DiagnosticsRequested?.Invoke();
        var welcome = new ToolStripMenuItem("Welcome and demo…");
        welcome.Click += (_, _) => WelcomeRequested?.Invoke();
        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => ExitRequested?.Invoke();

        _menu.Items.AddRange(new ToolStripItem[]
        {
            _autoItem, _appItem, new ToolStripSeparator(),
            clipboard, new ToolStripSeparator(),
            settings, diagnostics, welcome, new ToolStripSeparator(),
            exit,
        });
        _menu.Opening += (_, _) => RefreshAppItem();

        _icon = new NotifyIcon
        {
            Icon = _normal,
            Text = "Bili Index Translate",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _icon.DoubleClick += (_, _) => SettingsRequested?.Invoke();
    }

    public event Action<bool>? AutoTranslateToggled;
    public event Action<string, bool>? ExcludeAppToggled;
    public event Action? TranslateClipboardRequested;
    public event Action? SettingsRequested;
    public event Action? DiagnosticsRequested;
    public event Action? WelcomeRequested;
    public event Action? ExitRequested;

    /// <summary>Supplies the name of the application the user is most likely referring to (last probed / foreground).</summary>
    public void SetCurrentAppProvider(Func<string?> provider) => _currentAppProvider = provider;

    public Func<string, bool>? IsAppExcluded { get; set; }

    public void SetAutoTranslate(bool enabled)
    {
        if (_autoItem.Checked != enabled) _autoItem.Checked = enabled;
        _icon.Icon = enabled ? _normal : _paused;
        _icon.Text = enabled ? "Bili Index Translate" : "Bili Index Translate — paused";
    }

    public void ShowNotice(string title, string message, bool warning = false)
    {
        try
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = message;
            _icon.BalloonTipIcon = warning ? ToolTipIcon.Warning : ToolTipIcon.Info;
            _icon.ShowBalloonTip(4000);
        }
        catch (Exception) { /* notifications may be disabled */ }
    }

    private void RefreshAppItem()
    {
        var app = _currentAppProvider?.Invoke();
        if (string.IsNullOrEmpty(app))
        {
            _appItem.Text = "Don't translate in this app";
            _appItem.Enabled = false;
            _appItem.Checked = false;
            _appItem.Tag = null;
            return;
        }
        _appItem.Text = $"Don't translate in {app}";
        _appItem.Enabled = true;
        _appItem.Tag = app;
        _appItem.Checked = IsAppExcluded?.Invoke(app) ?? false;
    }

    private static Icon LoadIcon(string name)
    {
        try
        {
            var uri = new Uri($"pack://application:,,,/BiliIndexTranslate;component/Assets/{name}");
            var info = System.Windows.Application.GetResourceStream(uri);
            if (info is not null)
            {
                using var stream = info.Stream;
                return new Icon(stream);
            }
        }
        catch (Exception) { }
        return SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        if (!ReferenceEquals(_normal, SystemIcons.Application)) _normal.Dispose();
        if (!ReferenceEquals(_paused, SystemIcons.Application)) _paused.Dispose();
    }
}
