using BiliIndexTranslate.App.Native;

namespace BiliIndexTranslate.App.Input;

internal readonly record struct HotkeySpec(uint Modifiers, uint VirtualKey, string Display);

/// <summary>Parses "Alt+Q", "Ctrl+Shift+T", "Win+F8" style strings. Letters, digits and F-keys only.</summary>
internal static class HotkeyParser
{
    public static bool TryParse(string? text, out HotkeySpec spec)
    {
        spec = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        uint mods = 0;
        uint vk = 0;
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var display = new List<string>();
        foreach (var raw in parts)
        {
            var part = raw.ToUpperInvariant();
            switch (part)
            {
                case "CTRL" or "CONTROL": mods |= NativeMethods.MOD_CONTROL; display.Add("Ctrl"); continue;
                case "ALT": mods |= NativeMethods.MOD_ALT; display.Add("Alt"); continue;
                case "SHIFT": mods |= NativeMethods.MOD_SHIFT; display.Add("Shift"); continue;
                case "WIN" or "WINDOWS": mods |= NativeMethods.MOD_WIN; display.Add("Win"); continue;
            }
            if (vk != 0) return false;
            if (part.Length == 1 && (char.IsAsciiLetterUpper(part[0]) || char.IsAsciiDigit(part[0]))) { vk = part[0]; display.Add(part); continue; }
            if (part.Length is 2 or 3 && part[0] == 'F' && int.TryParse(part.AsSpan(1), out var f) && f is >= 1 and <= 24) { vk = (uint)(0x70 + f - 1); display.Add("F" + f); continue; }
            return false;
        }
        if (vk == 0 || mods == 0) return false; // a bare key would hijack typing
        spec = new HotkeySpec(mods, vk, string.Join("+", display));
        return true;
    }
}
