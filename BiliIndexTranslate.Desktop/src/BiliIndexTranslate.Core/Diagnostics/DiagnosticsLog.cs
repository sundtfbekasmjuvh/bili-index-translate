using System.Text;

namespace BiliIndexTranslate.Core.Diagnostics;

public enum DiagnosticLevel { Info, Warning, Error }

public sealed record DiagnosticEntry(DateTimeOffset Time, DiagnosticLevel Level, string Category, string Message)
{
    public override string ToString() => $"{Time:HH:mm:ss.fff} {LevelTag(Level)} {Category,-10} {Message}";

    private static string LevelTag(DiagnosticLevel l) => l switch
    {
        DiagnosticLevel.Warning => "WARN",
        DiagnosticLevel.Error => "ERR ",
        _ => "INFO",
    };
}

/// <summary>
/// Thread-safe bounded ring buffer. Messages must never contain selected text, translations, or
/// API keys; callers pass lengths/hashes instead. Optional sink receives entries (file logging).
/// </summary>
public sealed class DiagnosticsLog
{
    private readonly object _gate = new();
    private readonly DiagnosticEntry[] _buffer;
    private int _head;
    private int _count;
    private Action<DiagnosticEntry>? _sink;

    public DiagnosticsLog(int capacity = 300)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _buffer = new DiagnosticEntry[capacity];
    }

    public int Capacity => _buffer.Length;

    public event Action<DiagnosticEntry>? EntryAdded;

    public void SetSink(Action<DiagnosticEntry>? sink)
    {
        lock (_gate) _sink = sink;
    }

    public void Info(string category, string message) => Add(DiagnosticLevel.Info, category, message);
    public void Warn(string category, string message) => Add(DiagnosticLevel.Warning, category, message);
    public void Error(string category, string message) => Add(DiagnosticLevel.Error, category, message);

    public void Add(DiagnosticLevel level, string category, string message)
    {
        var entry = new DiagnosticEntry(DateTimeOffset.Now, level, category, message);
        Action<DiagnosticEntry>? sink;
        lock (_gate)
        {
            _buffer[(_head + _count) % _buffer.Length] = entry;
            if (_count < _buffer.Length) _count++;
            else _head = (_head + 1) % _buffer.Length;
            sink = _sink;
        }
        try { sink?.Invoke(entry); } catch { /* sinks must not break the app */ }
        EntryAdded?.Invoke(entry);
    }

    public IReadOnlyList<DiagnosticEntry> Snapshot()
    {
        lock (_gate)
        {
            var list = new DiagnosticEntry[_count];
            for (var i = 0; i < _count; i++) list[i] = _buffer[(_head + i) % _buffer.Length];
            return list;
        }
    }

    public string Render()
    {
        var sb = new StringBuilder();
        foreach (var e in Snapshot()) sb.AppendLine(e.ToString());
        return sb.ToString();
    }

    /// <summary>Stable, non-reversible fingerprint for correlating log lines about the same text.</summary>
    public static string Fingerprint(string text)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (var ch in text) { h ^= ch; h *= 16777619; }
            return h.ToString("x8");
        }
    }
}
