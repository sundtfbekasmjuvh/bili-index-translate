using BiliIndexTranslate.Core.Geometry;

namespace BiliIndexTranslate.Core.Selection;

public enum SelectionOrigin
{
    /// <summary>Read through UI Automation TextPattern.GetSelection().</summary>
    UiAutomation,
    /// <summary>Captured with an explicit user action (fallback shortcut / tray command); anchored to the pointer.</summary>
    Clipboard,
    /// <summary>Produced by the onboarding demo.</summary>
    Demo,
}

public enum EditableKind { NotEditable, SingleLineInput, MultiLineEditor }

/// <summary>
/// Everything the session needs to know about one observed selection. Immutable; a new snapshot is
/// produced for every probe. <see cref="SourceKey"/> identifies "the same place" for dismissal memory.
/// </summary>
public sealed record SelectionSnapshot(
    string Text,
    AnchorRect? Anchor,
    long SourceWindow,
    int ProcessId,
    string ProcessName,
    EditableKind Editable,
    SelectionOrigin Origin,
    double ProbeDurationMs)
{
    public bool HasGeometry => Anchor is not null;

    public string SourceKey => $"{ProcessId}:{SourceWindow}";

    public bool SameSelectionAs(SelectionSnapshot? other)
        => other is not null && string.Equals(Text, other.Text, StringComparison.Ordinal) && SourceKey == other.SourceKey;
}

/// <summary>Outcome of one selection probe.</summary>
public abstract record ProbeOutcome
{
    public sealed record Selected(SelectionSnapshot Snapshot) : ProbeOutcome;

    /// <summary>A text control is focused but nothing (or only a caret) is selected.</summary>
    public sealed record NoSelection : ProbeOutcome;

    /// <summary>The focused control does not expose selectable text through UI Automation.</summary>
    public sealed record Unsupported(string Reason) : ProbeOutcome;

    /// <summary>Selection exists but policy says skip (password, excluded app, editable field, too short/long, own window).</summary>
    public sealed record Skipped(string Reason) : ProbeOutcome;

    /// <summary>The provider failed, stalled, or threw.</summary>
    public sealed record Failed(string Reason) : ProbeOutcome;
}

/// <summary>Why a popup was hidden; surfaced in diagnostics.</summary>
public enum DismissReason
{
    SelectionCleared,
    SelectionReplaced,
    OutsideClick,
    Escape,
    ForegroundChanged,
    SourceWindowLost,
    GeometryUnavailable,
    SettingsChanged,
    UserClosed,
    Shutdown,
}
