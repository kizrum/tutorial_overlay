using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace TutorialOverlay;

/// <summary>The user (or another program) took the mouse or keyboard: the autoplay let go of everything and stopped.</summary>
internal sealed class TakenOverException : Exception
{
    public TakenOverException() : base("taken over") { }
}

/// <summary>Windows refused the input (the lock screen, a UAC prompt, a window above our rights).</summary>
internal sealed class InputBlockedException : Exception
{
    public InputBlockedException(string why) : base(why) { }
}

/// <summary>
/// The autoplay's hands: SendInput moves, clicks, keys, wheel turns and drags that look like a calm person's — the
/// pointer travels (hover effects, menus that open on hover and the mascot all see it), it rests before pressing.
/// Every input carries <see cref="InputHook.AutoplayTag"/>, so the hook tells it from the user's. Between every
/// small move it checks <see cref="Stop"/>: someone else's input there lets go of whatever it holds (a mouse button
/// mid-drag, Shift of a "hold") and throws <see cref="TakenOverException"/> — the user always wins.
/// All timings are a calm person's divided by <see cref="Speed"/>, with floors apps need to notice the input.
/// </summary>
internal static class InputSend
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }

    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint type);

    private const uint MOUSE = 0, KEYBOARD = 1;
    private const uint MOVE = 0x0001, ABSOLUTE = 0x8000, VIRTUALDESK = 0x4000, WHEEL = 0x0800, HWHEEL = 0x1000;
    private const uint KEYUP = 0x02, EXTENDED = 0x01, UNICODE = 0x04;

    /// <summary>A factor on every timing (settings: the autoplay's speed). 1 = a calm person.</summary>
    public static volatile float Speed = 1f;

    /// <summary>Checked between every small move: true = someone else has taken the input — let go and stop.</summary>
    public static Func<bool> Stop = () => false;

    // what it holds down right now: let go of on a takeover, whatever happens
    private static readonly HashSet<uint> HeldKeys = new();
    private static readonly HashSet<string> HeldButtons = new();
    private static readonly object HeldLock = new();

    /// <summary>The pointer when the last press went down (physical pixels): what the log and the events say.</summary>
    public static Point LastPress;
    /// <summary>When the last press went down (TickCount64): the app may answer it before the caller logs it.</summary>
    public static long LastPressAt;

    internal static int Ms(double baseMs, int floor) => Math.Max(floor, (int)Math.Round(baseMs / Math.Clamp(Speed, 0.25f, 4f)));

    private static (uint down, uint up) ButtonFlags(string b) => b switch
    {
        "right" => (0x08, 0x10),
        "middle" => (0x20, 0x40),
        _ => (0x02, 0x04),
    };

    /// <summary>Keys sharing a code with the number pad go as "extended", or Delete arrives as the pad's ".".</summary>
    private static bool Extended(uint vk) => vk is >= 0x21 and <= 0x28 or 0x2D or 0x2E or 0x5B or 0x5C or 0x6F or 0x90 or 0xA3 or 0xA5;

    private static void Send(params INPUT[] i)
    {
        uint sent = SendInput((uint)i.Length, i, Marshal.SizeOf<INPUT>());
        if (sent != i.Length) throw new InputBlockedException($"SendInput sent {sent} of {i.Length} (error {Marshal.GetLastWin32Error()})");
    }

    private static INPUT M(uint flags, uint data = 0, int dx = 0, int dy = 0) => new()
    {
        type = MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = flags, mouseData = data, dwExtraInfo = (IntPtr)InputHook.AutoplayTag } },
    };

    private static INPUT K(uint vk, bool up) => new()
    {
        type = KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = (ushort)vk, wScan = (ushort)MapVirtualKey(vk, 0), dwFlags = (up ? KEYUP : 0) | (Extended(vk) ? EXTENDED : 0), dwExtraInfo = (IntPtr)InputHook.AutoplayTag } },
    };

    private static INPUT U(char ch, bool up) => new()
    {
        type = KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = UNICODE | (up ? KEYUP : 0), dwExtraInfo = (IntPtr)InputHook.AutoplayTag } },
    };

    /// <summary>Waits, checking every few ms whether someone else took over.</summary>
    public static void Wait(int ms)
    {
        var until = Environment.TickCount64 + ms;
        while (true)
        {
            Check();
            long left = until - Environment.TickCount64;
            if (left <= 0) return;
            Thread.Sleep((int)Math.Min(10, left));
        }
    }

    private static void Check()
    {
        if (!Stop()) return;
        ReleaseAll();
        throw new TakenOverException();
    }

    /// <summary>Lets go of every button and key it holds (a takeover, an error): nothing stays pressed after it.</summary>
    public static void ReleaseAll()
    {
        lock (HeldLock)
        {
            foreach (var b in HeldButtons.ToList())
                try { SendInput(1, new[] { M(ButtonFlags(b).up) }, Marshal.SizeOf<INPUT>()); } catch { }
            foreach (var k in HeldKeys.ToList())
                try { SendInput(1, new[] { K(k, true) }, Marshal.SizeOf<INPUT>()); } catch { }
            HeldButtons.Clear();
            HeldKeys.Clear();
        }
    }

    private static void KeyDown(uint vk) { lock (HeldLock) HeldKeys.Add(vk); Send(K(vk, false)); }
    private static void KeyUp(uint vk) { Send(K(vk, true)); lock (HeldLock) HeldKeys.Remove(vk); }
    private static void ButtonDown(string b) { lock (HeldLock) HeldButtons.Add(b); Send(M(ButtonFlags(b).down)); }
    private static void ButtonUp(string b) { Send(M(ButtonFlags(b).up)); lock (HeldLock) HeldButtons.Remove(b); }

    /// <summary>A real move (absolute on the virtual desktop): the hooks see it, as they see a hand's.</summary>
    private static void MoveRaw(int x, int y)
    {
        int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
        int ax = (int)Math.Round((x - vx) * 65535.0 / Math.Max(1, vw - 1));
        int ay = (int)Math.Round((y - vy) * 65535.0 / Math.Max(1, vh - 1));
        Send(M(MOVE | ABSOLUTE | VIRTUALDESK, 0, ax, ay));
    }

    /// <summary>How long a move takes: about 900 px/s at speed 1, eased, never shorter than a person could follow.</summary>
    internal static int MoveMs(double dist) => Ms(Math.Clamp(dist / 0.9, 350, 1200), 150);

    /// <summary>Moves the pointer there like a hand: eased in and out, a sample every 10 ms.</summary>
    public static void MoveTo(PointF to)
    {
        Native.GetCursorPos(out var from);
        double dist = Math.Sqrt((to.X - from.X) * (to.X - from.X) + (to.Y - from.Y) * (to.Y - from.Y));
        if (dist < 1) { Check(); return; }
        int ms = MoveMs(dist), n = Math.Max(2, ms / 10);
        for (int i = 1; i <= n; i++)
        {
            double q = i / (double)n, e = q < 0.5 ? 2 * q * q : 1 - Math.Pow(-2 * q + 2, 2) / 2;
            MoveRaw((int)Math.Round(from.X + (to.X - from.X) * e), (int)Math.Round(from.Y + (to.Y - from.Y) * e));
            Wait(10);
        }
    }

    public enum Way { Straight, DownThenAcross, AcrossThenDown }

    /// <summary>Moves there straight or in two legs: out of a menu bar down first, into a submenu across first.</summary>
    public static void Hover(PointF to, Way way = Way.Straight)
    {
        Native.GetCursorPos(out var from);
        if (way == Way.DownThenAcross) MoveTo(new PointF(from.X, to.Y));
        else if (way == Way.AcrossThenDown) MoveTo(new PointF(to.X, from.Y));
        MoveTo(to);
    }

    /// <summary>Through the points without pressing, densely (every few pixels), as a hand traces a line.</summary>
    public static void Follow(IReadOnlyList<PointF> pts)
    {
        if (pts.Count == 0) return;
        MoveTo(pts[0]);
        Trace(pts);
    }

    private static void Trace(IReadOnlyList<PointF> pts)
    {
        var p = pts[0];
        foreach (var q in pts.Skip(1))
        {
            double len = Math.Sqrt((q.X - p.X) * (q.X - p.X) + (q.Y - p.Y) * (q.Y - p.Y));
            int n = Math.Max(1, (int)(len / 6));
            for (int i = 1; i <= n; i++)
            {
                MoveRaw((int)Math.Round(p.X + (q.X - p.X) * i / n), (int)Math.Round(p.Y + (q.Y - p.Y) * i / n));
                Wait(Ms(8, 4));
            }
            p = q;
        }
    }

    /// <summary>The rest before a press, once the hand is there (hover effects show; the user sees where).</summary>
    public static void Dwell() => Wait(Ms(300, 120));

    /// <summary>A click where the pointer is now (it has already moved and rested there).</summary>
    public static void PressHere(string button, int times = 1)
    {
        for (int i = 0; i < times; i++)
        {
            Check();
            Native.GetCursorPos(out var at);
            LastPress = new Point(at.X, at.Y);
            LastPressAt = Environment.TickCount64;
            ButtonDown(button);
            Thread.Sleep(60);
            ButtonUp(button);
            if (i + 1 < times) Wait(Ms(250, 120));
        }
    }

    /// <summary>A chord: the modifiers down in order, the key, then all up in reverse.</summary>
    public static void Chord(uint mods, uint vk)
    {
        var down = new List<uint>();
        if ((mods & Native.MOD_CONTROL) != 0) down.Add(0x11);
        if ((mods & Native.MOD_ALT) != 0) down.Add(0x12);
        if ((mods & Native.MOD_SHIFT) != 0) down.Add(0x10);
        if ((mods & Native.MOD_WIN) != 0) down.Add(0x5B);
        try
        {
            foreach (var k in down) { KeyDown(k); Wait(Ms(30, 20)); }
            KeyDown(vk);
            Thread.Sleep(60);
            KeyUp(vk);
            foreach (var k in Enumerable.Reverse(down)) { Thread.Sleep(30); KeyUp(k); }
        }
        catch { ReleaseAll(); throw; }
    }

    /// <summary>Wheel notches over where the pointer is, keys of a "hold" held meanwhile; sideways = Shift + wheel.</summary>
    public static void Wheel(int notches, bool up, List<uint> hold)
    {
        try
        {
            foreach (var k in hold) { KeyDown(k); Wait(Ms(40, 20)); }
            for (int i = 0; i < notches; i++)
            {
                Send(M(WHEEL, unchecked((uint)((up ? 1 : -1) * 120))));
                Wait(Ms(90, 50));
            }
            foreach (var k in Enumerable.Reverse(hold)) { Wait(Ms(40, 20)); KeyUp(k); }
        }
        catch { ReleaseAll(); throw; }
    }

    /// <summary>Presses at the first point, follows the rest with the button (and the hold keys) down, lets go at the last.</summary>
    public static void Drag(IReadOnlyList<PointF> pts, string button, List<uint> hold)
    {
        if (pts.Count == 0) return;
        MoveTo(pts[0]);
        Dwell();
        try
        {
            foreach (var k in hold) { KeyDown(k); Wait(Ms(40, 20)); }
            Native.GetCursorPos(out var at);
            LastPress = new Point(at.X, at.Y);
            LastPressAt = Environment.TickCount64;
            ButtonDown(button);
            Wait(Ms(120, 80));        // a press, then motion: apps tell a drag from a click by it
            Trace(pts);
            Wait(Ms(120, 80));
            ButtonUp(button);
            foreach (var k in Enumerable.Reverse(hold)) { Wait(Ms(40, 20)); KeyUp(k); }
        }
        catch { ReleaseAll(); throw; }
    }

    /// <summary>Types text as Unicode characters (no layout or IME involved).</summary>
    public static void Type(string text)
    {
        foreach (var ch in text)
        {
            Check();
            Send(U(ch, false), U(ch, true));
            Wait(Ms(35, 12));
        }
    }
}
