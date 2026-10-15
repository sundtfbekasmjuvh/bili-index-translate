using BiliIndexTranslate.Core.Settings;

namespace BiliIndexTranslate.Core.Selection;

/// <summary>Decides whether an observed selection should be translated automatically.</summary>
public static class SelectionPolicy
{
    /// <summary>Returns null when the selection may be translated, otherwise the reason to skip it.</summary>
    public static string? ReasonToSkip(SelectionSnapshot s, AppSettings settings, bool explicitRequest)
    {
        var text = s.Text;
        var trimmed = text.AsSpan().Trim();
        if (trimmed.IsEmpty) return "empty selection";
        if (trimmed.Length > settings.MaxChars) return $"selection longer than {settings.MaxChars} characters";

        if (explicitRequest) return null; // the user asked for it: ignore automatic-mode policies

        if (trimmed.Length < settings.MinChars) return "selection shorter than minimum";
        if (!HasTranslatableContent(trimmed)) return "no letters or ideographs";

        if (settings.IsProcessExcluded(s.ProcessName)) return $"translation paused for {s.ProcessName}";

        switch (settings.EditableFieldPolicy)
        {
            case EditableFieldPolicy.SkipSingleLineInputs when s.Editable == EditableKind.SingleLineInput:
                return "single-line input field";
            case EditableFieldPolicy.SkipAllEditable when s.Editable != EditableKind.NotEditable:
                return "editable field";
        }
        return null;
    }

    /// <summary>True when the text contains at least one letter or ideograph (not just digits/punctuation).</summary>
    public static bool HasTranslatableContent(ReadOnlySpan<char> text)
    {
        foreach (var ch in text)
        {
            if (char.IsLetter(ch)) return true;
            if (char.IsSurrogate(ch)) return true; // astral-plane CJK, emoji-adjacent scripts: let the service decide
        }
        return false;
    }

    /// <summary>
    /// Heuristic used by the UIA reader: an editable control whose own height is below ~2.5 selected
    /// lines is treated as a single-line input (address bars, search boxes, chat lines).
    /// </summary>
    public static EditableKind ClassifyEditable(bool isEditable, int controlHeightPx, int lineHeightPx)
    {
        if (!isEditable) return EditableKind.NotEditable;
        if (lineHeightPx <= 0) return EditableKind.MultiLineEditor;
        return controlHeightPx < lineHeightPx * 2.5 ? EditableKind.SingleLineInput : EditableKind.MultiLineEditor;
    }
}
