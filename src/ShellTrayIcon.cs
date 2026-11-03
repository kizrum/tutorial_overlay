using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

namespace TutorialOverlay;

/// <summary>
/// A notification-area icon identified by a fixed GUID instead of (window, id), as WinForms' NotifyIcon does.
///
/// Why: an icon is removed by the process that added it. A process that is killed (taskkill /F, Task Manager, a
/// tool stopping its background job) never does, and Windows leaves a dead icon in the tray until the mouse passes
/// over it. An icon with a GUID can be removed by any process of the same exe: the next server removes its
/// predecessor's dead icon before adding its own, and the tray guard (--tray-guard) removes it the moment the
/// server dies. The GUID is derived from the exe's path, because Windows binds a GUID icon to one exe path.
///
/// Also re-adds the icon when Explorer restarts (the "TaskbarCreated" broadcast), which NotifyIcon does too.
/// </summary>
internal sealed class ShellTrayIcon : NativeWindow, IDisposable
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(int msg, ref NOTIFYICONDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);

    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const int NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_GUID = 0x20;
    private const int WM_APP_TRAY = 0x8000 + 41;
    private const int WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205;
    private static readonly int WM_TASKBARCREATED = RegisterWindowMessage("TaskbarCreated");

    /// <summary>This exe's icon GUID (the same for every process started from the same path).</summary>
    public static Guid IconGuid(string? exePath = null)
    {
        var path = Path.GetFullPath(exePath ?? Environment.ProcessPath ?? "TutorialOverlay").ToLowerInvariant();
        var h = SHA256.HashData(Encoding.UTF8.GetBytes("TutorialOverlay tray icon|" + path));
        h[7] = (byte)((h[7] & 0x0F) | 0x50);   // shaped like a name-based GUID
        h[8] = (byte)((h[8] & 0x3F) | 0x80);
        return new Guid(h.AsSpan(0, 16));
    }

    /// <summary>
    /// Removes this exe's icon whoever added it — the dead icon of a killed server. Returns whether there was one.
    /// Works from any process started from the same exe path (the tray guard, the next server).
    /// </summary>
    public static bool RemoveStale()
    {
        var d = new NOTIFYICONDATA { cbSize = Marshal.SizeOf<NOTIFYICONDATA>(), uFlags = NIF_GUID, guidItem = IconGuid(), szTip = "", szInfo = "", szInfoTitle = "" };
        return Shell_NotifyIcon(NIM_DELETE, ref d);
    }

    private readonly Guid _guid = IconGuid();
    private bool _useGuid = true;
    private bool _added;
    private IntPtr _hicon;
    private string _tip = "";

    public event Action? LeftClick, RightClick;

    public ShellTrayIcon()
    {
        CreateHandle(new CreateParams());
        if (RemoveStale()) Log.Info("removed the tray icon a killed TutorialOverlay had left behind");
    }

    private NOTIFYICONDATA Data(int flags)
    {
        var d = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = Handle, uID = 1, uFlags = flags | NIF_MESSAGE, uCallbackMessage = WM_APP_TRAY,
            hIcon = _hicon, szTip = _tip, szInfo = "", szInfoTitle = "",
        };
        if (_useGuid) { d.uFlags |= NIF_GUID; d.guidItem = _guid; }
        return d;
    }

    public void Set(IntPtr hicon, string tip)
    {
        _hicon = hicon;
        _tip = tip.Length > 127 ? tip[..126] + "…" : tip;
        Apply();
    }

    private void Apply()
    {
        if (Handle == IntPtr.Zero) return;
        var d = Data(NIF_ICON | NIF_TIP);
        if (_added && Shell_NotifyIcon(NIM_MODIFY, ref d)) return;
        if (Shell_NotifyIcon(NIM_ADD, ref d)) { _added = true; return; }
        if (_useGuid)
        {
            // the GUID is bound to another path of this exe (it was moved/copied): an ordinary icon still works
            Log.Warn("tray icon with a GUID refused; using a plain one (a killed process may then leave a dead icon)");
            _useGuid = false;
            d = Data(NIF_ICON | NIF_TIP);
            _added = Shell_NotifyIcon(NIM_ADD, ref d);
        }
    }

    /// <summary>Takes the icon out of the tray now.</summary>
    public void Remove()
    {
        if (!_added || Handle == IntPtr.Zero) return;
        var d = Data(0);
        Shell_NotifyIcon(NIM_DELETE, ref d);
        _added = false;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_APP_TRAY)
        {
            int ev = (int)m.LParam & 0xFFFF;
            if (ev == WM_LBUTTONUP) LeftClick?.Invoke();
            else if (ev == WM_RBUTTONUP)
            {
                // the menu closes when the user clicks elsewhere only if our window is in the foreground
                SetForegroundWindow(Handle);
                RightClick?.Invoke();
            }
            return;
        }
        if (m.Msg == WM_TASKBARCREATED && WM_TASKBARCREATED != 0)
        {
            // Explorer restarted: the tray is new and empty
            _added = false;
            Apply();
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        Remove();
        DestroyHandle();
    }
}
