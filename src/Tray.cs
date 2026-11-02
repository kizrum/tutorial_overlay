using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace TutorialOverlay;

/// <summary>
/// Notification-area icon: shows that the server is alive (and whether the agent talks to it),
/// opens the settings window and quits the process. The icon is drawn in code like the rest of the UI.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _running, _agent, _tutorial, _library, _settings, _copyKey, _copyUrl, _copyCommand, _folder, _stop, _exit;
    private readonly System.Windows.Forms.Timer _timer;
    private IntPtr _hicon;
    private string _iconSig = "";

    /// <summary>The agent counts as connected while its last request is at most this old.</summary>
    private static readonly TimeSpan Recent = TimeSpan.FromMinutes(5);

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);

    public TrayIcon()
    {
        _menu = new ContextMenuStrip { ShowImageMargin = false, Renderer = new DarkMenuRenderer(), BackColor = Palette.Ink, ForeColor = Palette.Text };
        _running = Info();
        _agent = Info();
        _tutorial = Info();
        _library = new ToolStripMenuItem("");
        _settings = new ToolStripMenuItem("", null, (_, _) => SettingsWindow.Open()) { Font = new Font(_menu.Font, FontStyle.Bold) };
        _copyKey = new ToolStripMenuItem("", null, (_, _) => Copy(Program.Key));
        _copyUrl = new ToolStripMenuItem("", null, (_, _) => Copy($"http://127.0.0.1:{Program.Port}/mcp"));
        _copyCommand = new ToolStripMenuItem("", null, (_, _) => Copy(Ports.ConnectCommand(Program.Port, Program.Key)));
        _folder = new ToolStripMenuItem("", null, (_, _) => OpenFolder(Path.GetDirectoryName(Settings.FilePath)!));
        _stop = new ToolStripMenuItem("", null, (_, _) => Engine.UserAction("stop", "tray"));
        _exit = new ToolStripMenuItem("", null, (_, _) => Quit());
        _menu.Items.AddRange(new ToolStripItem[]
        {
            _running, _agent, _tutorial, new ToolStripSeparator(),
            _library, _settings, _copyCommand, _copyKey, _copyUrl, _folder, new ToolStripSeparator(),
            _stop, _exit,
        });
        _menu.Opening += (_, _) => { Refresh(); FillLibrary(); };

        _icon = new NotifyIcon { ContextMenuStrip = _menu, Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) SettingsWindow.Open(); };
        Refresh();

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
    }

    private static ToolStripMenuItem Info() => new() { Enabled = false };

    /// <summary>Saved tutorials: a click replays one — no agent needed.</summary>
    private void FillLibrary()
    {
        var t = Settings.Global.Text;
        _library.Text = t.TrayTutorials;
        _library.DropDownItems.Clear();
        _library.DropDown.Renderer = _menu.Renderer;
        _library.DropDown.BackColor = Palette.Ink;
        if (_library.DropDown is ToolStripDropDownMenu dd) dd.ShowImageMargin = false;
        var list = Tutorials.List();
        foreach (var o in list.Take(25))
        {
            var name = J.Str(o, "name")!;
            var label = $"{J.Str(o, "title")}  ·  {J.Int(o, "steps")}";
            _library.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) => StartSaved(name)));
        }
        if (list.Count == 0) _library.DropDownItems.Add(new ToolStripMenuItem(t.TrayTutorialsEmpty) { Enabled = false });
        _library.DropDownItems.Add(new ToolStripSeparator());
        _library.DropDownItems.Add(new ToolStripMenuItem(t.TrayTutorialsFolder, null, (_, _) => OpenFolder(Tutorials.Dir)));
    }

    private static void StartSaved(string name)
    {
        try { Engine.StartTutorial(new JsonObject { ["name"] = name }); }
        catch (Exception e) { MessageBox.Show(e.Message, "TutorialOverlay", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private static TimeSpan? SinceAgent()
    {
        long t = Interlocked.Read(ref McpServer.LastRequestTicks);
        return t == 0 ? null : DateTime.UtcNow - new DateTime(t, DateTimeKind.Utc);
    }

    private static string Ago(TimeSpan d, Loc t) =>
        d.TotalSeconds < 60 ? $"{(int)d.TotalSeconds} {t.Sec}" :
        d.TotalMinutes < 60 ? $"{(int)d.TotalMinutes} {t.Min}" : $"{(int)d.TotalHours} {t.Hour}";

    private void Refresh()
    {
        var t = Settings.Global.Text;
        var since = SinceAgent();
        var tut = Engine.Tutorial;
        bool active = tut is { Active: true };

        _running.Text = string.Format(t.TrayRunning, Program.Port);
        _agent.Text = since is TimeSpan d ? string.Format(t.TrayAgentAgo, Ago(d, t)) : t.TrayAgentNever;
        _tutorial.Text = active ? string.Format(t.TrayTutorial, tut!.Title, tut.Index + 1, tut.Steps.Count) : t.TrayNoTutorial;
        _settings.Text = t.TraySettings;
        _copyKey.Text = t.TrayCopyKey;
        _copyUrl.Text = t.TrayCopyUrl;
        _copyCommand.Text = t.TrayCopyCommand;
        _folder.Text = t.TrayOpenFolder;
        _stop.Text = t.TrayStopTutorial;
        _stop.Enabled = active;
        _exit.Text = t.TrayExit;

        // the tooltip is limited to 127 characters
        var tip = $"TutorialOverlay · :{Program.Port}\n{_agent.Text}\n{_tutorial.Text}";
        _icon.Text = tip.Length > 127 ? tip[..126] + "…" : tip;

        bool connected = since is TimeSpan s && s < Recent;
        float? progress = active && tut!.Steps.Count > 0 ? (tut.Index + 1f) / tut.Steps.Count : null;
        var sig = $"{connected}|{progress:0.00}|{Palette.Accent.ToArgb()}";
        if (sig != _iconSig)
        {
            _iconSig = sig;
            SetIcon(connected, progress);
        }
    }

    /// <summary>
    /// A small step card — progress bar, a tick and two text lines — like the card the user follows,
    /// plus a status dot: green = agent connected, grey = waiting. During a tutorial the bar shows its progress.
    /// </summary>
    private void SetIcon(bool connected, float? progress)
    {
        const int n = 32;
        using var bmp = new Bitmap(n, n);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            var card = new RectangleF(2, 3, 27, 25);
            using (var path = Theme.Rounded(card, 5))
            {
                using (var bg = new SolidBrush(Palette.Ink)) g.FillPath(bg, path);
                using var edge = new Pen(Palette.Line, 1.4f);
                g.DrawPath(edge, path);
            }
            // progress bar along the top, like the card's step progress
            float bx = 6, by = 7, bw = 19, bh = 3.2f;
            using (var track = new SolidBrush(Palette.InkHover)) g.FillRectangle(track, bx, by, bw, bh);
            using (var fill = new SolidBrush(Palette.Accent)) g.FillRectangle(fill, bx, by, bw * Math.Clamp(progress ?? 0.6f, 0.15f, 1f), bh);
            Theme.IconCheck(g, new PointF(10, 16.5f), 8, Palette.Accent, 2.4f);
            using (var line = new Pen(Palette.TextDim, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(line, 16, 16, 24, 16);
                g.DrawLine(line, 7, 22.5f, 18, 22.5f);
            }
            var status = connected ? Palette.Success : Palette.TextDim;
            using (var ring = new SolidBrush(Color.FromArgb(255, 10, 11, 15))) g.FillEllipse(ring, 19.5f, 19.5f, 12.5f, 12.5f);
            using (var dot = new SolidBrush(status)) g.FillEllipse(dot, 21.5f, 21.5f, 8.5f, 8.5f);
        }
        var h = bmp.GetHicon();
        var old = _hicon;
        _icon.Icon = Icon.FromHandle(h);
        _hicon = h;
        if (old != IntPtr.Zero) DestroyIcon(old);
    }

    private static void Copy(string text)
    {
        try { Clipboard.SetText(text); }
        catch (Exception e) { Log.Warn("clipboard: " + e.Message); }
    }

    private static void OpenFolder(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception e) { Log.Warn("open folder: " + e.Message); }
    }

    private static void Quit()
    {
        var t = Settings.Global.Text;
        if (Engine.Tutorial is { Active: true } &&
            MessageBox.Show(t.TrayExitConfirm, "TutorialOverlay", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        Application.Exit();
    }

    public void Dispose()
    {
        _timer.Dispose();
        _icon.Visible = false;     // otherwise a dead icon lingers until the mouse passes over it
        _icon.Dispose();
        _menu.Dispose();
        if (_hicon != IntPtr.Zero) DestroyIcon(_hicon);
    }
}
