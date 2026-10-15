using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using BiliIndexTranslate.App.Diagnostics;
using BiliIndexTranslate.App.Input;
using BiliIndexTranslate.App.Native;
using BiliIndexTranslate.App.Onboarding;
using BiliIndexTranslate.App.Popup;
using BiliIndexTranslate.App.Settings;
using BiliIndexTranslate.App.Themes;
using BiliIndexTranslate.App.Tracking;
using BiliIndexTranslate.App.Tray;
using BiliIndexTranslate.App.Uia;
using BiliIndexTranslate.Core.Diagnostics;
using BiliIndexTranslate.Core.Geometry;
using BiliIndexTranslate.Core.Selection;
using BiliIndexTranslate.Core.Session;
using BiliIndexTranslate.Core.Settings;
using BiliIndexTranslate.Core.Translation;

namespace BiliIndexTranslate.App;

/// <summary>
/// Composition root and effect executor. Everything here runs on the UI thread; the UIA observer,
/// input hooks and HTTP work post back through <see cref="Post"/>. The <see cref="ReadingSession"/>
/// decides, this class does.
/// </summary>
internal sealed class AppHost : IDisposable
{
    private const int ProbeWatchdogMs = 2500;
    private const int ClipboardWatchdogMs = 1500;
    private const int RefreshWatchdogMs = 1000;

    private readonly Application _app;
    private readonly Dispatcher _dispatcher;
    private readonly string _dataDir;
    private readonly bool _forceWelcome;

    private readonly DiagnosticsLog _log = new(300);
    private readonly TimingStats _timings = new(128);
    private readonly AppSupportRegistry _registry = new();
    private readonly SettingsStore _store;
    private AppSettings _settings = new AppSettings().Normalize();

    private readonly TranslationCache _cache = new(500, TimeSpan.FromHours(24));
    private readonly HttpTranslationTransport _transport = new();
    private readonly OpenAiCompatibleTranslator _translator;
    private readonly TranslationCoordinator _coordinator;

    private readonly ReadingSession _session = new();
    private readonly Queue<SessionEvent> _queue = new();
    private bool _dispatching;

    private readonly Win32MonitorLocator _monitors = new();
    private readonly ThemeManager _theme;
    private PopupPresenter _popup = null!;
    private UiaObserver _observer = null!;
    private InputHookThread _hooks = null!;
    private SourceWindowTracker _tracker = null!;
    private TrayIcon _tray = null!;

    private readonly DispatcherTimer _stabTimer = new(DispatcherPriority.Input);
    private readonly DispatcherTimer _probeWatchdog = new(DispatcherPriority.Background);
    private readonly DispatcherTimer _refreshWatchdog = new(DispatcherPriority.Background);
    private readonly DispatcherTimer _graceTimer = new(DispatcherPriority.Background);
    private readonly DispatcherTimer _saveTimer = new(DispatcherPriority.Background);

    private long _stabTimerId;
    private long _probeId;
    private ProbeMode _probeMode;
    private long _trackedSelectionId;
    private long _graceSelectionId;
    private string? _pendingClipboardText;
    private PxPoint _lastPointer;
    private long _lastPointerTick;
    private long _triggerTimestamp;
    private bool _anchorFromPointer;
    private string? _lastProcessName;
    private string? _hotkeyError;

    private SettingsWindow? _settingsWindow;
    private DiagnosticsWindow? _diagnosticsWindow;
    private WelcomeWindow? _welcomeWindow;
    private StreamWriter? _fileLog;
    private EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _activateWait;
    private bool _started;
    private bool _disposed;

    public AppHost(Application app, string dataDir, bool forceWelcome)
    {
        _app = app;
        _dispatcher = app.Dispatcher;
        _dataDir = dataDir;
        _forceWelcome = forceWelcome;
        _store = new SettingsStore(Path.Combine(dataDir, "settings.json"), new DpapiSecretProtector());
        _translator = new OpenAiCompatibleTranslator(_transport, TimeSpan.FromSeconds(20));
        _coordinator = new TranslationCoordinator(_translator, _cache, Post, _timings, _log, maxConcurrency: 2);
        _theme = new ThemeManager(app);
    }

    private void Post(Action action)
    {
        if (_disposed) return;
        _dispatcher.BeginInvoke(DispatcherPriority.Normal, action);
    }

    // ---- startup ----------------------------------------------------------------------------------

    public void Start()
    {
        if (_started) return;
        _started = true;
        _log.Info("app", $"Bili Index Translate {typeof(AppHost).Assembly.GetName().Version} starting on {Environment.OSVersion.VersionString}, .NET {Environment.Version}");

        var load = _store.Load();
        _settings = load.Settings;
        if (load.Note is not null) _log.Info("settings", load.Note);
        if (load.WasMigrated) { _log.Info("settings", "migrated to schema v2"); TrySave(); }
        ConfigureFileLogging(_settings.DiagnosticsFileLogging);

        _theme.Apply(_settings.Theme);
        _session.Options = new SessionOptions(_settings.AutoTranslate, _settings.StabilizationDelayMs);

        _popup = new PopupPresenter(_monitors, _log, _timings);
        _popup.Configure(_settings.TextScale, _settings.ShowOriginalByDefault);
        _popup.PinRequested += () => Dispatch(new SessionEvent.UserPinned());
        _popup.CloseRequested += () => Dispatch(new SessionEvent.UserClosed());
        _popup.RetryRequested += () => Dispatch(new SessionEvent.UserRetry());
        _popup.SettingsRequested += OpenSettings;
        _popup.TextScaleChanged += scale => { _settings.TextScale = scale; ScheduleSave(); };
        _popup.ShowOriginalChanged += show => { _settings.ShowOriginalByDefault = show; ScheduleSave(); };
        _popup.VisibleRectsChanged += rects => _hooks.PopupWheelRegions = rects;

        _observer = new UiaObserver(Post, _log, _timings, _settings.UseUiaSelectionEvents);
        _observer.ProbeCompleted += OnProbeCompleted;
        _observer.RefreshCompleted += OnRefreshCompleted;
        _observer.SelectionChangedByUia += OnUiaSelectionChanged;
        _observer.Start();

        _hooks = new InputHookThread(Post);
        _hooks.InputReceived += OnInput;
        _hooks.WheelOverPopup += (delta, ctrl) => _popup.HandleWheel(delta, ctrl);
        _hooks.HotkeyStatusChanged += OnHotkeyStatus;
        _hooks.Start();
        if (!_hooks.HooksInstalled) _log.Error("input", _hooks.HookError ?? "input hooks not installed");
        ApplyHotkey();

        _tracker = new SourceWindowTracker();
        _tracker.ForegroundChanged += OnForegroundChanged;
        _tracker.SourceLost += reason => { _log.Info("track", reason); Dispatch(new SessionEvent.SourceWindowLost()); };
        _tracker.RefreshRequested += RequestRefresh;

        _tray = new TrayIcon();
        _tray.SetAutoTranslate(_settings.AutoTranslate);
        _tray.SetCurrentAppProvider(CurrentAppName);
        _tray.IsAppExcluded = name => _settings.IsProcessExcluded(name);
        _tray.AutoTranslateToggled += enabled => { if (_settings.AutoTranslate != enabled) { _settings.AutoTranslate = enabled; ApplySettings(_settings, notifySession: true); } };
        _tray.ExcludeAppToggled += (name, excluded) => { _settings.SetProcessExcluded(name, excluded); ApplySettings(_settings, notifySession: excluded); _log.Info("settings", $"{name}: {(excluded ? "excluded" : "included")}"); };
        _tray.TranslateClipboardRequested += TranslateClipboard;
        _tray.SettingsRequested += OpenSettings;
        _tray.DiagnosticsRequested += OpenDiagnostics;
        _tray.WelcomeRequested += () => OpenWelcome(force: true);
        _tray.ExitRequested += Exit;

        _stabTimer.Tick += (_, _) => { _stabTimer.Stop(); var id = _stabTimerId; _stabTimerId = 0; Dispatch(new SessionEvent.StabilizationElapsed(id)); };
        _probeWatchdog.Tick += (_, _) => OnProbeWatchdog();
        _refreshWatchdog.Tick += (_, _) => OnRefreshWatchdog();
        _graceTimer.Tick += (_, _) => { _graceTimer.Stop(); Dispatch(new SessionEvent.GeometryGraceElapsed(_graceSelectionId)); };
        _saveTimer.Interval = TimeSpan.FromSeconds(1.5);
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); TrySave(); };

        try
        {
            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ActivateEventName);
            _activateWait = ThreadPool.RegisterWaitForSingleObject(_activateEvent, (_, _) => Post(() => { _log.Info("app", "second instance signalled; opening settings"); OpenSettings(); }), null, -1, false);
        }
        catch (Exception ex) { _log.Warn("app", $"activation event unavailable: {ex.GetType().Name}"); }

        if (!_hooks.HooksInstalled)
            _tray.ShowNotice("Bili Index Translate", "Input hooks could not be installed; automatic translation will not work. See Diagnostics.", warning: true);

        _log.Info("app", $"ready · auto={_settings.AutoTranslate} delay={_settings.StabilizationDelayMs}ms target={_settings.TargetLanguage} model={_settings.Model} relay={_settings.UseRelay}");
        if (_forceWelcome || !_settings.OnboardingCompleted) OpenWelcome(force: false);
    }

    // ---- session dispatch -------------------------------------------------------------------------

    private void Dispatch(SessionEvent e)
    {
        if (_disposed) return;
        _queue.Enqueue(e);
        if (_dispatching) return;
        _dispatching = true;
        try
        {
            while (_queue.Count > 0)
            {
                var next = _queue.Dequeue();
                IReadOnlyList<SessionEffect> effects;
                try { effects = _session.Handle(next); }
                catch (Exception ex) { _log.Error("session", $"{next.GetType().Name}: {ex.GetType().Name}: {ex.Message}"); continue; }
                foreach (var effect in effects)
                {
                    try { Execute(effect); }
                    catch (Exception ex) { _log.Error("effect", $"{effect.GetType().Name}: {ex.GetType().Name}: {ex.Message}"); }
                }
            }
        }
        finally { _dispatching = false; }
    }

    private void Execute(SessionEffect effect)
    {
        switch (effect)
        {
            case SessionEffect.StartStabilizationTimer t:
                _stabTimer.Stop();
                _stabTimerId = t.TimerId;
                _stabTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, t.DelayMs));
                _stabTimer.Start();
                break;

            case SessionEffect.CancelStabilizationTimer t:
                if (_stabTimerId == t.TimerId) { _stabTimer.Stop(); _stabTimerId = 0; }
                break;

            case SessionEffect.RequestProbe p:
                StartProbe(p.ProbeId, p.Mode);
                break;

            case SessionEffect.ShowLoading s:
                if (_triggerTimestamp != 0)
                {
                    _timings.Record(TimingCategory.Detect, Stopwatch.GetElapsedTime(_triggerTimestamp).TotalMilliseconds);
                    _triggerTimestamp = 0;
                }
                _popup.ShowLoading(s.Snapshot, Caption(s.Snapshot, null, false));
                break;

            case SessionEffect.StartTranslation st:
                StartTranslation(st.RequestId, st.Snapshot);
                break;

            case SessionEffect.CancelTranslation c:
                _coordinator.Cancel(c.RequestId);
                break;

            case SessionEffect.ShowTranslation show:
                _popup.ShowTranslation(show.Snapshot, show.Result, Caption(show.Snapshot, show.Result.Route, show.Result.FromCache));
                break;

            case SessionEffect.ShowError err:
                _popup.ShowError(err.Snapshot, err.Error, err.Error.Route == RequestRoute.Relay ? "relay" : null);
                break;

            case SessionEffect.HidePopup:
                _popup.Hide();
                break;

            case SessionEffect.BeginTracking b:
                _trackedSelectionId = b.SelectionId;
                _tracker.Begin(new IntPtr(b.Snapshot.SourceWindow), b.Snapshot.ProcessId);
                break;

            case SessionEffect.EndTracking:
                _trackedSelectionId = 0;
                _tracker.End();
                _refreshWatchdog.Stop();
                _observer.ClearTracking();
                break;

            case SessionEffect.UpdateAnchor u:
                _popup.UpdateAnchor(u.Anchor);
                break;

            case SessionEffect.StartGeometryGrace g:
                _graceSelectionId = g.SelectionId;
                _graceTimer.Interval = TimeSpan.FromMilliseconds(g.DelayMs);
                _graceTimer.Stop();
                _graceTimer.Start();
                break;

            case SessionEffect.CancelGeometryGrace:
                _graceTimer.Stop();
                break;

            case SessionEffect.DetachPopup:
                _popup.Detach();
                break;

            case SessionEffect.Report r:
                _log.Add(r.Level, r.Category, r.Message);
                break;
        }
    }

    // ---- probes -----------------------------------------------------------------------------------

    private ReaderPolicy Policy() => new(_settings.MaxChars, Environment.ProcessId, _welcomeWindow?.Hwnd ?? IntPtr.Zero);

    private void StartProbe(long probeId, ProbeMode mode)
    {
        _probeId = probeId;
        _probeMode = mode;

        if (_pendingClipboardText is { } clip)
        {
            _pendingClipboardText = null;
            _probeId = 0;
            Dispatch(new SessionEvent.ProbeCompleted(probeId, OutcomeForText(clip, SelectionOrigin.Clipboard, explicitRequest: true)));
            return;
        }

        if (!_observer.IsAlive)
        {
            _probeId = 0;
            Dispatch(new SessionEvent.ProbeCompleted(probeId, new ProbeOutcome.Failed("UI Automation thread is not running")));
            return;
        }

        _observer.RequestProbe(probeId, mode, Policy());
        _probeWatchdog.Stop();
        _probeWatchdog.Interval = TimeSpan.FromMilliseconds(ProbeWatchdogMs);
        _probeWatchdog.Start();
    }

    private void OnProbeWatchdog()
    {
        _probeWatchdog.Stop();
        if (_probeId == 0) return;
        var id = _probeId;
        _probeId = 0;
        _log.Warn("probe", "stalled: UI Automation did not answer in time (the source app may be busy)");
        Dispatch(new SessionEvent.ProbeCompleted(id, new ProbeOutcome.Failed("probe stalled")));
    }

    private void OnProbeCompleted(long probeId, ProbeOutcome outcome)
    {
        if (probeId != _probeId) { _log.Info("probe", $"late result #{probeId} ignored"); return; }
        var explicitRequest = _probeMode == ProbeMode.Explicit;

        if (outcome is ProbeOutcome.Selected sel)
        {
            _probeWatchdog.Stop();
            _probeId = 0;
            Dispatch(new SessionEvent.ProbeCompleted(probeId, FinishSelected(sel.Snapshot, explicitRequest)));
            return;
        }

        if (explicitRequest && outcome is ProbeOutcome.NoSelection or ProbeOutcome.Unsupported or ProbeOutcome.Failed)
        {
            // UIA could not read it: the explicit fallback copies once via Ctrl+C.
            _log.Info("probe", $"explicit request, UIA gave {outcome.GetType().Name}; trying clipboard fallback");
            _probeWatchdog.Stop();
            _probeWatchdog.Interval = TimeSpan.FromMilliseconds(ClipboardWatchdogMs);
            _probeWatchdog.Start();
            _ = RunClipboardFallbackAsync(probeId);
            return;
        }

        _probeWatchdog.Stop();
        _probeId = 0;
        RecordOutcome(outcome);
        Dispatch(new SessionEvent.ProbeCompleted(probeId, outcome));
    }

    private async Task RunClipboardFallbackAsync(long probeId)
    {
        string? text = null;
        try { text = await ClipboardFallback.CaptureSelectionAsync(); }
        catch (Exception ex) { _log.Warn("clipboard", $"fallback failed: {ex.GetType().Name}"); }
        if (probeId != _probeId) return; // watchdog or a newer probe took over
        _probeWatchdog.Stop();
        _probeId = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            _log.Info("clipboard", "nothing captured");
            Dispatch(new SessionEvent.ProbeCompleted(probeId, new ProbeOutcome.NoSelection()));
            return;
        }
        Dispatch(new SessionEvent.ProbeCompleted(probeId, OutcomeForText(text, SelectionOrigin.Clipboard, explicitRequest: true)));
    }

    private ProbeOutcome FinishSelected(SelectionSnapshot snapshot, bool explicitRequest)
    {
        _lastProcessName = snapshot.ProcessName;
        _anchorFromPointer = false;

        var reason = SelectionPolicy.ReasonToSkip(snapshot, _settings, explicitRequest);
        if (reason is not null)
        {
            _registry.RecordSuccess(snapshot.ProcessName, snapshot.HasGeometry);
            return new ProbeOutcome.Skipped(reason);
        }

        if (!snapshot.HasGeometry)
        {
            // Text but no on-screen bounds: anchor to the pointer only right after a mouse release, never guess otherwise.
            if (Environment.TickCount64 - _lastPointerTick <= 1500)
            {
                snapshot = snapshot with { Anchor = AnchorRect.AroundPoint(_lastPointer) };
                _anchorFromPointer = true;
                _log.Info("probe", $"{snapshot.ProcessName}: selection has no bounds; anchoring near the pointer");
            }
            else
            {
                _registry.RecordSuccess(snapshot.ProcessName, hadGeometry: false);
                return new ProbeOutcome.Skipped("selection reported without on-screen bounds");
            }
        }

        _registry.RecordSuccess(snapshot.ProcessName, !_anchorFromPointer);
        _log.Info("probe", $"{snapshot.ProcessName}: {snapshot.Text.Length} chars, {snapshot.Anchor!.LineCount} line(s), {snapshot.ProbeDurationMs:0.0} ms, fp={DiagnosticsLog.Fingerprint(snapshot.Text)}");
        return new ProbeOutcome.Selected(snapshot);
    }

    private ProbeOutcome OutcomeForText(string text, SelectionOrigin origin, bool explicitRequest)
    {
        var foreground = NativeMethods.GetForegroundWindow();
        var pid = NativeMethods.ProcessIdOf(foreground);
        NativeMethods.GetCursorPos(out var pt);
        _lastPointer = new PxPoint(pt.X, pt.Y);
        _anchorFromPointer = true;
        var snapshot = new SelectionSnapshot(text, AnchorRect.AroundPoint(_lastPointer), foreground.ToInt64(), pid, ProcessInfo.NameOf(pid), EditableKind.NotEditable, origin, 0);
        var reason = SelectionPolicy.ReasonToSkip(snapshot, _settings, explicitRequest);
        return reason is null ? new ProbeOutcome.Selected(snapshot) : new ProbeOutcome.Skipped(reason);
    }

    private void RecordOutcome(ProbeOutcome outcome)
    {
        var fg = NativeMethods.GetForegroundWindow();
        var name = ProcessInfo.NameOf(NativeMethods.ProcessIdOf(fg));
        if (NativeMethods.ProcessIdOf(fg) == Environment.ProcessId) return;
        switch (outcome)
        {
            case ProbeOutcome.Unsupported: _registry.RecordUnsupported(name); _lastProcessName = name; break;
            case ProbeOutcome.Failed: _registry.RecordFailure(name); _lastProcessName = name; break;
        }
    }

    // ---- geometry refresh ------------------------------------------------------------------------

    private void RequestRefresh()
    {
        if (_trackedSelectionId == 0) return;
        _observer.RequestRefresh(_trackedSelectionId, Policy());
        _refreshWatchdog.Stop();
        _refreshWatchdog.Interval = TimeSpan.FromMilliseconds(RefreshWatchdogMs);
        _refreshWatchdog.Start();
    }

    private void OnRefreshCompleted(long selectionId, GeometryUpdate update)
    {
        _refreshWatchdog.Stop();
        _tracker.RefreshCompleted();
        if (selectionId != _trackedSelectionId) return;
        Dispatch(new SessionEvent.GeometryRefreshed(selectionId, update));
    }

    private void OnRefreshWatchdog()
    {
        _refreshWatchdog.Stop();
        _tracker.RefreshCompleted();
        if (_trackedSelectionId == 0) return;
        Dispatch(new SessionEvent.GeometryRefreshed(_trackedSelectionId, new GeometryUpdate.Unavailable("refresh timed out")));
    }

    // ---- translation --------------------------------------------------------------------------------

    private TranslationRequest BuildRequest(string text, AppSettings s)
    {
        var lang = s.ResolvedTargetLanguage;
        return new TranslationRequest(text.Trim(), lang.Code, lang.PromptName, s.SourceLanguage, s.Model, s.BaseUrl, s.UseRelay, s.RelayPrefix, s.ApiKey);
    }

    private void StartTranslation(long requestId, SelectionSnapshot snapshot)
    {
        var request = BuildRequest(snapshot.Text, _settings);
        if (_coordinator.TryGetCached(request) is { } cached)
        {
            Dispatch(new SessionEvent.TranslationCompleted(requestId, cached));
            return;
        }
        _coordinator.Start(requestId, request, (id, outcome) => Dispatch(new SessionEvent.TranslationCompleted(id, outcome)));
    }

    private string? Caption(SelectionSnapshot snapshot, RequestRoute? route, bool fromCache)
    {
        var parts = new List<string>(3);
        if (fromCache) parts.Add("cached");
        if (route == RequestRoute.Relay || (route is null && _settings.UseRelay)) parts.Add("relay");
        if (snapshot.Origin == SelectionOrigin.Clipboard) parts.Add("clipboard");
        else if (_anchorFromPointer) parts.Add("near pointer");
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private async Task<(bool Ok, string Message)> TestConnectionAsync(AppSettings s, CancellationToken ct)
    {
        var request = BuildRequest("Hello, world.", s);
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _translator.TranslateAsync(request, ct);
            return (true, $"OK · {result.ElapsedMs:0} ms via {result.Route.ToString().ToLowerInvariant()} · “{Truncate(result.Translation, 40)}”");
        }
        catch (TranslationException ex)
        {
            var msg = $"{ex.Error.Message}";
            if (ex.Error.Hint is { } hint) msg += " " + hint;
            _log.Warn("test", $"{ex.Error} ({sw.ElapsedMilliseconds} ms)");
            return (false, msg);
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    // ---- input & tracking events ------------------------------------------------------------------

    private void OnInput(InputEvent e)
    {
        switch (e.Signal)
        {
            case InputSignal.MouseDown:
                if (_popup.IsVisible && !_popup.HitTest(e.Point)) Dispatch(new SessionEvent.OutsideClick());
                break;

            case InputSignal.MouseUp:
                if (_popup.HitTest(e.Point)) return;
                _lastPointer = e.Point;
                _lastPointerTick = Environment.TickCount64;
                _triggerTimestamp = Stopwatch.GetTimestamp();
                Dispatch(new SessionEvent.TriggerReceived(TriggerKind.MouseUp));
                break;

            case InputSignal.KeyUpSelection:
                _triggerTimestamp = Stopwatch.GetTimestamp();
                Dispatch(new SessionEvent.TriggerReceived(TriggerKind.KeyUp));
                break;

            case InputSignal.Escape:
                Dispatch(new SessionEvent.EscapePressed());
                break;

            case InputSignal.Hotkey:
                if (!_settings.HotkeyEnabled) return;
                _lastPointer = e.Point;
                _lastPointerTick = Environment.TickCount64;
                _triggerTimestamp = Stopwatch.GetTimestamp();
                Dispatch(new SessionEvent.TriggerReceived(TriggerKind.Hotkey));
                break;
        }
    }

    private void OnUiaSelectionChanged()
    {
        if (!_settings.UseUiaSelectionEvents || _hooks.IsLeftButtonDown) return; // mouse-up will probe
        _triggerTimestamp = Stopwatch.GetTimestamp();
        Dispatch(new SessionEvent.TriggerReceived(TriggerKind.UiaEvent));
    }

    private void OnForegroundChanged(IntPtr hwnd)
    {
        if (!_popup.IsVisible && !_session.IsProbePending) return;
        var pid = NativeMethods.ProcessIdOf(hwnd);
        var isOurs = pid == Environment.ProcessId;
        var root = hwnd == IntPtr.Zero ? IntPtr.Zero : NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
        var isSource = _tracker.IsTracking && (root == _tracker.Root || hwnd == _tracker.Root);
        Dispatch(new SessionEvent.ForegroundChanged(isOurs || isSource));
    }

    private void TranslateClipboard()
    {
        var text = ClipboardFallback.ReadText();
        if (string.IsNullOrWhiteSpace(text))
        {
            _tray.ShowNotice("Bili Index Translate", "The clipboard has no text.");
            return;
        }
        _pendingClipboardText = text;
        _triggerTimestamp = Stopwatch.GetTimestamp();
        Dispatch(new SessionEvent.TriggerReceived(TriggerKind.Hotkey));
    }

    private string? CurrentAppName()
    {
        var fg = NativeMethods.GetForegroundWindow();
        var pid = NativeMethods.ProcessIdOf(fg);
        if (pid != 0 && pid != Environment.ProcessId) return ProcessInfo.NameOf(pid);
        return _lastProcessName;
    }

    // ---- settings --------------------------------------------------------------------------------------

    private void ApplySettings(AppSettings next, bool notifySession)
    {
        var previous = _settings;
        _settings = next.Normalize();
        TrySave();

        _session.Options = new SessionOptions(_settings.AutoTranslate, _settings.StabilizationDelayMs);
        _theme.Apply(_settings.Theme);
        _popup.Configure(_settings.TextScale, _settings.ShowOriginalByDefault);
        _tray.SetAutoTranslate(_settings.AutoTranslate);
        if (previous.Hotkey != _settings.Hotkey || previous.HotkeyEnabled != _settings.HotkeyEnabled) ApplyHotkey();
        if (previous.UseUiaSelectionEvents != _settings.UseUiaSelectionEvents) _observer.SetSelectionEvents(_settings.UseUiaSelectionEvents);
        if (previous.DiagnosticsFileLogging != _settings.DiagnosticsFileLogging) ConfigureFileLogging(_settings.DiagnosticsFileLogging);

        var serviceChanged = previous.BaseUrl != _settings.BaseUrl || previous.UseRelay != _settings.UseRelay || previous.RelayPrefix != _settings.RelayPrefix
                             || previous.Model != _settings.Model || previous.TargetLanguage != _settings.TargetLanguage || previous.ApiKey != _settings.ApiKey;
        if (serviceChanged) _log.Info("settings", $"service: target={_settings.TargetLanguage} model={_settings.Model} relay={_settings.UseRelay} host={HostOf(_settings)}");
        if (notifySession) Dispatch(new SessionEvent.SettingsChanged());
    }

    private static string HostOf(AppSettings s)
        => EndpointResolver.TryResolve(s.BaseUrl, s.UseRelay, s.RelayPrefix, out var ep, out _) ? ep!.DisplayHost : "(invalid)";

    private void ApplyHotkey()
    {
        if (_settings.HotkeyEnabled && HotkeyParser.TryParse(_settings.Hotkey, out var spec)) _hooks.SetHotkey(spec);
        else _hooks.SetHotkey(null);
    }

    private void OnHotkeyStatus(string display, string? error)
    {
        _hotkeyError = error;
        if (error is null) _log.Info("hotkey", $"{display} registered");
        else
        {
            _log.Warn("hotkey", error + " Automatic translation is unaffected.");
            _tray.ShowNotice("Shortcut unavailable", error + " Automatic translation still works; change the shortcut in Settings.", warning: true);
        }
        _settingsWindow?.SetHotkeyStatus(error);
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void TrySave()
    {
        try { _store.Save(_settings); }
        catch (Exception ex) { _log.Error("settings", $"save failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    private void ConfigureFileLogging(bool enabled)
    {
        if (!enabled)
        {
            _log.SetSink(null);
            _fileLog?.Dispose();
            _fileLog = null;
            return;
        }
        try
        {
            var path = Path.Combine(_dataDir, "diagnostics.log");
            if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024) File.Move(path, path + ".1", overwrite: true);
            _fileLog = new StreamWriter(path, append: true, Encoding.UTF8) { AutoFlush = true };
            var writer = _fileLog;
            _log.SetSink(entry => { lock (writer) writer.WriteLine(entry.ToString()); });
            _log.Info("diag", "file logging enabled");
        }
        catch (Exception ex) { _log.Warn("diag", $"file logging unavailable: {ex.GetType().Name}"); }
    }

    // ---- windows -------------------------------------------------------------------------------------

    private void OpenSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(_settings, _store.FilePath, TestConnectionAsync, s => ApplySettings(s, notifySession: true));
        _settingsWindow.SetHotkeyStatus(_hotkeyError);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void OpenDiagnostics()
    {
        if (_diagnosticsWindow is { IsLoaded: true })
        {
            _diagnosticsWindow.Activate();
            return;
        }
        _diagnosticsWindow = new DiagnosticsWindow(_log, _timings, _registry, DiagnosticsHeader);
        _diagnosticsWindow.Closed += (_, _) => _diagnosticsWindow = null;
        _diagnosticsWindow.Show();
    }

    private void OpenWelcome(bool force)
    {
        if (_welcomeWindow is { IsLoaded: true })
        {
            _welcomeWindow.Activate();
            return;
        }
        _welcomeWindow = new WelcomeWindow();
        if (!_hooks.HooksInstalled) _welcomeWindow.SetWarning("Input hooks are unavailable on this system, so selections cannot be detected automatically. The fallback shortcut may still work.");
        _welcomeWindow.SettingsRequested += OpenSettings;
        _welcomeWindow.Finished += dontShow =>
        {
            if (dontShow && !_settings.OnboardingCompleted) { _settings.OnboardingCompleted = true; TrySave(); }
        };
        _welcomeWindow.Closed += (_, _) => _welcomeWindow = null;
        _welcomeWindow.Show();
        if (force) _welcomeWindow.Activate();
    }

    private string DiagnosticsHeader()
    {
        var sb = new StringBuilder();
        sb.Append($"v{typeof(AppHost).Assembly.GetName().Version} · {Environment.OSVersion.Version} · .NET {Environment.Version} · ");
        sb.Append($"session {_session.State} · hooks {(_hooks.HooksInstalled ? "ok" : "MISSING")} · uia {(_observer.IsAlive ? "ok" : "stopped")} · ");
        sb.Append($"cache {_cache.Count} entries, {_cache.Hits} hits / {_cache.Misses} misses · in-flight {_coordinator.ActiveCount} · ");
        sb.Append($"target {_settings.TargetLanguage} · {_settings.Model} · {HostOf(_settings)} · relay {(_settings.UseRelay ? "on" : "off")} · auto {(_settings.AutoTranslate ? "on" : "off")} · delay {_settings.StabilizationDelayMs} ms");
        try
        {
            var monitors = new List<string>();
            NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref RECT _, IntPtr _) =>
            {
                var m = Win32MonitorLocator.FromHandle(h);
                monitors.Add($"{m.Name.TrimStart('\\', '.')} {m.Bounds.Width}x{m.Bounds.Height}@{m.Scale * 100:0}%");
                return true;
            }, IntPtr.Zero);
            if (monitors.Count > 0) sb.Append(" · monitors: ").Append(string.Join(", ", monitors));
        }
        catch (Exception) { }
        return sb.ToString();
    }

    public void ReportCrash(Exception ex) => _log.Error("app", $"unhandled: {ex.GetType().Name}: {ex.Message}");

    // ---- lifetime -------------------------------------------------------------------------------------

    private void Exit()
    {
        _log.Info("app", "exit requested");
        Dispose();
        _app.Shutdown();
    }

    public void Dispose()
    {
        if (_disposed) return;
        try { Dispatch(new SessionEvent.Shutdown()); } catch (Exception) { }
        _disposed = true;

        _stabTimer.Stop();
        _probeWatchdog.Stop();
        _refreshWatchdog.Stop();
        _graceTimer.Stop();
        _saveTimer.Stop();
        TrySave();

        _activateWait?.Unregister(null);
        _activateEvent?.Dispose();
        SafeDispose(_hooks);
        SafeDispose(_tracker);
        SafeDispose(_observer);
        SafeDispose(_coordinator);
        SafeDispose(_popup);
        try { _settingsWindow?.Close(); _diagnosticsWindow?.Close(); _welcomeWindow?.Close(); } catch (Exception) { }
        SafeDispose(_tray);
        SafeDispose(_transport);
        SafeDispose(_theme);
        _log.Info("app", "stopped");
        _fileLog?.Dispose();
        _fileLog = null;
    }

    private static void SafeDispose(IDisposable? d)
    {
        try { d?.Dispose(); } catch (Exception) { }
    }
}
