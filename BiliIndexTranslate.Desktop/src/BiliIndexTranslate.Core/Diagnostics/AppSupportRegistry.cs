namespace BiliIndexTranslate.Core.Diagnostics;

public enum SupportLevel { Unknown, Works, Partial, Unsupported }

public sealed record AppSupportEntry(string ProcessName, SupportLevel Level, int Successes, int NoGeometry, int Unsupported, int Failures, DateTimeOffset LastSeen)
{
    public override string ToString() => $"{ProcessName,-24} {Level,-11} ok={Successes} noGeom={NoGeometry} unsupported={Unsupported} failed={Failures}";
}

/// <summary>
/// Observed per-process compatibility, built from real probe outcomes during the session.
/// This is what the diagnostics view shows instead of a hard-coded "supported apps" list.
/// </summary>
public sealed class AppSupportRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Counters> _apps = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _maxEntries;

    public AppSupportRegistry(int maxEntries = 64) { _maxEntries = Math.Max(8, maxEntries); }

    public void RecordSuccess(string processName, bool hadGeometry) => Update(processName, c => { if (hadGeometry) c.Successes++; else c.NoGeometry++; });
    public void RecordUnsupported(string processName) => Update(processName, c => c.Unsupported++);
    public void RecordFailure(string processName) => Update(processName, c => c.Failures++);

    public IReadOnlyList<AppSupportEntry> Snapshot()
    {
        lock (_gate)
        {
            return _apps.Select(kv => kv.Value.ToEntry(kv.Key)).OrderByDescending(e => e.LastSeen).ToList();
        }
    }

    private void Update(string processName, Action<Counters> mutate)
    {
        if (string.IsNullOrWhiteSpace(processName)) processName = "(unknown)";
        lock (_gate)
        {
            if (!_apps.TryGetValue(processName, out var c))
            {
                if (_apps.Count >= _maxEntries)
                {
                    var oldest = _apps.OrderBy(kv => kv.Value.LastSeen).First().Key;
                    _apps.Remove(oldest);
                }
                _apps[processName] = c = new Counters();
            }
            mutate(c);
            c.LastSeen = DateTimeOffset.Now;
        }
    }

    private sealed class Counters
    {
        public int Successes, NoGeometry, Unsupported, Failures;
        public DateTimeOffset LastSeen;

        public AppSupportEntry ToEntry(string name)
        {
            var level = SupportLevel.Unknown;
            var total = Successes + NoGeometry + Unsupported + Failures;
            if (total > 0)
            {
                if (Successes > 0 && Unsupported == 0 && Failures == 0) level = SupportLevel.Works;
                else if (Successes > 0 || NoGeometry > 0) level = SupportLevel.Partial;
                else level = SupportLevel.Unsupported;
            }
            return new AppSupportEntry(name, level, Successes, NoGeometry, Unsupported, Failures, LastSeen);
        }
    }
}
