using System.Text;
using System.Windows;
using System.Windows.Threading;
using BiliIndexTranslate.Core.Diagnostics;

namespace BiliIndexTranslate.App.Diagnostics;

public partial class DiagnosticsWindow : Window
{
    private readonly DiagnosticsLog _log;
    private readonly TimingStats _timings;
    private readonly AppSupportRegistry _registry;
    private readonly Func<string> _header;
    private readonly DispatcherTimer _refresh;
    private bool _dirty = true;

    public DiagnosticsWindow(DiagnosticsLog log, TimingStats timings, AppSupportRegistry registry, Func<string> header)
    {
        InitializeComponent();
        _log = log;
        _timings = timings;
        _registry = registry;
        _header = header;
        _refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _refresh.Tick += (_, _) => { if (_dirty) Render(); };
        _log.EntryAdded += OnEntry;
        Loaded += (_, _) => { Render(); _refresh.Start(); };
        Closed += (_, _) => { _refresh.Stop(); _log.EntryAdded -= OnEntry; };
    }

    private void OnEntry(DiagnosticEntry entry) => _dirty = true;

    private void Render()
    {
        _dirty = false;
        Summary.Text = _header();
        TimingsBox.Text = RenderTimings();
        AppsBox.Text = RenderApps();
        var atEnd = LogBox.VerticalOffset >= LogBox.ExtentHeight - LogBox.ViewportHeight - 4;
        LogBox.Text = _log.Render();
        if (atEnd) LogBox.ScrollToEnd();
    }

    private string RenderTimings()
    {
        var all = _timings.All();
        if (all.Count == 0) return "no samples yet";
        var sb = new StringBuilder();
        sb.AppendLine($"{"category",-9} {"n",5} {"p50",8} {"p95",8} {"max",8} {"last",8}");
        foreach (var t in all)
            sb.AppendLine($"{t.Category,-9} {t.Count,5} {t.P50,8:0.0} {t.P95,8:0.0} {t.Max,8:0.0} {t.Last,8:0.0}");
        return sb.ToString().TrimEnd();
    }

    private string RenderApps()
    {
        var apps = _registry.Snapshot();
        if (apps.Count == 0) return "no selections observed yet";
        var sb = new StringBuilder();
        foreach (var a in apps) sb.AppendLine(a.ToString());
        return sb.ToString().TrimEnd();
    }

    public string BuildReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine(_header());
        sb.AppendLine();
        sb.AppendLine("== Timings ==");
        sb.AppendLine(RenderTimings());
        sb.AppendLine();
        sb.AppendLine("== Applications ==");
        sb.AppendLine(RenderApps());
        sb.AppendLine();
        sb.AppendLine("== Log ==");
        sb.Append(_log.Render());
        return sb.ToString();
    }

    private void OnCopyReport(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetDataObject(BuildReport(), true); }
        catch (Exception) { /* clipboard busy */ }
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        LogBox.Clear();
        _log.Info("diag", "log view cleared (buffer keeps rolling)");
    }
}
