using System.Diagnostics;
using System.Runtime.InteropServices;
using BiliIndexTranslate.App.Native;
using BiliIndexTranslate.Core.Geometry;
using BiliIndexTranslate.Core.Selection;
using BiliIndexTranslate.Core.Session;
using Interop.UIAutomationClient;

namespace BiliIndexTranslate.App.Uia;

/// <summary>Policy inputs the reader needs; copied from settings on the UI thread, read on the observer thread.</summary>
internal sealed record ReaderPolicy(int MaxChars, int OwnProcessId, IntPtr DemoWindow);

/// <summary>What the reader keeps between a probe and later geometry refreshes. Observer-thread only.</summary>
internal sealed class TrackedSelection
{
    public required IUIAutomationElement Element { get; init; }
    public required IUIAutomationTextPattern Pattern { get; init; }
    public required string Text { get; init; }
    public required PxRect ElementRect { get; set; }
    public required IntPtr RootWindow { get; init; }
}

/// <summary>
/// Reads the current text selection through UI Automation. Runs on the observer thread only.
/// Never walks the whole tree: focused element plus at most six ancestors.
/// </summary>
internal sealed class UiaSelectionReader
{
    private const int MaxAncestorHops = 6;

    private readonly IUIAutomation _automation;
    private readonly IUIAutomationCacheRequest _cache;
    private readonly IUIAutomationTreeWalker _walker;

    public UiaSelectionReader(IUIAutomation automation)
    {
        _automation = automation;
        _cache = automation.CreateCacheRequest();
        _cache.TreeScope = TreeScope.TreeScope_Element;
        _cache.AutomationElementMode = AutomationElementMode.AutomationElementMode_Full;
        foreach (var id in new[]
                 {
                     UIA_PropertyIds.UIA_ProcessIdPropertyId,
                     UIA_PropertyIds.UIA_ControlTypePropertyId,
                     UIA_PropertyIds.UIA_IsPasswordPropertyId,
                     UIA_PropertyIds.UIA_NativeWindowHandlePropertyId,
                     UIA_PropertyIds.UIA_BoundingRectanglePropertyId,
                     UIA_PropertyIds.UIA_ClassNamePropertyId,
                     UIA_PropertyIds.UIA_IsTextPatternAvailablePropertyId,
                     UIA_PropertyIds.UIA_IsTextEditPatternAvailablePropertyId,
                     UIA_PropertyIds.UIA_IsValuePatternAvailablePropertyId,
                     UIA_PropertyIds.UIA_ValueIsReadOnlyPropertyId,
                 })
        {
            _cache.AddProperty(id);
        }
        _cache.AddPattern(UIA_PatternIds.UIA_TextPatternId);
        _walker = automation.RawViewWalker;
    }

    public TrackedSelection? Tracked { get; private set; }

    public void ClearTracking() => Tracked = null;

    /// <summary>Full probe: focused element → text pattern → selection → snapshot.</summary>
    public ProbeOutcome Probe(ReaderPolicy policy, bool explicitRequest)
    {
        var sw = Stopwatch.StartNew();
        IUIAutomationElement focused;
        try
        {
            focused = _automation.GetFocusedElementBuildCache(_cache);
        }
        catch (COMException ex)
        {
            return new ProbeOutcome.Failed($"GetFocusedElement failed (0x{ex.HResult:X8})");
        }
        if (focused is null) return new ProbeOutcome.Unsupported("no focused element");

        int pid;
        try { pid = focused.CachedProcessId; } catch (COMException) { return new ProbeOutcome.Failed("focused element vanished"); }

        var focusedHwnd = SafeHwnd(focused);
        if (pid == policy.OwnProcessId)
        {
            var root = focusedHwnd == IntPtr.Zero ? IntPtr.Zero : NativeMethods.GetAncestor(focusedHwnd, NativeMethods.GA_ROOT);
            if (policy.DemoWindow == IntPtr.Zero || root != policy.DemoWindow)
                return new ProbeOutcome.Skipped("own window");
        }

        // Locate an element exposing TextPattern: the focused element or a near ancestor.
        IUIAutomationElement? textElement = null;
        IUIAutomationTextPattern? pattern = null;
        var element = focused;
        var hwnd = focusedHwnd;
        var isPassword = SafeBool(() => focused.CachedIsPassword != 0);
        var controlType = SafeInt(() => focused.CachedControlType);
        var editable = IsEditable(focused, controlType);
        var elementRect = SafeRect(focused);

        for (var hop = 0; hop <= MaxAncestorHops && element is not null; hop++)
        {
            if (isPassword) return new ProbeOutcome.Skipped("password field");
            if (SafeBool(() => (bool)element.GetCachedPropertyValue(UIA_PropertyIds.UIA_IsTextPatternAvailablePropertyId)))
            {
                var p = SafePattern(element);
                if (p is not null)
                {
                    textElement = element;
                    pattern = p;
                    elementRect = SafeRect(element);
                    if (hop > 0) editable = editable || IsEditable(element, SafeInt(() => element.CachedControlType));
                    break;
                }
            }
            if (hwnd == IntPtr.Zero) hwnd = SafeHwnd(element);
            try { element = _walker.GetParentElementBuildCache(element, _cache); }
            catch (COMException) { element = null; }
            if (element is not null)
            {
                isPassword = SafeBool(() => element.CachedIsPassword != 0);
                if (hwnd == IntPtr.Zero) hwnd = SafeHwnd(element);
            }
        }

        if (pattern is null || textElement is null)
            return new ProbeOutcome.Unsupported($"no text pattern near focus (control type {controlType}, {NativeMethods.ClassNameOf(hwnd)})");

        if (hwnd == IntPtr.Zero) hwnd = SafeHwnd(textElement);
        if (hwnd == IntPtr.Zero) hwnd = NativeMethods.GetForegroundWindow();
        var rootWindow = hwnd == IntPtr.Zero ? IntPtr.Zero : NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
        if (rootWindow == IntPtr.Zero) rootWindow = hwnd;

        string text;
        double[]? quads;
        try
        {
            var selection = pattern.GetSelection();
            if (selection is null || selection.Length == 0) return new ProbeOutcome.NoSelection();
            var range = selection.GetElement(0);
            if (range is null) return new ProbeOutcome.NoSelection();
            text = range.GetText(policy.MaxChars + 1) ?? string.Empty;
            if (text.AsSpan().Trim().IsEmpty) return new ProbeOutcome.NoSelection();
            if (text.Length > policy.MaxChars) return new ProbeOutcome.Skipped($"selection longer than {policy.MaxChars} characters");
            quads = range.GetBoundingRectangles() as double[];
        }
        catch (COMException ex)
        {
            return new ProbeOutcome.Failed($"GetSelection failed (0x{ex.HResult:X8})");
        }
        catch (InvalidCastException)
        {
            return new ProbeOutcome.Failed("unexpected UIA return type");
        }

        if (elementRect.IsEmpty && rootWindow != IntPtr.Zero && NativeMethods.GetWindowRect(rootWindow, out var wr))
            elementRect = new PxRect(wr.Left, wr.Top, wr.Right, wr.Bottom);

        var lines = SelectionAnchor.FromUiaArray(quads);
        var anchor = SelectionAnchor.Compute(lines, elementRect.IsEmpty ? null : elementRect);
        var lineHeight = anchor?.LineHeight ?? 0;
        var editableKind = SelectionPolicy.ClassifyEditable(editable, elementRect.Height, lineHeight);

        Tracked = new TrackedSelection { Element = textElement, Pattern = pattern, Text = text, ElementRect = elementRect, RootWindow = rootWindow };

        var snapshot = new SelectionSnapshot(
            text,
            anchor,
            rootWindow.ToInt64(),
            pid,
            ProcessInfo.NameOf(pid),
            editableKind,
            SelectionOrigin.UiAutomation,
            sw.Elapsed.TotalMilliseconds);
        return new ProbeOutcome.Selected(snapshot);
    }

    /// <summary>Cheap refresh while the popup is visible: re-read the tracked selection's text and rectangles.</summary>
    public GeometryUpdate Refresh(ReaderPolicy policy)
    {
        var tracked = Tracked;
        if (tracked is null) return new GeometryUpdate.Unavailable("nothing tracked");
        if (tracked.RootWindow != IntPtr.Zero && !NativeMethods.IsWindow(tracked.RootWindow)) return new GeometryUpdate.Unavailable("source window closed");

        try
        {
            return ReadGeometry(tracked, policy);
        }
        catch (COMException)
        {
            // Element may have been re-created (virtualized views, page navigation): one full re-probe.
            var outcome = Probe(policy, explicitRequest: false);
            if (outcome is ProbeOutcome.Selected s)
            {
                if (!string.Equals(s.Snapshot.Text, tracked.Text, StringComparison.Ordinal)) return new GeometryUpdate.SelectionChanged();
                return s.Snapshot.Anchor is { } a ? new GeometryUpdate.Rects(a) : new GeometryUpdate.NoVisibleRects();
            }
            if (outcome is ProbeOutcome.NoSelection) return new GeometryUpdate.Cleared();
            return new GeometryUpdate.Unavailable("element unavailable");
        }
    }

    private GeometryUpdate ReadGeometry(TrackedSelection tracked, ReaderPolicy policy)
    {
        var selection = tracked.Pattern.GetSelection();
        if (selection is null || selection.Length == 0) return new GeometryUpdate.Cleared();
        var range = selection.GetElement(0);
        if (range is null) return new GeometryUpdate.Cleared();
        var text = range.GetText(policy.MaxChars + 1) ?? string.Empty;
        if (text.AsSpan().Trim().IsEmpty) return new GeometryUpdate.Cleared();
        if (!string.Equals(text, tracked.Text, StringComparison.Ordinal)) return new GeometryUpdate.SelectionChanged();

        var rect = SafeCurrentRect(tracked.Element);
        if (!rect.IsEmpty) tracked.ElementRect = rect;
        var quads = range.GetBoundingRectangles() as double[];
        var anchor = SelectionAnchor.Compute(SelectionAnchor.FromUiaArray(quads), tracked.ElementRect.IsEmpty ? null : tracked.ElementRect);
        return anchor is null ? new GeometryUpdate.NoVisibleRects() : new GeometryUpdate.Rects(anchor);
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static bool IsEditable(IUIAutomationElement element, int controlType)
    {
        var valueAvailable = SafeBool(() => (bool)element.GetCachedPropertyValue(UIA_PropertyIds.UIA_IsValuePatternAvailablePropertyId));
        var readOnly = valueAvailable && SafeBool(() => (bool)element.GetCachedPropertyValue(UIA_PropertyIds.UIA_ValueIsReadOnlyPropertyId));
        if (valueAvailable && !readOnly) return true;
        if (SafeBool(() => (bool)element.GetCachedPropertyValue(UIA_PropertyIds.UIA_IsTextEditPatternAvailablePropertyId))) return true;
        return controlType == UIA_ControlTypeIds.UIA_EditControlTypeId && !readOnly;
    }

    private static IUIAutomationTextPattern? SafePattern(IUIAutomationElement element)
    {
        try { return element.GetCachedPattern(UIA_PatternIds.UIA_TextPatternId) as IUIAutomationTextPattern; }
        catch (COMException) { return null; }
        catch (InvalidCastException) { return null; }
    }

    private static IntPtr SafeHwnd(IUIAutomationElement element)
    {
        try { return element.CachedNativeWindowHandle; }
        catch (COMException) { return IntPtr.Zero; }
    }

    private static PxRect SafeRect(IUIAutomationElement element)
    {
        try
        {
            var r = element.CachedBoundingRectangle;
            return new PxRect(r.left, r.top, r.right, r.bottom);
        }
        catch (COMException) { return PxRect.Empty; }
    }

    private static PxRect SafeCurrentRect(IUIAutomationElement element)
    {
        try
        {
            var r = element.CurrentBoundingRectangle;
            return new PxRect(r.left, r.top, r.right, r.bottom);
        }
        catch (COMException) { return PxRect.Empty; }
    }

    private static bool SafeBool(Func<bool> f)
    {
        try { return f(); }
        catch (COMException) { return false; }
        catch (InvalidCastException) { return false; }
        catch (NullReferenceException) { return false; }
    }

    private static int SafeInt(Func<int> f)
    {
        try { return f(); }
        catch (COMException) { return 0; }
    }
}
