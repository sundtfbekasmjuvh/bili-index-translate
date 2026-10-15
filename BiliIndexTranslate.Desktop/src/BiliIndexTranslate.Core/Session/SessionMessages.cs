using BiliIndexTranslate.Core.Diagnostics;
using BiliIndexTranslate.Core.Selection;
using BiliIndexTranslate.Core.Translation;

namespace BiliIndexTranslate.Core.Session;

public enum SessionState
{
    /// <summary>Nothing selected, nothing shown.</summary>
    Idle,
    /// <summary>A trigger arrived; waiting for the selection to settle before probing.</summary>
    SelectionPending,
    /// <summary>Popup visible with a loading indicator; a translation request is in flight.</summary>
    Loading,
    ShowingTranslation,
    ShowingError,
    /// <summary>Popup hidden by the user or by loss of context; the same selection will not reopen it.</summary>
    Dismissed,
}

public enum TriggerKind { MouseUp, KeyUp, UiaEvent, Hotkey }

public enum ProbeMode { Auto, Explicit }

/// <summary>What the geometry tracker learned while the popup was visible.</summary>
public abstract record GeometryUpdate
{
    public sealed record Rects(AnchorRect Anchor) : GeometryUpdate;
    /// <summary>Selection still exists and reads the same, but no part of it is visible (scrolled away).</summary>
    public sealed record NoVisibleRects : GeometryUpdate;
    /// <summary>The selected text now differs from the one being shown.</summary>
    public sealed record SelectionChanged : GeometryUpdate;
    /// <summary>The selection collapsed to a caret or vanished.</summary>
    public sealed record Cleared : GeometryUpdate;
    /// <summary>The element or window could not be queried (even after one re-probe).</summary>
    public sealed record Unavailable(string Reason) : GeometryUpdate;
}

public abstract record SessionEvent
{
    public sealed record TriggerReceived(TriggerKind Kind) : SessionEvent;
    public sealed record StabilizationElapsed(long TimerId) : SessionEvent;
    public sealed record ProbeCompleted(long ProbeId, ProbeOutcome Outcome) : SessionEvent;
    public sealed record TranslationCompleted(long RequestId, TranslationOutcome Outcome) : SessionEvent;
    public sealed record GeometryRefreshed(long SelectionId, GeometryUpdate Update) : SessionEvent;
    public sealed record GeometryGraceElapsed(long SelectionId) : SessionEvent;
    public sealed record OutsideClick : SessionEvent;
    public sealed record EscapePressed : SessionEvent;
    public sealed record ForegroundChanged(bool IsSourceOrPopup) : SessionEvent;
    public sealed record SourceWindowLost : SessionEvent;
    public sealed record UserRetry : SessionEvent;
    public sealed record UserClosed : SessionEvent;
    public sealed record UserPinned : SessionEvent;
    public sealed record SettingsChanged : SessionEvent;
    public sealed record Shutdown : SessionEvent;
}

public abstract record SessionEffect
{
    public sealed record StartStabilizationTimer(long TimerId, int DelayMs) : SessionEffect;
    public sealed record CancelStabilizationTimer(long TimerId) : SessionEffect;
    public sealed record RequestProbe(long ProbeId, ProbeMode Mode) : SessionEffect;
    public sealed record ShowLoading(long SelectionId, SelectionSnapshot Snapshot) : SessionEffect;
    public sealed record StartTranslation(long RequestId, long SelectionId, SelectionSnapshot Snapshot) : SessionEffect;
    public sealed record CancelTranslation(long RequestId) : SessionEffect;
    public sealed record ShowTranslation(long SelectionId, SelectionSnapshot Snapshot, TranslationOutcome.Success Result) : SessionEffect;
    public sealed record ShowError(long SelectionId, SelectionSnapshot Snapshot, TranslationError Error) : SessionEffect;
    public sealed record HidePopup(DismissReason Reason) : SessionEffect;
    public sealed record BeginTracking(long SelectionId, SelectionSnapshot Snapshot) : SessionEffect;
    public sealed record EndTracking : SessionEffect;
    public sealed record UpdateAnchor(long SelectionId, AnchorRect Anchor) : SessionEffect;
    public sealed record StartGeometryGrace(long SelectionId, int DelayMs) : SessionEffect;
    public sealed record CancelGeometryGrace : SessionEffect;
    public sealed record DetachPopup(long SelectionId) : SessionEffect;
    public sealed record Report(DiagnosticLevel Level, string Category, string Message) : SessionEffect;
}

/// <summary>Tunables the session reads; replaced wholesale when settings change.</summary>
public sealed record SessionOptions(bool AutoTranslate = true, int BaseDelayMs = 250, int GeometryGraceMs = 400)
{
    public int DelayFor(TriggerKind kind) => kind switch
    {
        TriggerKind.MouseUp => BaseDelayMs,
        TriggerKind.KeyUp => BaseDelayMs + 30,
        TriggerKind.UiaEvent => BaseDelayMs + 70,
        _ => 0,
    };
}
