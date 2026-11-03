using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace TutorialOverlay;

/// <summary>Win32 interop. Everything here works in physical pixels (process is Per-Monitor-V2 aware).</summary>
internal static class Native
{
    public const int WS_POPUP = unchecked((int)0x80000000);
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TOPMOST = 0x00000008;

    public const int WH_MOUSE_LL = 14;
    public const int WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204, WM_MBUTTONDOWN = 0x0207;
    public const int WM_MOUSEACTIVATE = 0x0021, MA_NOACTIVATE = 3;
    public const int WM_HOTKEY = 0x0312;
    public const int WM_QUIT = 0x0012;
    public const int LLMHF_INJECTED = 0x01;

    public const uint GA_ROOT = 2;
    public const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    public static readonly IntPtr HWND_TOPMOST = new(-1);

    public const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    public const int ULW_ALPHA = 2;
    public const byte AC_SRC_OVER = 0, AC_SRC_ALPHA = 1;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    public const int SRCCOPY = 0x00CC0020;
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14, DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;
    public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; public SIZE(int w, int h) { cx = w; cy = h; } }
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight; public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    public delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);
    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT pt);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hwnd, ref POINT p);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc fn, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int idx);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr hmon, ref MONITORINFO mi);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr hmon, int type, out uint dpiX, out uint dpiY);

    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);

    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    // ---- helpers ----

    public static string WindowTitle(IntPtr h)
    {
        int n = GetWindowTextLength(h);
        if (n <= 0) return "";
        var sb = new StringBuilder(n + 1);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string ClassName(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static int ProcessId(IntPtr h) { GetWindowThreadProcessId(h, out int pid); return pid; }

    public static bool IsCloaked(IntPtr h) =>
        DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int c, 4) == 0 && c != 0;

    /// <summary>Visible frame (without the invisible resize borders of Win10/11).</summary>
    public static Rectangle FrameRect(IntPtr h)
    {
        if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) == 0 && r.Right > r.Left)
            return r.ToRectangle();
        GetWindowRect(h, out r);
        return r.ToRectangle();
    }

    public static Rectangle ClientScreenRect(IntPtr h)
    {
        GetClientRect(h, out RECT r);
        var p = new POINT(0, 0);
        ClientToScreen(h, ref p);
        return new Rectangle(p.X, p.Y, r.Right - r.Left, r.Bottom - r.Top);
    }

    public static Rectangle VirtualScreen() => new(
        GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
        GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));

    public static IntPtr MonitorAt(PointF p) => MonitorFromPoint(new POINT((int)p.X, (int)p.Y), MONITOR_DEFAULTTONEAREST);

    public static float ScaleAt(PointF p)
    {
        try
        {
            if (GetDpiForMonitor(MonitorAt(p), 0, out uint dx, out _) == 0 && dx > 0) return dx / 96f;
        }
        catch { }
        return 1f;
    }

    public static Rectangle WorkAreaAt(PointF p)
    {
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorAt(p), ref mi);
        return mi.rcWork.ToRectangle();
    }

    public static Rectangle MonitorRectAt(PointF p)
    {
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorAt(p), ref mi);
        return mi.rcMonitor.ToRectangle();
    }

    /// <summary>Process id of the top-level window under a screen point (and of the child window itself).</summary>
    public static (IntPtr root, int rootPid, int childPid) HitWindow(PointF p)
    {
        var h = WindowFromPoint(new POINT((int)Math.Round(p.X), (int)Math.Round(p.Y)));
        if (h == IntPtr.Zero) return (IntPtr.Zero, 0, 0);
        var root = GetAncestor(h, GA_ROOT);
        if (root == IntPtr.Zero) root = h;
        return (root, ProcessId(root), ProcessId(h));
    }

    // ---- power throttling: Windows 11 slows background threads ("efficiency mode", EcoQoS); a low-level hook that
    // answers late is removed without notice, so the hook thread opts out (it sleeps in GetMessage: it costs nothing).

    [StructLayout(LayoutKind.Sequential)]
    private struct POWER_THROTTLING_STATE { public uint Version, ControlMask, StateMask; }

    [DllImport("kernel32.dll")] private static extern bool SetThreadInformation(IntPtr thread, int cls, ref POWER_THROTTLING_STATE info, int size);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();

    private const int ThreadPowerThrottling = 3;
    private const uint THROTTLING_EXECUTION_SPEED = 0x1;

    /// <summary>Opts the calling thread out of power throttling.</summary>
    public static void NoPowerThrottlingForThisThread()
    {
        var s = new POWER_THROTTLING_STATE { Version = 1, ControlMask = THROTTLING_EXECUTION_SPEED, StateMask = 0 };
        if (!SetThreadInformation(GetCurrentThread(), ThreadPowerThrottling, ref s, Marshal.SizeOf<POWER_THROTTLING_STATE>()))
            Log.Debug("power throttling opt-out (thread) not applied: " + Marshal.GetLastWin32Error());
    }

    // ---- integrity levels: UIPI keeps a process from hooking or automating windows of a higher one

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr info, int len, out int needed);
    [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, TOKEN_QUERY = 0x0008;
    private const int TokenIntegrityLevel = 25, ERROR_ACCESS_DENIED = 5;

    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr h, uint ms);
    private const uint SYNCHRONIZE = 0x00100000, WAIT_TIMEOUT = 0x102;

    /// <summary>The process is still running (two cheap system calls, no process list).</summary>
    public static bool ProcessAlive(int pid)
    {
        var h = OpenProcess(SYNCHRONIZE, false, pid);
        if (h == IntPtr.Zero) return Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED;   // exists, just not ours to open
        try { return WaitForSingleObject(h, 0) == WAIT_TIMEOUT; }
        finally { CloseHandle(h); }
    }

    private static int? _ownLevel;
    private static readonly Dictionary<int, (bool above, long at)> AboveCache = new();

    /// <summary>Integrity level RID of a process (0x2000 medium, 0x3000 high = "as administrator"); -1 = may not look, null = unknown.</summary>
    private static int? IntegrityLevel(IntPtr process)
    {
        if (!OpenProcessToken(process, TOKEN_QUERY, out var token))
            return Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED ? -1 : null;
        try
        {
            GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out int len);
            if (len <= 0) return null;
            var buf = Marshal.AllocHGlobal(len);
            try
            {
                if (!GetTokenInformation(token, TokenIntegrityLevel, buf, len, out _)) return null;
                var sid = Marshal.ReadIntPtr(buf);   // TOKEN_MANDATORY_LABEL.Label.Sid
                int count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                return count == 0 ? null : Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { CloseHandle(token); }
    }

    /// <summary>
    /// The process runs at a higher integrity level than we do (typically "Run as administrator" while we do not):
    /// our input hooks see none of its input and UI Automation sees little of it. Cached for 10 s per PID.
    /// </summary>
    public static bool IsAboveUs(int pid)
    {
        if (pid <= 0 || pid == Environment.ProcessId) return false;
        long now = Environment.TickCount64;
        lock (AboveCache)
            if (AboveCache.TryGetValue(pid, out var c) && now - c.at < 10000) return c.above;
        _ownLevel ??= IntegrityLevel(GetCurrentProcess()) ?? 0x2000;
        bool above = false;
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) above = Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED && _ownLevel < 0x3000;
        else
        {
            try
            {
                var lvl = IntegrityLevel(h);
                above = lvl == -1 ? _ownLevel < 0x3000 : lvl is int l && l > _ownLevel;
            }
            finally { CloseHandle(h); }
        }
        lock (AboveCache)
        {
            if (AboveCache.Count > 256) AboveCache.Clear();
            AboveCache[pid] = (above, now);
        }
        return above;
    }
}
