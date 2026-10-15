using BiliIndexTranslate.Core.Geometry;

namespace BiliIndexTranslate.Core.Selection;

/// <summary>
/// The geometry the popup is attached to. <see cref="FirstLine"/> is the uppermost visible selected
/// line (the horizontal anchor); <see cref="Union"/> spans every visible selected line (the vertical anchor).
/// </summary>
public sealed record AnchorRect(PxRect FirstLine, PxRect Union, int LineCount, int LineHeight)
{
    public bool IsMultiLine => LineCount > 1;

    /// <summary>Anchor for geometry-less fallbacks (clipboard capture): a small box around a point.</summary>
    public static AnchorRect AroundPoint(PxPoint p, int halfSize = 10)
    {
        var r = new PxRect(p.X - halfSize, p.Y - halfSize, p.X + halfSize, p.Y + halfSize);
        return new AnchorRect(r, r, 1, halfSize * 2);
    }
}

public static class SelectionAnchor
{
    /// <summary>
    /// Builds an anchor from the per-line rectangles returned by UI Automation (physical pixels, any order).
    /// Rectangles are optionally clipped to the source window so that lines scrolled out of the visible
    /// client area do not pull the popup off the text. Returns null when nothing visible remains.
    /// </summary>
    public static AnchorRect? Compute(IReadOnlyList<PxRect> lineRects, PxRect? clip = null)
    {
        if (lineRects.Count == 0) return null;

        var lines = new List<PxRect>(lineRects.Count);
        foreach (var raw in lineRects)
        {
            if (raw.IsEmpty) continue;
            var r = clip is { } c ? raw.Intersect(c) : raw;
            if (r.IsEmpty) continue;
            lines.Add(r);
        }
        if (lines.Count == 0) return null;

        lines.Sort((a, b) => a.Top != b.Top ? a.Top.CompareTo(b.Top) : a.Left.CompareTo(b.Left));

        var first = lines[0];
        var union = first;
        var heights = new int[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            union = union.Union(lines[i]);
            heights[i] = lines[i].Height;
        }
        Array.Sort(heights);
        var lineHeight = heights[heights.Length / 2];

        // Merge fragments that UIA reports for the same visual line (e.g. per-run rectangles) so
        // that the horizontal anchor is the whole first line, not its first word.
        var firstLineBand = first;
        foreach (var r in lines)
        {
            if (Math.Abs(r.Top - first.Top) <= Math.Max(2, lineHeight / 3))
                firstLineBand = firstLineBand.Union(r);
        }

        var distinctLines = CountDistinctLines(lines, lineHeight);
        return new AnchorRect(firstLineBand, union, distinctLines, Math.Max(1, lineHeight));
    }

    private static int CountDistinctLines(List<PxRect> sortedByTop, int lineHeight)
    {
        var count = 0;
        var lastTop = int.MinValue;
        var tolerance = Math.Max(2, lineHeight / 3);
        foreach (var r in sortedByTop)
        {
            if (count == 0 || Math.Abs(r.Top - lastTop) > tolerance)
            {
                count++;
                lastTop = r.Top;
            }
        }
        return count;
    }

    /// <summary>Converts the flat [left, top, width, height, ...] array produced by UIA into rectangles.</summary>
    public static List<PxRect> FromUiaArray(double[]? quads)
    {
        var list = new List<PxRect>();
        if (quads is null) return list;
        for (var i = 0; i + 3 < quads.Length; i += 4)
        {
            if (double.IsNaN(quads[i]) || double.IsNaN(quads[i + 1]) || double.IsNaN(quads[i + 2]) || double.IsNaN(quads[i + 3])) continue;
            var left = (int)Math.Floor(quads[i]);
            var top = (int)Math.Floor(quads[i + 1]);
            var right = (int)Math.Ceiling(quads[i] + quads[i + 2]);
            var bottom = (int)Math.Ceiling(quads[i + 1] + quads[i + 3]);
            list.Add(new PxRect(left, top, right, bottom));
        }
        return list;
    }
}
