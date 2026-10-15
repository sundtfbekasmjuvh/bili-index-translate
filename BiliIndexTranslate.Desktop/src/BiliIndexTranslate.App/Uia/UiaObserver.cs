using System.Diagnostics;
using System.Runtime.InteropServices;
using BiliIndexTranslate.Core.Diagnostics;
using BiliIndexTranslate.Core.Selection;
using BiliIndexTranslate.Core.Session;
using Interop.UIAutomationClient;

namespace BiliIndexTranslate.App.Uia;

/// <summary>
/// Owns the single UI Automation thread (MTA). Work arrives through three "latest wins" slots
/// (probe, refresh, focus re-subscription); results are posted back to the UI thread. A stalled
/// provider blocks only this thread for at most the configured UIA timeouts; the UI side keeps its
/// own watchdog and simply ignores late results. No replacement threads are ever spawned.
/// </summary>
internal sealed class UiaObserver : IDisposable
{
    private readonly Action<Action> _post;
    private readonly DiagnosticsLog _log;
    private readonly TimingStats _timings;
    private readonly Thread _thread;
    private readonly AutoResetEvent _signal = new(false);
    private readonly object _gate = new();

    private (long ProbeId, ProbeMode Mode, ReaderPolicy Policy)? _probeSlot;
    private (long SelectionId, ReaderPolicy Policy)? _refreshSlot;
    private IUIAutomationElement? _focusSlot;
    private bool _clearTrackingRequested;
    private bool _resyncEvents;
    private volatile bool _stop;
    private volatile bool _selectionEventsEnabled;

    private IUIAutomation? _automation;
    private UiaSelectionReader? _reader;
    private FocusHandler? _focusHandler;
    private SelectionHandler? _selectionHandler;
    private IUIAutomationElement? _subscribedElement;

    public UiaObserver(Action<Action> post, DiagnosticsLog log, TimingStats timings, bool selectionEvents)
    {
        _post = post;
        _log = log;
        _timings = timings;
        _selectionEventsEnabled = selectionEvents;
        _thread = new Thread(Run) { Name = "uia-observer", IsBackground = true };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    /// <summary>Raised (on the UI thread) when UIA reports a selection change in the focused text control.</summary>
    public event Action? SelectionChangedByUia;

    public event Action<long, ProbeOutcome>? ProbeCompleted;
    public event Action<long, GeometryUpdate>? RefreshCompleted;

    public bool IsAlive => _thread.IsAlive;

    public void Start() => _thread.Start();

    public void SetSelectionEvents(bool enabled)
    {
        _selectionEventsEnabled = enabled;
        lock (_gate) { _focusSlot = null; _resyncEvents = true; }
        _signal.Set();
    }

    public void RequestProbe(long probeId, ProbeMode mode, ReaderPolicy policy)
    {
        lock (_gate) _probeSlot = (probeId, mode, policy);
        _signal.Set();
    }

    public void RequestRefresh(long selectionId, ReaderPolicy policy)
    {
        lock (_gate) _refreshSlot = (selectionId, policy);
        _signal.Set();
    }

    public void ClearTracking()
    {
        lock (_gate) { _clearTrackingRequested = true; _refreshSlot = null; }
        _signal.Set();
    }

    private void Run()
    {
        try
        {
            _automation = new CUIAutomation8Class();
            try
            {
                if (_automation is IUIAutomation2 a2)
                {
                    a2.ConnectionTimeout = 1500;
                    a2.TransactionTimeout = 2000;
                }
                if (_automation is IUIAutomation6 a6)
                {
                    a6.CoalesceEvents = CoalesceEventsOptions.CoalesceEventsOptions_Enabled;
                    a6.ConnectionRecoveryBehavior = ConnectionRecoveryBehaviorOptions.ConnectionRecoveryBehaviorOptions_Enabled;
                }
            }
            catch (Exception ex) { _log.Warn("uia", $"timeouts/coalescing not configurable: {ex.GetType().Name}"); }

            _reader = new UiaSelectionReader(_automation);
            _log.Info("uia", "observer thread ready");

            if (_selectionEventsEnabled) SubscribeFocus();
        }
        catch (Exception ex)
        {
            _log.Error("uia", $"UI Automation unavailable: {ex.GetType().Name}: {ex.Message}");
            FailPending($"UI Automation unavailable ({ex.GetType().Name})");
            return;
        }

        while (!_stop)
        {
            _signal.WaitOne();
            if (_stop) break;
            try { Drain(); }
            catch (Exception ex) { _log.Error("uia", $"observer loop: {ex.GetType().Name}: {ex.Message}"); }
        }

        try
        {
            UnsubscribeSelection();
            if (_focusHandler is not null) _automation!.RemoveFocusChangedEventHandler(_focusHandler);
            _automation?.RemoveAllEventHandlers();
        }
        catch (Exception) { /* shutting down */ }
        _log.Info("uia", "observer thread stopped");
    }

    private void Drain()
    {
        while (true)
        {
            (long, ProbeMode, ReaderPolicy)? probe;
            (long, ReaderPolicy)? refresh;
            IUIAutomationElement? focus;
            bool clear, resync;
            lock (_gate)
            {
                probe = _probeSlot; _probeSlot = null;
                refresh = _refreshSlot; _refreshSlot = null;
                focus = _focusSlot; _focusSlot = null;
                clear = _clearTrackingRequested; _clearTrackingRequested = false;
                resync = _resyncEvents; _resyncEvents = false;
            }
            if (probe is null && refresh is null && focus is null && !clear && !resync) return;

            if (clear) _reader!.ClearTracking();
            if (resync)
            {
                if (_selectionEventsEnabled && _focusHandler is null) SubscribeFocus();
                else if (!_selectionEventsEnabled) UnsubscribeSelection();
            }

            if (probe is { } p)
            {
                var sw = Stopwatch.StartNew();
                ProbeOutcome outcome;
                try { outcome = _reader!.Probe(p.Item3, p.Item2 == ProbeMode.Explicit); }
                catch (Exception ex) { outcome = new ProbeOutcome.Failed($"{ex.GetType().Name}: {ex.Message}"); }
                _timings.Record(TimingCategory.Probe, sw.Elapsed.TotalMilliseconds);
                var id = p.Item1;
                _post(() => ProbeCompleted?.Invoke(id, outcome));
            }

            if (refresh is { } r)
            {
                var sw = Stopwatch.StartNew();
                GeometryUpdate update;
                try { update = _reader!.Refresh(r.Item2); }
                catch (Exception ex) { update = new GeometryUpdate.Unavailable(ex.GetType().Name); }
                _timings.Record(TimingCategory.Refresh, sw.Elapsed.TotalMilliseconds);
                var id = r.Item1;
                _post(() => RefreshCompleted?.Invoke(id, update));
            }

            if (focus is not null) Resubscribe(focus);
        }
    }

    private void FailPending(string reason)
    {
        (long, ProbeMode, ReaderPolicy)? probe;
        lock (_gate) { probe = _probeSlot; _probeSlot = null; _refreshSlot = null; }
        if (probe is { } p)
        {
            var id = p.Item1;
            _post(() => ProbeCompleted?.Invoke(id, new ProbeOutcome.Failed(reason)));
        }
    }

    // ---- UIA events -----------------------------------------------------------------------------

    private void SubscribeFocus()
    {
        try
        {
            _focusHandler = new FocusHandler(this);
            _automation!.AddFocusChangedEventHandler(null, _focusHandler);
            _log.Info("uia", "focus-changed events subscribed");
        }
        catch (Exception ex)
        {
            _focusHandler = null;
            _log.Warn("uia", $"focus events unavailable: {ex.GetType().Name}");
        }
    }

    private void OnFocusChanged(IUIAutomationElement sender)
    {
        // Called on a UIA callback thread: never do UIA work here, hand the element to the observer thread.
        if (!_selectionEventsEnabled || _stop) return;
        lock (_gate) _focusSlot = sender;
        _signal.Set();
    }

    private void Resubscribe(IUIAutomationElement element)
    {
        if (!_selectionEventsEnabled) { UnsubscribeSelection(); return; }
        try
        {
            if (element.CurrentProcessId == Environment.ProcessId) return; // never watch our own windows
            var available = element.GetCurrentPropertyValue(UIA_PropertyIds.UIA_IsTextPatternAvailablePropertyId) is bool b && b;
            UnsubscribeSelection();
            if (!available) return;
            _selectionHandler ??= new SelectionHandler(this);
            _automation!.AddAutomationEventHandler(UIA_EventIds.UIA_Text_TextSelectionChangedEventId, element, TreeScope.TreeScope_Element, null, _selectionHandler);
            _subscribedElement = element;
        }
        catch (COMException ex)
        {
            _subscribedElement = null;
            _log.Info("uia", $"selection events not available for focused element (0x{ex.HResult:X8})");
        }
        catch (Exception ex)
        {
            _subscribedElement = null;
            _log.Warn("uia", $"selection event subscription: {ex.GetType().Name}");
        }
    }

    private void UnsubscribeSelection()
    {
        if (_subscribedElement is null || _selectionHandler is null) return;
        try { _automation!.RemoveAutomationEventHandler(UIA_EventIds.UIA_Text_TextSelectionChangedEventId, _subscribedElement, _selectionHandler); }
        catch (Exception) { /* element gone */ }
        _subscribedElement = null;
    }

    private void OnSelectionChangedEvent()
    {
        if (_stop) return;
        _post(() => SelectionChangedByUia?.Invoke());
    }

    [ComVisible(true)]
    private sealed class FocusHandler : IUIAutomationFocusChangedEventHandler
    {
        private readonly UiaObserver _owner;
        public FocusHandler(UiaObserver owner) { _owner = owner; }
        public void HandleFocusChangedEvent(IUIAutomationElement sender) => _owner.OnFocusChanged(sender);
    }

    [ComVisible(true)]
    private sealed class SelectionHandler : IUIAutomationEventHandler
    {
        private readonly UiaObserver _owner;
        public SelectionHandler(UiaObserver owner) { _owner = owner; }
        public void HandleAutomationEvent(IUIAutomationElement sender, int eventId) => _owner.OnSelectionChangedEvent();
    }

    public void Dispose()
    {
        _stop = true;
        _signal.Set();
        if (_thread.IsAlive && Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(2));
        _signal.Dispose();
    }
}
