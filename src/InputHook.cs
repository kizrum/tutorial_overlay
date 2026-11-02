using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace TutorialOverlay;

internal enum InputKind { MouseDown, MouseUp, KeyDown }

internal readonly record struct InputEvent(InputKind Kind, int X, int Y, string Button, uint Vk, uint Mods, bool Injected, bool Swallowed, long Ticks);

/// <summary>
/// WH_MOUSE_LL + WH_KEYBOARD_LL on a dedicated thread with its own message loop. Callbacks only enqueue:
/// a slow low-level hook lags the whole system's input, and Windows silently removes hooks that exceed
/// LowLevelHooksTimeout — the watchdog below notices that and re-installs them.
/// CallNextHookEx is always called, except for the one click swallowed while the agent is picking a point.
/// </summary>
internal static class InputHook
{
    public static readonly BlockingCollection<InputEvent> Queue = new(new ConcurrentQueue<InputEvent>(), 4096);

    /// <summary>Key presses are queued only while something needs them (a "key" condition or a pick). Never logged.</summary>
    public static volatile bool KeysWanted;
    /// <summary>Swallow the next mouse click (down + its up) instead of passing it to the app.</summary>
    public static volatile bool SwallowNextClick;

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
    private const int WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_MBUTTONUP = 0x0208;
    private const uint WM_APP_REINSTALL = 0x8000 + 17;

    private static Native.LowLevelProc? _mouseProc, _keyProc;   // keep the delegates alive
    private static IntPtr _mouseHook, _keyHook;
    private static uint _threadId;
    private static int _lastCallbackTick = Environment.TickCount;
    private static int _swallowingButtonUp;                    // WM_*BUTTONUP to eat after a swallowed down
    private static volatile bool _stop;
    public static int Reinstalls { get; private set; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize, dwTime; }

    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);

    public static void Start()
    {
        var ready = new ManualResetEventSlim();
        new Thread(() =>
        {
            _threadId = Native.GetCurrentThreadId();
            _mouseProc = MouseCallback;
            _keyProc = KeyCallback;
            Install();
            ready.Set();
            while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.hwnd == IntPtr.Zero && msg.message == WM_APP_REINSTALL)
                {
                    Uninstall();
                    Install();
                }
            }
            Uninstall();
        }) { IsBackground = true, Name = "input-hook", Priority = ThreadPriority.Highest }.Start();
        ready.Wait(3000);
        new Thread(Watchdog) { IsBackground = true, Name = "hook-watchdog" }.Start();
    }

    public static void Stop()
    {
        _stop = true;
        if (_threadId != 0) Native.PostThreadMessage(_threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        Queue.CompleteAdding();
    }

    private static void Install()
    {
        var mod = Native.GetModuleHandle(null);
        _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc!, mod, 0);
        _keyHook = Native.SetWindowsHookEx(WH_KEYBOARD_LL, _keyProc!, mod, 0);
        if (_mouseHook == IntPtr.Zero || _keyHook == IntPtr.Zero) Log.Warn("SetWindowsHookEx failed: " + Marshal.GetLastWin32Error());
        _lastCallbackTick = Environment.TickCount;
    }

    private static void Uninstall()
    {
        if (_mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_mouseHook);
        if (_keyHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_keyHook);
        _mouseHook = _keyHook = IntPtr.Zero;
    }

    /// <summary>
    /// The system saw input (GetLastInputInfo moved) but none of it reached our hooks for a while:
    /// Windows has dropped them. Re-install on the hook thread.
    /// </summary>
    private static void Watchdog()
    {
        while (!_stop)
        {
            Thread.Sleep(1000);
            var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref lii)) continue;
            int lastInput = (int)lii.dwTime, now = Environment.TickCount;
            int silentFor = lastInput - _lastCallbackTick;          // input newer than our last callback
            if (silentFor > 1500 && now - lastInput < 10000)
            {
                Reinstalls++;
                Log.Warn($"input hooks went silent for {silentFor} ms while the user was active: re-installing");
                Events.Emit("hook_reinstalled", new() { ["silentMs"] = silentFor, ["count"] = Reinstalls });
                _lastCallbackTick = now;
                Native.PostThreadMessage(_threadId, WM_APP_REINSTALL, IntPtr.Zero, IntPtr.Zero);
            }
        }
    }

    private static uint Modifiers()
    {
        uint m = 0;
        if (GetAsyncKeyState(0x11) < 0) m |= Native.MOD_CONTROL;
        if (GetAsyncKeyState(0x12) < 0) m |= Native.MOD_ALT;
        if (GetAsyncKeyState(0x10) < 0) m |= Native.MOD_SHIFT;
        if (GetAsyncKeyState(0x5B) < 0 || GetAsyncKeyState(0x5C) < 0) m |= Native.MOD_WIN;
        return m;
    }

    private static unsafe IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        _lastCallbackTick = Environment.TickCount;
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            var info = (Native.MSLLHOOKSTRUCT*)lParam;
            bool injected = (info->flags & Native.LLMHF_INJECTED) != 0;
            switch (msg)
            {
                case Native.WM_LBUTTONDOWN or Native.WM_RBUTTONDOWN or Native.WM_MBUTTONDOWN:
                {
                    var button = msg == Native.WM_LBUTTONDOWN ? "left" : msg == Native.WM_RBUTTONDOWN ? "right" : "middle";
                    bool swallow = SwallowNextClick && !injected;
                    Queue.TryAdd(new InputEvent(InputKind.MouseDown, info->pt.X, info->pt.Y, button, 0, 0, injected, swallow, DateTime.UtcNow.Ticks));
                    if (swallow)
                    {
                        SwallowNextClick = false;
                        _swallowingButtonUp = msg + 1;   // *BUTTONUP = *BUTTONDOWN + 1
                        return (IntPtr)1;
                    }
                    break;
                }
                case WM_LBUTTONUP or WM_RBUTTONUP or WM_MBUTTONUP:
                {
                    var button = msg == WM_LBUTTONUP ? "left" : msg == WM_RBUTTONUP ? "right" : "middle";
                    if (_swallowingButtonUp == msg) { _swallowingButtonUp = 0; return (IntPtr)1; }
                    Queue.TryAdd(new InputEvent(InputKind.MouseUp, info->pt.X, info->pt.Y, button, 0, 0, injected, false, DateTime.UtcNow.Ticks));
                    break;
                }
            }
        }
        return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private static unsafe IntPtr KeyCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        _lastCallbackTick = Environment.TickCount;
        if (nCode >= 0 && KeysWanted)
        {
            int msg = (int)wParam;
            if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
            {
                var k = (KBDLLHOOKSTRUCT*)lParam;
                uint vk = k->vkCode;
                bool isModifier = vk is 0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C;
                if (!isModifier)
                    Queue.TryAdd(new InputEvent(InputKind.KeyDown, 0, 0, "", vk, Modifiers(), (k->flags & 0x10) != 0, false, DateTime.UtcNow.Ticks));
            }
        }
        return Native.CallNextHookEx(_keyHook, nCode, wParam, lParam);
    }
}
