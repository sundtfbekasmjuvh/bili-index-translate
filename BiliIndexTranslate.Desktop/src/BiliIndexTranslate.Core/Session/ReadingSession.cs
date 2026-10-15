using BiliIndexTranslate.Core.Diagnostics;
using BiliIndexTranslate.Core.Selection;
using BiliIndexTranslate.Core.Translation;

namespace BiliIndexTranslate.Core.Session;

/// <summary>
/// The popup lifecycle as a pure state machine: events in, effects out, no threads, no timers,
/// no UI. Every asynchronous thing (timers, probes, translations, geometry refreshes) carries an id
/// issued here, so anything that finishes late is recognised and ignored instead of reopening a
/// dismissed popup or overwriting a newer translation.
/// </summary>
public sealed class ReadingSession
{
    private readonly List<SessionEffect> _effects = new();
    private long _nextId = 1;

    private long _timerId;
    private long _probeId;
    private ProbeMode _probeMode;
    private long _selectionId;
    private long _requestId;
    private bool _graceActive;
    private bool _shutdown;
    private SelectionSnapshot? _current;
    private (string Text, string SourceKey)? _suppressed;

    public ReadingSession(SessionOptions? options = null)
    {
        Options = options ?? new SessionOptions();
    }

    public SessionState State { get; private set; } = SessionState.Idle;
    public SessionOptions Options { get; set; }
    public SelectionSnapshot? CurrentSelection => _current;
    public long CurrentSelectionId => _selectionId;
    public bool IsProbePending => _timerId != 0 || _probeId != 0;
    public bool IsPopupVisible => State is SessionState.Loading or SessionState.ShowingTranslation or SessionState.ShowingError;

    /// <summary>Processes one event and returns the effects to execute, in order.</summary>
    public IReadOnlyList<SessionEffect> Handle(SessionEvent e)
    {
        _effects.Clear();
        if (_shutdown) return Array.Empty<SessionEffect>();

        switch (e)
        {
            case SessionEvent.TriggerReceived t: OnTrigger(t.Kind); break;
            case SessionEvent.StabilizationElapsed s: OnStabilizationElapsed(s.TimerId); break;
            case SessionEvent.ProbeCompleted p: OnProbeCompleted(p.ProbeId, p.Outcome); break;
            case SessionEvent.TranslationCompleted r: OnTranslationCompleted(r.RequestId, r.Outcome); break;
            case SessionEvent.GeometryRefreshed g: OnGeometryRefreshed(g.SelectionId, g.Update); break;
            case SessionEvent.GeometryGraceElapsed g: OnGeometryGraceElapsed(g.SelectionId); break;
            case SessionEvent.OutsideClick: DismissByUser(DismissReason.OutsideClick); break;
            case SessionEvent.EscapePressed: DismissByUser(DismissReason.Escape); break;
            case SessionEvent.UserClosed: DismissByUser(DismissReason.UserClosed); break;
            case SessionEvent.ForegroundChanged f: if (!f.IsSourceOrPopup) LoseContext(DismissReason.ForegroundChanged); break;
            case SessionEvent.SourceWindowLost: LoseContext(DismissReason.SourceWindowLost); break;
            case SessionEvent.UserRetry: OnRetry(); break;
            case SessionEvent.UserPinned: OnPinned(); break;
            case SessionEvent.SettingsChanged: OnSettingsChanged(); break;
            case SessionEvent.Shutdown: OnShutdown(); break;
        }
        return _effects.ToArray();
    }

    // ---- triggers & probes -------------------------------------------------------------------

    private void OnTrigger(TriggerKind kind)
    {
        if (kind == TriggerKind.Hotkey)
        {
            CancelTimer();
            _suppressed = null; // the user asked explicitly
            IssueProbe(ProbeMode.Explicit);
            return;
        }
        if (!Options.AutoTranslate) return;

        CancelTimer();
        _timerId = NextId();
        Emit(new SessionEffect.StartStabilizationTimer(_timerId, Options.DelayFor(kind)));
        if (State == SessionState.Idle) State = SessionState.SelectionPending;
    }

    private void OnStabilizationElapsed(long timerId)
    {
        if (timerId != _timerId) return; // superseded timer
        _timerId = 0;
        IssueProbe(ProbeMode.Auto);
    }

    private void IssueProbe(ProbeMode mode)
    {
        _probeId = NextId();
        _probeMode = mode;
        Emit(new SessionEffect.RequestProbe(_probeId, mode));
    }

    private void OnProbeCompleted(long probeId, ProbeOutcome outcome)
    {
        if (probeId != _probeId)
        {
            Report(DiagnosticLevel.Info, "session", $"ignored stale probe #{probeId}");
            return;
        }
        _probeId = 0;
        var mode = _probeMode;

        switch (outcome)
        {
            case ProbeOutcome.Selected s:
                OnSelected(s.Snapshot, mode);
                break;

            case ProbeOutcome.NoSelection:
                _suppressed = null;
                if (IsPopupVisible) Hide(DismissReason.SelectionCleared, suppress: false);
                else if (State is SessionState.SelectionPending or SessionState.Dismissed) State = SessionState.Idle;
                break;

            case ProbeOutcome.Skipped sk:
                Report(DiagnosticLevel.Info, "probe", $"skipped: {sk.Reason}");
                if (IsPopupVisible) Hide(DismissReason.SelectionReplaced, suppress: false);
                else if (State == SessionState.SelectionPending) State = SessionState.Idle;
                break;

            case ProbeOutcome.Unsupported u:
                Report(DiagnosticLevel.Info, "probe", $"unsupported: {u.Reason}");
                if (State == SessionState.SelectionPending) State = SessionState.Idle;
                break;

            case ProbeOutcome.Failed f:
                Report(DiagnosticLevel.Warning, "probe", $"failed: {f.Reason}");
                if (State == SessionState.SelectionPending) State = SessionState.Idle;
                break;
        }
    }

    private void OnSelected(SelectionSnapshot snapshot, ProbeMode mode)
    {
        if (mode == ProbeMode.Auto && _suppressed is { } sup
            && string.Equals(sup.Text, snapshot.Text, StringComparison.Ordinal) && sup.SourceKey == snapshot.SourceKey)
        {
            Report(DiagnosticLevel.Info, "session", "same selection after dismissal; staying quiet");
            State = SessionState.Dismissed;
            return;
        }
        _suppressed = null;

        if (IsPopupVisible && snapshot.SameSelectionAs(_current))
        {
            _current = snapshot;
            if (snapshot.Anchor is { } a) Emit(new SessionEffect.UpdateAnchor(_selectionId, a));
            return;
        }

        if (_requestId != 0)
        {
            Emit(new SessionEffect.CancelTranslation(_requestId));
            _requestId = 0;
        }
        CancelGrace();

        _selectionId = NextId();
        _current = snapshot;
        Emit(new SessionEffect.BeginTracking(_selectionId, snapshot));
        Emit(new SessionEffect.ShowLoading(_selectionId, snapshot));
        _requestId = NextId();
        Emit(new SessionEffect.StartTranslation(_requestId, _selectionId, snapshot));
        State = SessionState.Loading;
    }

    // ---- translation ------------------------------------------------------------------------

    private void OnTranslationCompleted(long requestId, TranslationOutcome outcome)
    {
        if (requestId != _requestId || _current is null)
        {
            Report(DiagnosticLevel.Info, "session", $"ignored stale translation #{requestId}");
            return;
        }
        _requestId = 0;
        switch (outcome)
        {
            case TranslationOutcome.Success ok:
                Emit(new SessionEffect.ShowTranslation(_selectionId, _current, ok));
                State = SessionState.ShowingTranslation;
                break;
            case TranslationOutcome.Failure { Error.Kind: TranslationErrorKind.Cancelled }:
                break; // our own cancellation; nothing to show
            case TranslationOutcome.Failure fail:
                Emit(new SessionEffect.ShowError(_selectionId, _current, fail.Error));
                State = SessionState.ShowingError;
                break;
        }
    }

    private void OnRetry()
    {
        if (State != SessionState.ShowingError || _current is null) return;
        _requestId = NextId();
        Emit(new SessionEffect.ShowLoading(_selectionId, _current));
        Emit(new SessionEffect.StartTranslation(_requestId, _selectionId, _current));
        State = SessionState.Loading;
    }

    // ---- geometry -------------------------------------------------------------------------

    private void OnGeometryRefreshed(long selectionId, GeometryUpdate update)
    {
        if (!IsPopupVisible || selectionId != _selectionId) return;
        switch (update)
        {
            case GeometryUpdate.Rects r:
                CancelGrace();
                Emit(new SessionEffect.UpdateAnchor(_selectionId, r.Anchor));
                break;
            case GeometryUpdate.NoVisibleRects:
            case GeometryUpdate.Unavailable:
                if (!_graceActive)
                {
                    _graceActive = true;
                    Emit(new SessionEffect.StartGeometryGrace(_selectionId, Options.GeometryGraceMs));
                }
                break;
            case GeometryUpdate.SelectionChanged:
                if (_timerId == 0 && Options.AutoTranslate)
                {
                    _timerId = NextId();
                    Emit(new SessionEffect.StartStabilizationTimer(_timerId, Options.DelayFor(TriggerKind.UiaEvent)));
                }
                break;
            case GeometryUpdate.Cleared:
                _suppressed = null;
                Hide(DismissReason.SelectionCleared, suppress: false);
                break;
        }
    }

    private void OnGeometryGraceElapsed(long selectionId)
    {
        if (!_graceActive || selectionId != _selectionId) return;
        _graceActive = false;
        if (IsPopupVisible) Hide(DismissReason.GeometryUnavailable, suppress: true);
    }

    // ---- dismissal ------------------------------------------------------------------------

    private void DismissByUser(DismissReason reason)
    {
        if (IsPopupVisible) Hide(reason, suppress: true);
    }

    private void LoseContext(DismissReason reason)
    {
        if (IsPopupVisible) Hide(reason, suppress: true);
        else if (State == SessionState.SelectionPending)
        {
            CancelTimer();
            State = SessionState.Idle;
        }
    }

    private void OnPinned()
    {
        if (State != SessionState.ShowingTranslation || _current is null) return;
        CancelGrace();
        Emit(new SessionEffect.DetachPopup(_selectionId));
        Emit(new SessionEffect.EndTracking());
        _suppressed = (_current.Text, _current.SourceKey);
        _selectionId = 0;
        State = SessionState.Dismissed;
        Report(DiagnosticLevel.Info, "session", "popup pinned");
    }

    private void OnSettingsChanged()
    {
        if (IsPopupVisible) Hide(DismissReason.SettingsChanged, suppress: false);
        _suppressed = null;
    }

    private void OnShutdown()
    {
        CancelTimer();
        _probeId = 0;
        if (_requestId != 0) { Emit(new SessionEffect.CancelTranslation(_requestId)); _requestId = 0; }
        CancelGrace();
        if (IsPopupVisible) Emit(new SessionEffect.HidePopup(DismissReason.Shutdown));
        Emit(new SessionEffect.EndTracking());
        _current = null;
        _selectionId = 0;
        State = SessionState.Idle;
        _shutdown = true;
    }

    private void Hide(DismissReason reason, bool suppress)
    {
        if (_requestId != 0)
        {
            Emit(new SessionEffect.CancelTranslation(_requestId));
            _requestId = 0;
        }
        CancelGrace();
        Emit(new SessionEffect.HidePopup(reason));
        Emit(new SessionEffect.EndTracking());
        Report(DiagnosticLevel.Info, "session", $"hidden: {reason}");
        if (suppress && _current is not null)
        {
            _suppressed = (_current.Text, _current.SourceKey);
            State = SessionState.Dismissed;
        }
        else
        {
            State = SessionState.Idle;
        }
        _selectionId = 0;
    }

    // ---- helpers ----------------------------------------------------------------------------

    private void CancelTimer()
    {
        if (_timerId == 0) return;
        Emit(new SessionEffect.CancelStabilizationTimer(_timerId));
        _timerId = 0;
    }

    private void CancelGrace()
    {
        if (!_graceActive) return;
        _graceActive = false;
        Emit(new SessionEffect.CancelGeometryGrace());
    }

    private long NextId() => _nextId++;

    private void Emit(SessionEffect effect) => _effects.Add(effect);

    private void Report(DiagnosticLevel level, string category, string message) => Emit(new SessionEffect.Report(level, category, message));

    public override string ToString() => $"{State} sel#{_selectionId} req#{_requestId} probe#{_probeId} timer#{_timerId}{(_suppressed is null ? string.Empty : " suppressed")}";
}
