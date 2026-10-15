using System.Runtime.InteropServices;
using System.Windows;
using BiliIndexTranslate.App.Native;

namespace BiliIndexTranslate.App.Input;

/// <summary>
/// Explicit-only clipboard capture (fallback shortcut or tray command). Sends one Ctrl+C to the
/// foreground application, waits briefly for the clipboard sequence number to change, and reads the
/// text. Never runs automatically and never polls the clipboard.
/// </summary>
internal static class ClipboardFallback
{
    public static async Task<string?> CaptureSelectionAsync(int timeoutMs = 350)
    {
        var before = NativeMethods.GetClipboardSequenceNumber();
        var sent = SendCtrlC();
        if (sent == 0) return null;

        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            await Task.Delay(25).ConfigureAwait(true);
            if (NativeMethods.GetClipboardSequenceNumber() != before) break;
        }
        if (NativeMethods.GetClipboardSequenceNumber() == before) return null;
        return ReadText();
    }

    public static string? ReadText()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return Clipboard.ContainsText() ? Clipboard.GetText() : null;
            }
            catch (COMException)
            {
                Thread.Sleep(30); // clipboard busy
            }
        }
        return null;
    }

    private static uint SendCtrlC()
    {
        // Release Alt/Shift/Win if the hotkey still holds them so the target sees a clean Ctrl+C.
        var inputs = new List<INPUT>();
        foreach (var mod in new ushort[] { NativeMethods.VK_MENU, NativeMethods.VK_SHIFT, NativeMethods.VK_LWIN, NativeMethods.VK_RWIN })
            if (NativeMethods.IsKeyDown(mod)) inputs.Add(Key(mod, up: true));
        inputs.Add(Key(NativeMethods.VK_CONTROL, up: false));
        inputs.Add(Key(NativeMethods.VK_C, up: false));
        inputs.Add(Key(NativeMethods.VK_C, up: true));
        inputs.Add(Key(NativeMethods.VK_CONTROL, up: true));
        var arr = inputs.ToArray();
        return NativeMethods.SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());
    }

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        u = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? NativeMethods.KEYEVENTF_KEYUP : 0 } },
    };
}
