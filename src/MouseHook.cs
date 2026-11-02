using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace TutorialOverlay;

internal readonly record struct ClickEvent(int X, int Y, string Button, bool Injected, long TimestampTicks);

/// <summary>
/// WH_MOUSE_LL on a dedicated thread with its own message loop. The callback does nothing but enqueue:
/// a slow low-level hook makes the whole system's mouse lag (and Windows silently unhooks it).
/// CallNextHookEx is always called, so every click still reaches the application.
/// </summary>
internal static class MouseHook
{
    public static readonly BlockingCollection<ClickEvent> Queue = new(new ConcurrentQueue<ClickEvent>(), 4096);
    private static Native.LowLevelProc? _proc;   // keep the delegate alive
    private static IntPtr _hook;
    private static uint _threadId;
    private static Thread? _thread;

    public static void Start()
    {
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = Native.GetCurrentThreadId();
            _proc = Callback;
            _hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _proc, Native.GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero) Log.Warn("SetWindowsHookEx failed: " + Marshal.GetLastWin32Error());
            ready.Set();
            while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0) { }
            if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook);
        }) { IsBackground = true, Name = "mouse-hook", Priority = ThreadPriority.Highest };
        _thread.Start();
        ready.Wait(3000);
    }

    public static void Stop()
    {
        if (_threadId != 0) Native.PostThreadMessage(_threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        Queue.CompleteAdding();
    }

    private static IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            if (msg == Native.WM_LBUTTONDOWN || msg == Native.WM_RBUTTONDOWN || msg == Native.WM_MBUTTONDOWN)
            {
                var info = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                var button = msg == Native.WM_LBUTTONDOWN ? "left" : msg == Native.WM_RBUTTONDOWN ? "right" : "middle";
                Queue.TryAdd(new ClickEvent(info.pt.X, info.pt.Y, button, (info.flags & Native.LLMHF_INJECTED) != 0, DateTime.UtcNow.Ticks));
            }
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}
