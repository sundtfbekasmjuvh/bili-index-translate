using System.IO;
using System.Text;

namespace BiliIndexTranslate.App.Native;

/// <summary>Resolves process ids to executable names (without extension) with a small cache.</summary>
internal static class ProcessInfo
{
    private static readonly object Gate = new();
    private static readonly Dictionary<int, (string Name, long Stamp)> Cache = new();
    private const int MaxEntries = 64;

    public static string NameOf(int pid)
    {
        if (pid <= 0) return "(unknown)";
        lock (Gate)
        {
            if (Cache.TryGetValue(pid, out var hit) && Environment.TickCount64 - hit.Stamp < 60_000) return hit.Name;
        }

        var name = Query(pid);
        lock (Gate)
        {
            if (Cache.Count >= MaxEntries)
            {
                var oldest = Cache.OrderBy(kv => kv.Value.Stamp).First().Key;
                Cache.Remove(oldest);
            }
            Cache[pid] = (name, Environment.TickCount64);
        }
        return name;
    }

    private static string Query(int pid)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle != IntPtr.Zero)
        {
            try
            {
                var sb = new StringBuilder(1024);
                var size = sb.Capacity;
                if (NativeMethods.QueryFullProcessImageNameW(handle, 0, sb, ref size))
                    return Path.GetFileNameWithoutExtension(sb.ToString(0, size));
            }
            finally { NativeMethods.CloseHandle(handle); }
        }
        try { return System.Diagnostics.Process.GetProcessById(pid).ProcessName; }
        catch { return "pid" + pid; }
    }
}
