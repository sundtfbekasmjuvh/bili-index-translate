using System.Runtime.InteropServices;
using BiliIndexTranslate.App.Native;
using BiliIndexTranslate.Core.Geometry;

namespace BiliIndexTranslate.App.Input;

internal enum InputSignal { MouseDown, MouseUp, KeyUpSelection, Escape, Hotkey }

internal readonly record struct InputEvent(InputSignal Signal, PxPoint Point, bool CtrlDown);

/// <summary>
/// Low-level mouse/keyboard hooks plus the optional global hotkey, on their own STA thread with a
/// message loop. Hook callbacks only classify the message and post a tiny event to the UI thread;
/// they never block, never touch UI Automation and never swallow input — except wheel events whose
/// cursor is over one of our own cards, which are forwarded to it (see <see cref="PopupWheelRegions"/>).
/// </summary>
internal sealed class InputHookThread : IDisposable
{
    private const int HotkeyId = 0x4251; // "BQ"

    private readonly Action<Action> _post;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private uint _threadId;
    private IntPtr _mouseHook;
    private IntPtr _keyboardHook;
    private LowLevelProc? _mouseProc;
    private LowLevelProc? _keyboardProc;
    private volatile bool _leftDown;
    private volatile bool _shiftUsedForSelection;
    private HotkeySpec? _pendingHotkey;
    private HotkeySpec? _registeredHotkey;
    private volatile bool _stop;

    public InputHookThread(Action<Action> post)
    {
        _post = post;
        _thread = new Thread(Run) { Name = "input-hooks", IsBackground = true };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    public event Action<InputEvent>? InputReceived;

    /// <summary>Raised when the wheel turns over the popup: (deltaY, ctrlHeld). Delivered on the UI thread.</summary>
    public event Action<int, bool>? WheelOverPopup;

    /// <summary>Raised when hotkey registration succeeds or fails; (display, error message or null).</summary>
    public event Action<string, string?>? HotkeyStatusChanged;

    private PxRect[] _popupRegions = Array.Empty<PxRect>();

    /// <summary>Screen rectangles of our visible cards (physical px); written on the UI thread, read on the hook thread.</summary>
    public PxRect[] PopupWheelRegions
    {
        get => Volatile.Read(ref _popupRegions);
        set => Volatile.Write(ref _popupRegions, value ?? Array.Empty<PxRect>());
    }

    public bool IsLeftButtonDown => _leftDown;
    public bool HooksInstalled { get; private set; }
    public string? HookError { get; private set; }

    public void Start()
    {
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(3));
    }

    public void SetHotkey(HotkeySpec? spec)
    {
        _pendingHotkey = spec;
        if (_threadId != 0) NativeMethods.PostThreadMessageW(_threadId, NativeMethods.WM_USER + 1, IntPtr.Zero, IntPtr.Zero);
    }

    private void Run()
    {
        _threadId = NativeMethods.GetCurrentThreadId();
        var module = NativeMethods.GetModuleHandleW(null);
        _mouseProc = MouseProc;
        _keyboardProc = KeyboardProc;
        _mouseHook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_MOUSE_LL, _mouseProc, module, 0);
        _keyboardHook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_KEYBOARD_LL, _keyboardProc, module, 0);
        HooksInstalled = _mouseHook != IntPtr.Zero && _keyboardHook != IntPtr.Zero;
        if (!HooksInstalled) HookError = $"SetWindowsHookEx failed (error {Marshal.GetLastWin32Error()})";
        _ready.Set();

        ApplyHotkey();

        while (!_stop && NativeMethods.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == NativeMethods.WM_HOTKEY && msg.wParam.ToInt32() == HotkeyId)
            {
                NativeMethods.GetCursorPos(out var pt);
                Emit(new InputEvent(InputSignal.Hotkey, new PxPoint(pt.X, pt.Y), false));
                continue;
            }
            if (msg.message == NativeMethods.WM_USER + 1) { ApplyHotkey(); continue; }
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessageW(ref msg);
        }

        if (_registeredHotkey is not null) NativeMethods.UnregisterHotKey(IntPtr.Zero, HotkeyId);
        if (_mouseHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(_mouseHook);
        if (_keyboardHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(_keyboardHook);
        _mouseHook = _keyboardHook = IntPtr.Zero;
    }

    private void ApplyHotkey()
    {
        var wanted = _pendingHotkey;
        if (_registeredHotkey is not null)
        {
            NativeMethods.UnregisterHotKey(IntPtr.Zero, HotkeyId);
            _registeredHotkey = null;
        }
        if (wanted is not { } spec) return;
        if (NativeMethods.RegisterHotKey(IntPtr.Zero, HotkeyId, spec.Modifiers | NativeMethods.MOD_NOREPEAT, spec.VirtualKey))
        {
            _registeredHotkey = spec;
            _post(() => HotkeyStatusChanged?.Invoke(spec.Display, null));
        }
        else
        {
            var err = Marshal.GetLastWin32Error();
            var msg = err == NativeMethods.ERROR_HOTKEY_ALREADY_REGISTERED
                ? $"{spec.Display} is already used by another application."
                : $"Could not register {spec.Display} (error {err}).";
            _post(() => HotkeyStatusChanged?.Invoke(spec.Display, msg));
        }
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        var msg = wParam.ToInt32();
        switch (msg)
        {
            case NativeMethods.WM_LBUTTONDOWN:
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                _leftDown = true;
                Emit(new InputEvent(InputSignal.MouseDown, new PxPoint(data.pt.X, data.pt.Y), false));
                break;
            }
            case NativeMethods.WM_LBUTTONUP:
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                _leftDown = false;
                Emit(new InputEvent(InputSignal.MouseUp, new PxPoint(data.pt.X, data.pt.Y), false));
                break;
            }
            case NativeMethods.WM_RBUTTONDOWN:
            case NativeMethods.WM_MBUTTONDOWN:
            case NativeMethods.WM_XBUTTONDOWN:
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                Emit(new InputEvent(InputSignal.MouseDown, new PxPoint(data.pt.X, data.pt.Y), false));
                break;
            }
            case NativeMethods.WM_MOUSEWHEEL:
            {
                var regions = PopupWheelRegions;
                if (regions.Length > 0)
                {
                    var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    foreach (var r in regions)
                    {
                        if (!r.Contains(data.pt.X, data.pt.Y)) continue;
                        var delta = unchecked((short)(data.mouseData >> 16));
                        var ctrl = NativeMethods.IsKeyDown(NativeMethods.VK_CONTROL);
                        _post(() => WheelOverPopup?.Invoke(delta, ctrl));
                        return new IntPtr(1); // consumed by our card; the source app must not scroll underneath it
                    }
                }
                break;
            }
        }
        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        var msg = wParam.ToInt32();
        var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        var vk = data.vkCode;
        var injected = (data.flags & NativeMethods.LLKHF_INJECTED) != 0;

        if (!injected)
        {
            if (msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN)
            {
                if (vk == NativeMethods.VK_ESCAPE) Emit(new InputEvent(InputSignal.Escape, default, false));
                else if (IsNavigationKey(vk) && NativeMethods.IsKeyDown(NativeMethods.VK_SHIFT)) _shiftUsedForSelection = true;
            }
            else if (msg is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP)
            {
                var ctrl = NativeMethods.IsKeyDown(NativeMethods.VK_CONTROL);
                if (vk is NativeMethods.VK_SHIFT or NativeMethods.VK_LSHIFT or NativeMethods.VK_RSHIFT)
                {
                    if (_shiftUsedForSelection)
                    {
                        _shiftUsedForSelection = false;
                        Emit(new InputEvent(InputSignal.KeyUpSelection, default, ctrl));
                    }
                }
                else if (vk == NativeMethods.VK_A && ctrl)
                {
                    Emit(new InputEvent(InputSignal.KeyUpSelection, default, true)); // Ctrl+A
                }
                else if (IsNavigationKey(vk) && NativeMethods.IsKeyDown(NativeMethods.VK_SHIFT))
                {
                    _shiftUsedForSelection = true; // extension in progress; probe once Shift is released
                }
            }
        }
        return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private static bool IsNavigationKey(uint vk) => vk is NativeMethods.VK_LEFT or NativeMethods.VK_RIGHT or NativeMethods.VK_UP or NativeMethods.VK_DOWN
        or NativeMethods.VK_HOME or NativeMethods.VK_END or NativeMethods.VK_PRIOR or NativeMethods.VK_NEXT;

    private void Emit(InputEvent e) => _post(() => InputReceived?.Invoke(e));

    public void Dispose()
    {
        _stop = true;
        if (_threadId != 0) NativeMethods.PostThreadMessageW(_threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        if (_thread.IsAlive && Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }
}
