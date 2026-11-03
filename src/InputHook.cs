using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace TutorialOverlay;

internal enum InputKind { MouseDown, MouseUp, KeyDown, Move, Wheel }

internal readonly record struct InputEvent(InputKind Kind, int X, int Y, string Button, uint Vk, uint Mods, bool Injected, bool Swallowed, long Ticks, int Delta = 0);

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
    /// <summary>
    /// Mouse moves (only while a button is held) and wheel turns are queued only while a step needs them (trace,
    /// scroll, a drag with held keys); otherwise the hook passes them on untouched. Never logged.
    /// </summary>
    public static volatile bool MotionWanted;
    /// <summary>Swallow the next mouse click (down + its up) instead of passing it to the app.</summary>
    public static volatile bool SwallowNextClick;

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
    private const int WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_MBUTTONUP = 0x0208;
    private const int WM_MOUSEMOVE = 0x0200, WM_MOUSEWHEEL = 0x020A, WM_MOUSEHWHEEL = 0x020E;
    private static volatile int _buttonsHeld;
    private static int _lastMoveTick;
    private const uint WM_APP_REINSTALL = 0x8000 + 17;

    private static Native.LowLevelProc? _mouseProc, _keyProc;   // keep the delegates alive
    private static IntPtr _mouseHook, _keyHook;
    private static uint _threadId;
    // when each hook was last called: Windows drops hooks one by one, so each is watched on its own
    private static int _lastMouseTick = Environment.TickCount, _lastKeyTick = Environment.TickCount;
    private static int LastCallbackTick => Math.Max(_lastMouseTick, _lastKeyTick);
    private static int _swallowingButtonUp;                    // WM_*BUTTONUP to eat after a swallowed down
    private static volatile bool _stop;
    public static int Reinstalls { get; private set; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize, dwTime; }

    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    public static void Start()
    {
        var ready = new ManualResetEventSlim();
        new Thread(() =>
        {
            _threadId = Native.GetCurrentThreadId();
            // Windows must not slow this thread down to save power: a hook that answers late is silently removed
            Native.NoPowerThrottlingForThisThread();
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
                else if (msg.hwnd == IntPtr.Zero && msg.message == WM_APP_PING) _pongTick = Environment.TickCount;
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
        _lastMouseTick = _lastKeyTick = Environment.TickCount;
    }

    private static void Uninstall()
    {
        if (_mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_mouseHook);
        if (_keyHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_keyHook);
        _mouseHook = _keyHook = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }

    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1, MOUSEEVENTF_MOVE = 0x0001, KEYEVENTF_KEYUP = 0x0002;
    /// <summary>A virtual-key code no key has (0x0A is reserved): releasing it types nothing and no app reacts.</summary>
    private const ushort ProbeVk = 0x0A;
    private static int _lastFalseAlarmLog, _lastRoutineProbe;
    /// <summary>While the user is active, both hooks are also probed this often (a silent mouse can't hide a dead keyboard hook).</summary>
    private const int RoutineProbeMs = 30000;

    // ---- diagnostics for "dropped" hooks: is the hook thread itself stuck, how long do callbacks take, GC pauses
    private const uint WM_APP_PING = 0x8000 + 18;
    private static volatile int _pongTick;
    private static long _slowestCallbackTicks;          // Stopwatch ticks, since the last report
    private static TimeSpan _gcPauseAtLastReport = GC.GetTotalPauseDuration();

    /// <summary>Does the hook thread answer its message queue? (a stuck thread also makes every hook "time out")</summary>
    private static int PingHookThread()
    {
        int sent = Environment.TickCount;
        _pongTick = 0;
        Native.PostThreadMessage(_threadId, WM_APP_PING, IntPtr.Zero, IntPtr.Zero);
        for (int i = 0; i < 40; i++)
        {
            Thread.Sleep(25);
            if (_pongTick != 0) return _pongTick - sent;
        }
        return -1;
    }

    private static void NoteCallback(long startTimestamp)
    {
        long took = System.Diagnostics.Stopwatch.GetTimestamp() - startTimestamp;
        if (took > Interlocked.Read(ref _slowestCallbackTicks)) Interlocked.Exchange(ref _slowestCallbackTicks, took);
    }

    private static string Diagnostics()
    {
        double slowest = Interlocked.Exchange(ref _slowestCallbackTicks, 0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        var gc = GC.GetTotalPauseDuration();
        var gcDelta = gc - _gcPauseAtLastReport;
        _gcPauseAtLastReport = gc;
        int ping = PingHookThread();
        return $"hook thread answers: {(ping < 0 ? "NO (stuck > 1 s)" : ping + " ms")}; slowest callback since last report: {slowest:0.0} ms; " +
               $"GC pauses since last report: {gcDelta.TotalMilliseconds:0} ms; managed heap {GC.GetTotalMemory(false) / 1048576} MB";
    }

    /// <summary>
    /// Are the hooks still installed? Each gets something it must see within a moment, nothing anyone notices:
    /// the mouse hook a move of 1 px and back (the cursor ends where it was; a move of 0 px is no movement, Windows
    /// drops it before any hook sees it), the keyboard hook the release of a key code no key has. Injected input
    /// goes through low-level hooks like real input.
    /// </summary>
    private static (bool mouse, bool keys) ProbeAlive()
    {
        int mouseBefore = _lastMouseTick, keyBefore = _lastKeyTick;
        var input = new[]
        {
            new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dx = 1, dwFlags = MOUSEEVENTF_MOVE } } },
            new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dx = -1, dwFlags = MOUSEEVENTF_MOVE } } },
            new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = ProbeVk, dwFlags = KEYEVENTF_KEYUP } } },
        };
        uint sent = SendInput((uint)input.Length, input, Marshal.SizeOf<INPUT>());
        if (sent != input.Length)
        {
            // blocked (e.g. the secure desktop is up): nothing can be told — count them as alive rather than churn
            Log.Debug($"hook probe not sent: SendInput sent {sent} of {input.Length}, error {Marshal.GetLastWin32Error()} (size {Marshal.SizeOf<INPUT>()})");
            return (true, true);
        }
        bool mouse = false, keys = false;
        int waited = 0;
        for (int i = 0; i < 6 && !(mouse && keys); i++)
        {
            Thread.Sleep(50);
            waited += 50;
            mouse |= _lastMouseTick != mouseBefore;
            keys |= _lastKeyTick != keyBefore;
        }
        Log.Debug($"hook probe: mouse {(mouse ? "reached" : "NOT reached")}, keyboard {(keys ? "reached" : "NOT reached")} ({waited} ms)");
        return (mouse, keys);
    }

    private static string ForegroundInfo()
    {
        var fg = GetForegroundWindow();
        int pid = fg == IntPtr.Zero ? 0 : Native.ProcessId(fg);
        if (pid == 0) return "no foreground window";
        var name = Engine.ProcName(pid);
        return Native.IsAboveUs(pid) ? $"{name} (run as administrator)" : name;
    }

    /// <summary>
    /// The system saw input (GetLastInputInfo moved) that none of our hooks saw. Either Windows dropped the hooks
    /// (it removes a low-level hook that once answers too slowly, without telling) — then they are re-installed on
    /// the hook thread — or the input went where hooks do not see it: a window run as administrator, the sign-in or
    /// UAC screen, and touchpad scrolling / gestures that apps such as browsers and VS Code take straight from the
    /// touchpad, without mouse messages. A probe tells the two apart, so hooks are only re-installed when they are
    /// really gone.
    /// </summary>
    private static void Watchdog()
    {
        while (!_stop)
        {
            Thread.Sleep(1000);
            var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref lii)) continue;
            int lastInput = (int)lii.dwTime, now = Environment.TickCount;
            if (now - lastInput >= 10000) continue;                 // the user is away: nothing to check
            int silentFor = lastInput - LastCallbackTick;           // input newer than our last callback
            bool routine = now - _lastRoutineProbe >= RoutineProbeMs;
            if (silentFor <= 1500 && !routine) continue;
            _lastRoutineProbe = now;
            var where = ForegroundInfo();
            var (mouseOk, keysOk) = ProbeAlive();
            if (mouseOk && keysOk)
            {
                if (silentFor <= 1500) continue;                    // a routine check, all fine
                // alive: that input went somewhere hooks do not see — nothing to repair, nothing for the agent
                if (now - _lastFalseAlarmLog > 60000)
                {
                    _lastFalseAlarmLog = now;
                    Log.Info($"input hooks are alive (a probe reached them): the user's input went where hooks do not see it ({where}; " +
                             $"the last input the hooks saw was {silentFor / 1000.0:0.#} s before it)");
                }
                continue;
            }
            Reinstalls++;
            var dead = !mouseOk && !keysOk ? "mouse and keyboard" : !mouseOk ? "mouse" : "keyboard";
            Log.Warn($"input hook dropped by Windows: {dead} (a probe did not reach it; the last input the hooks saw was {Math.Max(0, silentFor) / 1000.0:0.#} s before the user's latest; foreground: {where}): re-installing. " +
                     Diagnostics());
            Events.Emit("hook_reinstalled", new() { ["hooks"] = dead, ["silentMs"] = Math.Max(0, silentFor), ["count"] = Reinstalls, ["foreground"] = where });
            _lastMouseTick = _lastKeyTick = Environment.TickCount;
            Native.PostThreadMessage(_threadId, WM_APP_REINSTALL, IntPtr.Zero, IntPtr.Zero);
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
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        _lastMouseTick = Environment.TickCount;
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
                    if (!swallow) _buttonsHeld++;
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
                    _buttonsHeld = Math.Max(0, _buttonsHeld - 1);
                    Queue.TryAdd(new InputEvent(InputKind.MouseUp, info->pt.X, info->pt.Y, button, 0, 0, injected, false, DateTime.UtcNow.Ticks));
                    break;
                }
                case WM_MOUSEMOVE when MotionWanted && _buttonsHeld > 0:
                {
                    // ~120 samples a second is plenty to follow a stroke
                    int now = Environment.TickCount;
                    if (now - _lastMoveTick >= 8)
                    {
                        _lastMoveTick = now;
                        Queue.TryAdd(new InputEvent(InputKind.Move, info->pt.X, info->pt.Y, "", 0, 0, injected, false, DateTime.UtcNow.Ticks));
                    }
                    break;
                }
                case WM_MOUSEWHEEL or WM_MOUSEHWHEEL when MotionWanted:
                    Queue.TryAdd(new InputEvent(InputKind.Wheel, info->pt.X, info->pt.Y, msg == WM_MOUSEWHEEL ? "v" : "h", 0, 0, injected, false,
                                                DateTime.UtcNow.Ticks, (short)(info->mouseData >> 16)));
                    break;
            }
        }
        NoteCallback(started);
        return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    /// <summary>
    /// Keys a step needs held ("hold"): their up/down state as the hook sees it. GetAsyncKeyState from this
    /// background process proved unreliable for that; the hook always sees the real presses. Only these keys
    /// are tracked, only while such a step is current, and nothing about them is queued or logged.
    /// </summary>
    public static volatile uint[] HoldWatched = Array.Empty<uint>();
    private static readonly bool[] _down = new bool[256];

    /// <summary>Is this key down? Generic Shift/Ctrl/Alt/Win also count their left and right keys.</summary>
    public static bool IsHeld(uint vk) => vk switch
    {
        0x10 => _down[0x10] || _down[0xA0] || _down[0xA1],
        0x11 => _down[0x11] || _down[0xA2] || _down[0xA3],
        0x12 => _down[0x12] || _down[0xA4] || _down[0xA5],
        0x5B => _down[0x5B] || _down[0x5C],
        _ => vk < 256 && _down[vk],
    };

    private static bool Watched(uint vk)
    {
        foreach (var w in HoldWatched)
        {
            if (w == vk) return true;
            if (w == 0x10 && vk is 0xA0 or 0xA1) return true;
            if (w == 0x11 && vk is 0xA2 or 0xA3) return true;
            if (w == 0x12 && vk is 0xA4 or 0xA5) return true;
            if (w == 0x5B && vk == 0x5C) return true;
        }
        return false;
    }

    private static unsafe IntPtr KeyCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        _lastKeyTick = Environment.TickCount;
        if (nCode >= 0 && HoldWatched.Length > 0)
        {
            int m = (int)wParam;
            uint hv = ((KBDLLHOOKSTRUCT*)lParam)->vkCode;
            if (hv < 256 && Watched(hv)) _down[hv] = m is WM_KEYDOWN or WM_SYSKEYDOWN;
        }
        else if (HoldWatched.Length == 0) Array.Clear(_down);
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
        NoteCallback(started);
        return Native.CallNextHookEx(_keyHook, nCode, wParam, lParam);
    }
}
