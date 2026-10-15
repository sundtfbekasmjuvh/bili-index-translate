namespace BiliIndexTranslate.Core.Diagnostics;

public enum TimingCategory
{
    /// <summary>Mouse/key release → selection snapshot available (includes the stabilization delay).</summary>
    Detect,
    /// <summary>UIA probe alone (observer thread).</summary>
    Probe,
    /// <summary>Cache lookup.</summary>
    Cache,
    /// <summary>Popup content set → window positioned and visible.</summary>
    Render,
    /// <summary>HTTP round trip.</summary>
    Http,
    /// <summary>Geometry refresh while the popup is visible.</summary>
    Refresh,
}

public sealed record TimingSummary(TimingCategory Category, int Count, double P50, double P95, double Max, double Last);

/// <summary>
/// Bounded per-category samples (ms). Percentiles are computed on demand from the last
/// <c>windowSize</c> samples so the diagnostics view reflects current behavior, not the whole session.
/// </summary>
public sealed class TimingStats
{
    private readonly object _gate = new();
    private readonly Dictionary<TimingCategory, Ring> _rings = new();
    private readonly int _windowSize;

    public TimingStats(int windowSize = 128)
    {
        _windowSize = Math.Max(8, windowSize);
    }

    public void Record(TimingCategory category, double milliseconds)
    {
        if (!double.IsFinite(milliseconds) || milliseconds < 0) return;
        lock (_gate)
        {
            if (!_rings.TryGetValue(category, out var ring)) _rings[category] = ring = new Ring(_windowSize);
            ring.Add(milliseconds);
        }
    }

    public TimingSummary? Summary(TimingCategory category)
    {
        lock (_gate)
        {
            return _rings.TryGetValue(category, out var ring) && ring.Count > 0 ? ring.Summarize(category) : null;
        }
    }

    public IReadOnlyList<TimingSummary> All()
    {
        lock (_gate)
        {
            var list = new List<TimingSummary>();
            foreach (var c in Enum.GetValues<TimingCategory>())
                if (_rings.TryGetValue(c, out var ring) && ring.Count > 0) list.Add(ring.Summarize(c));
            return list;
        }
    }

    private sealed class Ring
    {
        private readonly double[] _values;
        private int _next;
        public int Count { get; private set; }
        public int Total { get; private set; }
        public double Last { get; private set; }

        public Ring(int size) { _values = new double[size]; }

        public void Add(double v)
        {
            _values[_next] = v;
            _next = (_next + 1) % _values.Length;
            if (Count < _values.Length) Count++;
            Total++;
            Last = v;
        }

        public TimingSummary Summarize(TimingCategory c)
        {
            var copy = new double[Count];
            Array.Copy(_values, copy, Count);
            Array.Sort(copy);
            return new TimingSummary(c, Total, Percentile(copy, 0.50), Percentile(copy, 0.95), copy[^1], Last);
        }

        private static double Percentile(double[] sorted, double p)
        {
            if (sorted.Length == 1) return sorted[0];
            var rank = p * (sorted.Length - 1);
            var lo = (int)Math.Floor(rank);
            var hi = Math.Min(lo + 1, sorted.Length - 1);
            var frac = rank - lo;
            return sorted[lo] + (sorted[hi] - sorted[lo]) * frac;
        }
    }
}
