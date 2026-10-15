using System.Windows.Threading;
using BiliIndexTranslate.App.Native;
using BiliIndexTranslate.Core.Geometry;

namespace BiliIndexTranslate.App.Tracking;

/// <summary>
/// Watches the source window while a popup is visible: WinEvents (move/size/minimize/destroy/hide,
/// filtered to the source process) plus a light periodic check and a geometry refresh request.
/// Also owns the global foreground-change hook. Everything runs on the UI thread (WinEvent
/// callbacks are delivered to the thread that installed the hook).
/// </summary>
internal sealed class SourceWindowTracker : IDisposable
{
    private readonly WinEventDelegate _foregroundProc;
    private readonly WinEventDelegate _sourceProc;
    private readonly DispatcherTimer _tick;
    private IntPtr _foregroundHook;
    private IntPtr _sourceLocationHook;
    private IntPtr _sourceStateHook;
    private IntPtr _root;
    private int _pid;
    private RECT _lastRect;
    private long _lastRefreshTick;
    private bool _refreshInFlight;

    public SourceWindowTracker()
    {
        _foregroundProc = OnForegroundEvent;
        _sourceProc = OnSourceEvent;
        _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        _tick.Tick += (_, _) => OnTick();
        _foregroundHook = NativeMethods.SetWinEventHook(NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _foregroundProc, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);
    }

    /// <summary>Foreground window changed (any process). Argument: the new foreground hwnd.</summary>
    public event Action<IntPtr>? ForegroundChanged;

    /// <summary>The source window was minimized, hidden, cloaked or destroyed.</summary>
    public event Action<string>? SourceLost;

    /// <summary>Geometry may have changed: the owner should ask the observer for a refresh.</summary>
    public event Action? RefreshRequested;

    public bool IsTracking => _root != IntPtr.Zero;
    public IntPtr Root => _root;

    public void Begin(IntPtr root, int pid)
    {
        End();
        if (root == IntPtr.Zero) return;
        _root = root;
        _pid = pid;
        NativeMethods.GetWindowRect(root, out _lastRect);
        _sourceLocationHook = NativeMethods.SetWinEventHook(NativeMethods.EVENT_OBJECT_LOCATIONCHANGE, NativeMethods.EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, _sourceProc, (uint)pid, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);
        _sourceStateHook = NativeMethods.SetWinEventHook(NativeMethods.EVENT_SYSTEM_MOVESIZESTART, NativeMethods.EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, _sourceProc, (uint)pid, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);
        _lastRefreshTick = Environment.TickCount64;
        _refreshInFlight = false;
        _tick.Start();
    }

    public void End()
    {
        _tick.Stop();
        if (_sourceLocationHook != IntPtr.Zero) { NativeMethods.UnhookWinEvent(_sourceLocationHook); _sourceLocationHook = IntPtr.Zero; }
        if (_sourceStateHook != IntPtr.Zero) { NativeMethods.UnhookWinEvent(_sourceStateHook); _sourceStateHook = IntPtr.Zero; }
        _root = IntPtr.Zero;
        _pid = 0;
        _refreshInFlight = false;
    }

    /// <summary>Called by the owner when a refresh result arrived, so the next one may be requested.</summary>
    public void RefreshCompleted() => _refreshInFlight = false;

    public PxRect? CurrentRootRect()
    {
        if (_root == IntPtr.Zero || !NativeMethods.GetWindowRect(_root, out var r)) return null;
        return new PxRect(r.Left, r.Top, r.Right, r.Bottom);
    }

    private void OnForegroundEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (eventType == NativeMethods.EVENT_SYSTEM_FOREGROUND) ForegroundChanged?.Invoke(hwnd);
    }

    private void OnSourceEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (_root == IntPtr.Zero) return;
        switch (eventType)
        {
            case NativeMethods.EVENT_OBJECT_LOCATIONCHANGE:
                if (idObject == NativeMethods.OBJID_CARET) { RequestRefresh(force: false); return; }
                if (idObject != NativeMethods.OBJID_WINDOW) return;
                if (hwnd == _root || NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT) == _root) RequestRefresh(force: true);
                return;
            case NativeMethods.EVENT_SYSTEM_MOVESIZESTART:
            case NativeMethods.EVENT_SYSTEM_MOVESIZEEND:
                if (hwnd == _root) RequestRefresh(force: true);
                return;
            case NativeMethods.EVENT_SYSTEM_SCROLLINGSTART:
            case NativeMethods.EVENT_SYSTEM_SCROLLINGEND:
                RequestRefresh(force: true);
                return;
            case NativeMethods.EVENT_SYSTEM_MINIMIZESTART:
                if (hwnd == _root) SourceLost?.Invoke("source minimized");
                return;
        }
    }

    private void OnTick()
    {
        if (_root == IntPtr.Zero) return;
        if (!NativeMethods.IsWindow(_root)) { SourceLost?.Invoke("source window closed"); return; }
        if (NativeMethods.IsIconic(_root)) { SourceLost?.Invoke("source minimized"); return; }
        if (!NativeMethods.IsWindowVisible(_root)) { SourceLost?.Invoke("source hidden"); return; }

        if (NativeMethods.GetWindowRect(_root, out var now))
        {
            var moved = now.Left != _lastRect.Left || now.Top != _lastRect.Top || now.Right != _lastRect.Right || now.Bottom != _lastRect.Bottom;
            _lastRect = now;
            if (moved) { RequestRefresh(force: true); return; }
        }
        RequestRefresh(force: false);
    }

    private void RequestRefresh(bool force)
    {
        var nowTick = Environment.TickCount64;
        if (_refreshInFlight && nowTick - _lastRefreshTick < 1000) return; // one at a time; stalled refreshes time out after 1 s
        if (!force && nowTick - _lastRefreshTick < 180) return;
        _refreshInFlight = true;
        _lastRefreshTick = nowTick;
        RefreshRequested?.Invoke();
    }

    public void Dispose()
    {
        End();
        if (_foregroundHook != IntPtr.Zero) { NativeMethods.UnhookWinEvent(_foregroundHook); _foregroundHook = IntPtr.Zero; }
    }
}
