using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using BiliIndexTranslate.App.Native;
using BiliIndexTranslate.Core.Diagnostics;
using BiliIndexTranslate.Core.Geometry;
using BiliIndexTranslate.Core.Selection;
using BiliIndexTranslate.Core.Translation;

namespace BiliIndexTranslate.App.Popup;

/// <summary>
/// Owns the reading card window(s) and turns anchors into screen positions. One window is reused
/// for the live session; pinned ("keep open") cards are detached copies, capped at three.
/// All placement is done in physical pixels through SetWindowPos so WPF's own DPI/position logic
/// never fights with the anchor math.
/// </summary>
internal sealed class PopupPresenter : IDisposable
{
    private const int MaxPinned = 3;

    private readonly IMonitorLocator _monitors;
    private readonly DiagnosticsLog _log;
    private readonly TimingStats _timings;
    private readonly List<PopupWindow> _pinned = new();
    private PopupWindow? _window;
    private AnchorRect? _anchor;
    private PlacementSide? _lastSide;
    private PxRect? _lastWindowRect;
    private bool _visible;
    private bool _placing;
    private double _textScale = 1.0;
    private bool _showOriginal;

    public PopupPresenter(IMonitorLocator monitors, DiagnosticsLog log, TimingStats timings)
    {
        _monitors = monitors;
        _log = log;
        _timings = timings;
    }

    public event Action? PinRequested;
    public event Action? CloseRequested;
    public event Action? RetryRequested;
    public event Action? SettingsRequested;
    public event Action<double>? TextScaleChanged;
    public event Action<bool>? ShowOriginalChanged;
    /// <summary>Fires whenever the set of visible card rectangles (live + pinned, physical px) changes.</summary>
    public event Action<PxRect[]>? VisibleRectsChanged;

    public bool IsVisible => _visible;

    public void Configure(double textScale, bool showOriginal)
    {
        _textScale = Math.Clamp(textScale, 0.8, 1.6);
        _showOriginal = showOriginal;
        if (_window is not null)
        {
            _window.ApplyTextScale(_textScale);
            _window.SetShowOriginal(_showOriginal);
        }
        foreach (var p in _pinned) p.ApplyTextScale(_textScale);
    }

    private PopupWindow Window
    {
        get
        {
            if (_window is not null) return _window;
            _window = CreateWindow();
            return _window;
        }
    }

    private PopupWindow CreateWindow()
    {
        var w = new PopupWindow();
        w.EnsureHandle();
        w.ApplyTextScale(_textScale);
        w.SetShowOriginal(_showOriginal);
        w.PinRequested += () => PinRequested?.Invoke();
        w.CloseRequested += () => { if (ReferenceEquals(w, _window)) CloseRequested?.Invoke(); else ClosePinned(w); };
        w.RetryRequested += () => RetryRequested?.Invoke();
        w.SettingsRequested += () => SettingsRequested?.Invoke();
        w.OriginalToggled += show => { _showOriginal = show; ShowOriginalChanged?.Invoke(show); Replace(); };
        w.SizeChanged += (_, _) => { if (ReferenceEquals(w, _window) && _visible && !_placing) Replace(); };
        w.Moved += PublishRects;
        return w;
    }

    // ---- state entry points (UI thread) ----------------------------------------------------------

    public void ShowLoading(SelectionSnapshot snapshot, string? caption)
    {
        if (snapshot.Anchor is null) return;
        var w = Window;
        w.ShowLoading(snapshot, caption);
        Present(snapshot.Anchor, firstShow: !_visible);
    }

    public void ShowTranslation(SelectionSnapshot snapshot, TranslationOutcome.Success result, string? caption)
    {
        if (snapshot.Anchor is null) return;
        var sw = Stopwatch.StartNew();
        var w = Window;
        w.ShowTranslation(snapshot, result.Translation, caption);
        Present(snapshot.Anchor, firstShow: !_visible);
        _timings.Record(TimingCategory.Render, sw.Elapsed.TotalMilliseconds);
    }

    public void ShowError(SelectionSnapshot snapshot, TranslationError error, string? caption)
    {
        if (snapshot.Anchor is null) return;
        var w = Window;
        w.ShowError(snapshot, error, caption);
        Present(snapshot.Anchor, firstShow: !_visible);
    }

    public void UpdateAnchor(AnchorRect anchor)
    {
        if (!_visible || _window is null) return;
        if (_anchor is { } old && old.FirstLine == anchor.FirstLine && old.Union == anchor.Union) return;
        _anchor = anchor;
        Place(animate: false);
    }

    public void Hide()
    {
        if (_window is null) return;
        _visible = false;
        _anchor = null;
        _lastSide = null;
        _lastWindowRect = null;
        if (_window.IsVisible) _window.Hide();
        _window.ResetOpacity();
        PublishRects();
    }

    /// <summary>Converts the live card into a pinned window and starts a fresh live window for the next selection.</summary>
    public void Detach()
    {
        if (_window is null || !_visible) return;
        var pinned = _window;
        _window = null;
        _visible = false;
        _anchor = null;
        _lastSide = null;
        _lastWindowRect = null;
        pinned.SetPinned(true);
        _pinned.Add(pinned);
        while (_pinned.Count > MaxPinned) ClosePinned(_pinned[0]);
        PublishRects();
        _log.Info("popup", $"pinned ({_pinned.Count} open)");
    }

    public void HandleWheel(int delta, bool ctrl)
    {
        if (ctrl)
        {
            var next = Math.Clamp(Math.Round((_textScale + (delta > 0 ? 0.1 : -0.1)) * 10) / 10, 0.8, 1.6);
            if (Math.Abs(next - _textScale) < 0.01) return;
            _textScale = next;
            _window?.ApplyTextScale(next);
            foreach (var p in _pinned) p.ApplyTextScale(next);
            TextScaleChanged?.Invoke(next);
            return;
        }
        // Scroll whichever of our windows is under the cursor.
        NativeMethods.GetCursorPos(out var pt);
        var target = AllWindows().FirstOrDefault(w => w.CardPhysicalRect() is { } r && r.Contains(pt.X, pt.Y));
        target?.ScrollBy(delta);
    }

    /// <summary>True when the point lies inside any of our visible cards (live or pinned). The transparent shadow halo does not count.</summary>
    public bool HitTest(PxPoint p) => AllWindows().Any(w => w.CardPhysicalRect() is { } r && r.Contains(p));

    private void PublishRects()
    {
        var rects = AllWindows().Select(w => w.CardPhysicalRect()).Where(r => r.HasValue).Select(r => r!.Value).ToArray();
        VisibleRectsChanged?.Invoke(rects);
    }

    public bool IsOurWindow(IntPtr hwnd) => hwnd != IntPtr.Zero && AllWindows().Any(w => w.Hwnd == hwnd);

    public PxRect? LiveRect() => _visible ? _window?.PhysicalRect() : null;

    private IEnumerable<PopupWindow> AllWindows()
    {
        if (_window is not null && _visible) yield return _window;
        foreach (var p in _pinned) yield return p;
    }

    private void ClosePinned(PopupWindow w)
    {
        _pinned.Remove(w);
        try { w.Close(); } catch (Exception) { /* already closed */ }
        PublishRects();
    }

    // ---- placement ---------------------------------------------------------------------------------

    private void Present(AnchorRect anchor, bool firstShow)
    {
        _anchor = anchor;
        var w = Window;
        var monitor = _monitors.FromRect(anchor.Union);
        ApplySizeLimits(w, monitor);

        if (firstShow || !w.IsVisible)
        {
            // Park the (still fully transparent) window on the target monitor so WPF lays it out at that DPI.
            w.ResetOpacity();
            NativeMethods.SetWindowPos(w.Hwnd, NativeMethods.HWND_TOPMOST, anchor.FirstLine.Left, anchor.FirstLine.Top, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            _placing = true;
            try { w.Show(); }
            finally { _placing = false; }
            _visible = true;
            Place(animate: true);
        }
        else
        {
            _visible = true;
            Place(animate: false);
        }
    }

    private void Replace()
    {
        if (_anchor is { } a && _visible) { Place(animate: false); }
    }

    private void Place(bool animate)
    {
        if (_window is null || _anchor is not { } anchor) return;
        _placing = true;
        try
        {
            _window.UpdateLayout();
            if (!NativeMethods.GetWindowRect(_window.Hwnd, out var wr)) return;
            var scale = _window.DpiScale();
            var inset = (int)Math.Round(PopupWindow.ShadowMarginLogical * scale);
            var windowW = wr.Right - wr.Left;
            var windowH = wr.Bottom - wr.Top;
            var cardW = Math.Max(1, windowW - 2 * inset);
            var cardH = Math.Max(1, windowH - 2 * inset);
            var monitor = _monitors.FromRect(anchor.Union);
            var placement = PopupPlacer.Place(anchor, cardW, cardH, PxInsets.Uniform(inset), monitor, _lastSide);
            _lastSide = placement.Side;

            var rect = placement.WindowRect;
            if (_lastWindowRect is { } last && last == rect && _window.Opacity > 0) return;
            _lastWindowRect = rect;
            NativeMethods.SetWindowPos(_window.Hwnd, NativeMethods.HWND_TOPMOST, rect.Left, rect.Top, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

            // If the move crossed into a monitor with a different DPI, WPF re-lays out and SizeChanged re-places us.
            var monitorAfter = _monitors.FromRect(rect);
            if (Math.Abs(monitorAfter.Scale - monitor.Scale) > 0.01) ApplySizeLimits(_window, monitorAfter);

            if (animate || _window.Opacity < 1) _window.FadeIn();
            PublishRects();
        }
        finally { _placing = false; }
    }

    private static void ApplySizeLimits(PopupWindow w, MonitorArea monitor)
    {
        var (maxW, maxH) = PopupPlacer.MaxCardSize(monitor);
        var scale = monitor.Scale;
        // Body height leaves room for footer/padding (~56 logical px).
        w.SetSizeLimits(maxW / scale, Math.Max(48, maxH / scale - 56));
    }

    public void Dispose()
    {
        foreach (var p in _pinned.ToList()) ClosePinned(p);
        if (_window is not null)
        {
            try { _window.Close(); } catch (Exception) { }
            _window = null;
        }
    }
}
