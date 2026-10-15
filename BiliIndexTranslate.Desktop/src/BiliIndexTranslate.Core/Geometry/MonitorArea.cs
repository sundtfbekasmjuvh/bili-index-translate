namespace BiliIndexTranslate.Core.Geometry;

/// <summary>
/// One monitor as seen by the placement engine. <see cref="Scale"/> is the effective DPI divided by 96.
/// Bounds and work area are physical pixels in the virtual-desktop coordinate space (may be negative).
/// </summary>
public sealed record MonitorArea(PxRect Bounds, PxRect WorkArea, double Scale, bool IsPrimary, string Name)
{
    public static MonitorArea Default(int width = 1920, int height = 1080, double scale = 1.0)
        => new(new PxRect(0, 0, width, height), new PxRect(0, 0, width, height - 48), scale, true, "default");

    /// <summary>Converts logical (96-dpi) units to physical pixels on this monitor.</summary>
    public int Px(double logical) => (int)Math.Round(logical * Scale);

    /// <summary>Converts physical pixels to logical units on this monitor.</summary>
    public double Logical(int px) => px / Scale;
}

/// <summary>Locates the monitor that should host a popup for a given anchor.</summary>
public interface IMonitorLocator
{
    MonitorArea FromRect(PxRect anchor);
    MonitorArea FromPoint(PxPoint point);
}

/// <summary>A static locator over a fixed list of monitors. Used by tests and as a fallback.</summary>
public sealed class StaticMonitorLocator : IMonitorLocator
{
    private readonly IReadOnlyList<MonitorArea> _monitors;

    public StaticMonitorLocator(params MonitorArea[] monitors)
    {
        if (monitors.Length == 0) throw new ArgumentException("At least one monitor is required.", nameof(monitors));
        _monitors = monitors;
    }

    public MonitorArea FromRect(PxRect anchor)
    {
        if (anchor.IsEmpty) return FromPoint(new PxPoint(anchor.Left, anchor.Top));
        MonitorArea? best = null;
        long bestArea = -1;
        foreach (var m in _monitors)
        {
            var i = m.Bounds.Intersect(anchor);
            long area = i.IsEmpty ? 0 : (long)i.Width * i.Height;
            if (area > bestArea) { bestArea = area; best = m; }
        }
        return bestArea > 0 ? best! : FromPoint(new PxPoint(anchor.CenterX, anchor.CenterY));
    }

    public MonitorArea FromPoint(PxPoint point)
    {
        foreach (var m in _monitors)
            if (m.Bounds.Contains(point)) return m;

        // Nearest monitor by distance to bounds.
        MonitorArea best = _monitors[0];
        long bestDist = long.MaxValue;
        foreach (var m in _monitors)
        {
            long dx = Math.Max(Math.Max(m.Bounds.Left - point.X, 0), point.X - m.Bounds.Right + 1);
            long dy = Math.Max(Math.Max(m.Bounds.Top - point.Y, 0), point.Y - m.Bounds.Bottom + 1);
            long d = dx * dx + dy * dy;
            if (d < bestDist) { bestDist = d; best = m; }
        }
        return best;
    }
}
