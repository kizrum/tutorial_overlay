using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace TutorialOverlay;

/// <summary>
/// Global defaults (config.json) edited by hand: server, look, hotkeys. Opened from the tray icon.
/// Saving rewrites only the fields shown here; anything else in config.json is kept.
/// Drawn in the card's dark style (see DarkControls).
/// </summary>
internal sealed class SettingsWindow : Form
{
    private static SettingsWindow? _open;

    private readonly Loc _t = Settings.Global.Text;
    private readonly float _s;
    private readonly int _pageW;
    private readonly DarkField _key;
    private DarkField _port = null!;          // built by ServerPage
    private Label _portStatus = null!;
    private readonly DarkButton _ru, _en;
    private readonly List<DarkSwatch> _swatches = new();
    private readonly DarkToggle _autoColor, _animate, _hideInactive, _hideCapture;
    private readonly Dictionary<string, DarkField> _keyBoxes = new();
    private readonly Dictionary<string, Chord?> _chords = new();
    private readonly List<(DarkButton tab, Panel page)> _tabs = new();
    private readonly Panel _pages;
    private Color _accent;
    private bool _english;

    /// <param name="tab">server | look | keys</param>
    public static void Open(string tab = "server")
    {
        if (_open == null)
        {
            _open = new SettingsWindow();
            _open.FormClosed += (_, _) => _open = null;
            _open.Show();
        }
        if (_open.WindowState == FormWindowState.Minimized) _open.WindowState = FormWindowState.Normal;
        _open.Select(_open._tabs[tab switch { "look" => 1, "keys" or "hotkeys" => 2, _ => 0 }].tab);
        _open.Activate();
    }

    private SettingsWindow()
    {
        Native.GetCursorPos(out var cp);
        var at = new PointF(cp.X, cp.Y);
        float s = _s = Native.ScaleAt(at);
        AutoScaleMode = AutoScaleMode.None;
        Text = _t.SetTitle;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Palette.Ink;
        ForeColor = Palette.Text;
        DoubleBuffered = true;
        KeyPreview = true;

        var g = Settings.Global;
        _accent = g.Accent;
        _english = g.Lang.StartsWith("en", StringComparison.OrdinalIgnoreCase);
        int pad = (int)(24 * s), inner = (int)(20 * s), W = (int)(640 * s);
        _pageW = W - 2 * pad - 2 * inner;

        // ---- header: title + where the server runs (the window caption already names the app)
        var title = Label(_t.TraySettings.TrimEnd('…', '.'), 19, true, Palette.Text);
        title.Location = new Point(pad, (int)(18 * s));
        var sub = Label($"TutorialOverlay {Program.Version} · 127.0.0.1:{Program.Port} · PID {Environment.ProcessId}", 12, false, Palette.TextDim);
        sub.Location = new Point(pad, title.Bottom + (int)(4 * s));
        Controls.Add(title);
        Controls.Add(sub);

        // ---- tabs: a segmented switch, pages in an outlined box below
        int tx = pad, ty = sub.Bottom + (int)(18 * s);
        var box = new DarkBox(s) { Location = new Point(pad, ty + (int)(48 * s)) };
        _pages = new Panel { Location = new Point(inner, inner), BackColor = DarkBox.Surface };
        box.Controls.Add(_pages);
        foreach (var (name, build) in new (string, Func<Panel>)[] { (_t.SetTabServer, ServerPage), (_t.SetTabLook, LookPage), (_t.SetTabKeys, KeysPage) })
        {
            var tab = new DarkButton(name, "secondary", s) { Location = new Point(tx, ty) };
            var page = build();
            page.Visible = false;
            _pages.Controls.Add(page);
            tab.Click += (_, _) => Select(tab);
            _tabs.Add((tab, page));
            Controls.Add(tab);
            tx = tab.Right + (int)(8 * s);
        }
        int pageH = _tabs.Max(p => p.page.Height);
        _pages.Size = new Size(_pageW, pageH);
        box.Size = new Size(_pageW + 2 * inner, pageH + 2 * inner - (int)(12 * s));
        Controls.Add(box);

        // ---- footer
        int fy = box.Bottom + (int)(20 * s);
        var line = new Panel { BackColor = Palette.Line, Location = new Point(0, fy), Size = new Size(W, 1) };
        Controls.Add(line);
        fy += (int)(16 * s);
        var save = new DarkButton(_t.SetSave, "primary", s);
        save.Location = new Point(W - pad - save.Width, fy);
        save.Click += (_, _) => Save();
        var cancel = new DarkButton(_t.Cancel, "secondary", s);
        cancel.Location = new Point(save.Left - (int)(10 * s) - cancel.Width, fy);
        cancel.Click += (_, _) => Close();
        var defaults = new DarkButton(_t.SetDefaults, "ghost", s) { Location = new Point(pad, fy) };
        defaults.Click += (_, _) => ResetDefaults();
        Controls.AddRange(new Control[] { save, cancel, defaults });

        ClientSize = new Size(W, save.Bottom + (int)(20 * s));
        var work = Native.WorkAreaAt(at);
        Location = new Point(work.X + (work.Width - Width) / 2, work.Y + Math.Max(0, (work.Height - Height) / 2));
        _key = (DarkField)FindByTag("key");
        _ru = (DarkButton)FindByTag("ru");
        _en = (DarkButton)FindByTag("en");
        _autoColor = (DarkToggle)FindByTag("autoColor");
        _animate = (DarkToggle)FindByTag("animate");
        _hideInactive = (DarkToggle)FindByTag("hideInactive");
        _hideCapture = (DarkToggle)FindByTag("hideCapture");
        Select(_tabs[0].tab);
        ShowLang();
        ShowAccent();
        ShowChords();
        ShowPortStatus();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        DarkWindow.TitleBar(Handle);
    }

    protected override bool ProcessDialogKey(System.Windows.Forms.Keys keyData)
    {
        if (keyData == System.Windows.Forms.Keys.Escape) { Close(); return true; }
        return base.ProcessDialogKey(keyData);
    }

    // ------------------------------------------------------------------ pages

    private Panel ServerPage()
    {
        var g = Settings.Global;
        var (p, add) = Page();
        int avail = _pageW - CaptionW, gap = (int)(8 * _s);
        _port = new DarkField(_s, (int)(96 * _s));
        // the configured default — not this run's --port, which must not leak into config.json on Save
        _port.Box.Text = (g.Port ?? DefaultPort).ToString();
        _port.Box.KeyPress += (_, e) => { if (!char.IsDigit(e.KeyChar) && !char.IsControl(e.KeyChar)) e.Handled = true; };
        _port.Box.TextChanged += (_, _) => ShowPortStatus();
        var pick = Button(_t.SetPortPick, PickPort);
        // live verdict next to the field: free / in use by whom / this server
        _portStatus = new Label
        {
            AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false,
            Size = new Size(avail - _port.Width - pick.Width - 2 * gap, _port.Height),
            Font = Theme.Font(12.5f * _s), BackColor = Color.Transparent, ForeColor = Palette.TextDim,
        };
        add(_t.SetPort, Row(_port, pick, _portStatus));
        if (Program.PortFromArgs) add(null, Label(string.Format(_t.SetPortArg, Program.Port), 12, false, Palette.Warn, avail));
        add(null, Note(_t.SetPortNote, avail));

        // fields take whatever width their row's buttons leave
        var urlText = $"http://127.0.0.1:{Program.Port}/mcp";
        var copyUrl = Button(_t.Copy, () => Copy(urlText));
        var url = new DarkField(_s, avail - copyUrl.Width - gap, readOnly: true);
        url.Box.Text = urlText;
        add(_t.SetUrl, Row(url, copyUrl));

        var copyKey = Button(_t.Copy, () => Copy(Program.Key));
        DarkField? key = null;
        DarkButton? show = null;
        show = Button(_t.Show, () =>
        {
            key!.Box.UseSystemPasswordChar = !key.Box.UseSystemPasswordChar;
            show!.Text = key.Box.UseSystemPasswordChar ? _t.Show : _t.Hide;
        });
        // room for the longer caption ("Hide"), so the row does not jump when it toggles
        int showW = Math.Max(show.Width, (int)Math.Ceiling(Theme.Measure(_t.Hide, Theme.Font(13 * _s, true), 600 * _s).Width + 32 * _s));
        show.Width = showW;
        key = new DarkField(_s, avail - showW - copyKey.Width - 2 * gap, readOnly: true) { Tag = "key" };
        key.Box.Text = Program.Key;
        key.Box.UseSystemPasswordChar = true;
        add(_t.SetKey, Row(key, show, copyKey));
        add(null, Note(string.Format(_t.SetKeySource, Program.KeyFrom), avail));

        // the hand-off: one line the user pastes to the agent, for the port in the field above
        DarkButton? forAgent = null;
        forAgent = Button(_t.SetForAgentBtn, () =>
        {
            Copy(Ports.ConnectCommand(PortValue ?? Program.Port, Program.Key));
            Flash(forAgent!, _t.Copied);
        });
        add(_t.SetForAgent, forAgent);
        add(null, Note(_t.SetForAgentNote, avail));
        return p;
    }

    /// <summary>The port typed in the field, if it is a usable number.</summary>
    private int? PortValue => int.TryParse(_port?.Box.Text, out int v) && v is >= 1024 and <= 65535 ? v : null;

    private void ShowPortStatus()
    {
        if (_port == null || _portStatus == null) return;
        (string text, Color color) v = PortValue switch
        {
            null => (_t.SetPortBad, Palette.Warn),
            int p when p == Program.Port => (_t.SetPortHere, Palette.Success),
            int p when Ports.IsFree(p) => (_t.SetPortFree, Palette.Success),
            int p => (Ports.Owner(p) is string o ? string.Format(_t.SetPortBusy, o) : _t.SetPortBusyUnknown, Palette.Warn),
        };
        _portStatus.Text = v.text;
        _portStatus.ForeColor = v.color;
    }

    /// <summary>Keep the typed port if it works (free, or this server's); otherwise the first free one from 7788.</summary>
    private void PickPort()
    {
        if (PortValue is int cur && (cur == Program.Port || Ports.IsFree(cur))) { ShowPortStatus(); return; }
        if (Ports.FindFree() is int free) _port.Box.Text = free.ToString();
        else { _portStatus.Text = string.Format(_t.SetPortNone, Ports.First, Ports.Last); _portStatus.ForeColor = Palette.Warn; }
    }

    /// <summary>Confirm a click on the button itself for a moment ("Copied ✓").</summary>
    private void Flash(DarkButton b, string text)
    {
        var was = b.Text;
        int w = b.Width;
        b.Text = text;
        b.Width = Math.Max(w, b.Width);
        var t = new System.Windows.Forms.Timer { Interval = 1400 };
        t.Tick += (_, _) => { t.Dispose(); if (!b.IsDisposed) { b.Text = was; b.Width = w; } };
        t.Start();
    }

    private Panel LookPage()
    {
        var g = Settings.Global;
        var (p, add) = Page();
        var ru = new DarkButton("Русский", "secondary", _s) { Tag = "ru" };
        var en = new DarkButton("English", "secondary", _s) { Tag = "en" };
        ru.Click += (_, _) => { _english = false; ShowLang(); };
        en.Click += (_, _) => { _english = true; ShowLang(); };
        add(_t.SetLang, Row(ru, en));

        // the default accent first, then the named mark colours
        var colors = new[] { Palette.DefaultAccent }.Concat(new[] { "orange", "yellow", "green", "teal", "blue", "purple", "white" }
            .Where(Palette.Named.ContainsKey).Select(n => Palette.Named[n])).ToList();
        var row = new List<Control>();
        foreach (var c in colors)
        {
            var sw = new DarkSwatch(c, _s);
            sw.Click += (_, _) => { _accent = sw.Value; ShowAccent(); };
            _swatches.Add(sw);
            row.Add(sw);
        }
        row.Add(Button(_t.SetPickColor, PickColor));
        add(_t.SetAccent, Row(row.ToArray()));

        int tw = _pageW;
        add(null, new DarkToggle(_t.SetAutoColor, g.AutoColor, _s, tw) { Tag = "autoColor" }, wide: true);
        add(null, new DarkToggle(_t.SetAnimate, g.Animate ?? true, _s, tw) { Tag = "animate" }, wide: true);
        add(null, new DarkToggle(_t.SetHideInactive, g.HideWhenInactive ?? true, _s, tw) { Tag = "hideInactive" }, wide: true);
        add(null, new DarkToggle(_t.SetHideCapture, g.HideFromCapture, _s, tw) { Tag = "hideCapture" }, wide: true);
        return p;
    }

    private Panel KeysPage()
    {
        var g = Settings.Global;
        var (p, add) = Page();
        add(null, Note(_t.SetKeysNote, _pageW), wide: true);
        var names = new Dictionary<string, string>
        {
            ["done"] = _t.Done, ["back"] = _t.Back, ["skip"] = _t.Skip, ["undo"] = _t.Undo, ["stop"] = _t.KeyStop, ["help"] = _t.Help,
        };
        foreach (var a in Settings.Actions)
        {
            _chords[a] = g.Hotkeys.TryGetValue(a, out var c) ? c : null;
            var f = new DarkField(_s, (int)(230 * _s), readOnly: true);
            f.Box.ShortcutsEnabled = false;
            f.Box.Tag = a;
            f.Box.KeyDown += CaptureChord;
            f.Box.Cursor = Cursors.Hand;
            _keyBoxes[a] = f;
            add(names[a], f);
        }
        return p;
    }

    // ------------------------------------------------------------------ building blocks

    private delegate void AddRow(string? caption, Control control, bool wide = false);

    private const int DefaultPort = 7788;

    /// <summary>Width of the caption column on every page.</summary>
    private int CaptionW => (int)(150 * _s);

    /// <summary>A page and a function adding "caption | control" rows to it, top to bottom.</summary>
    private (Panel page, AddRow add) Page()
    {
        var p = new Panel { BackColor = DarkBox.Surface, Location = Point.Empty, Width = _pageW };
        int y = 0;
        void Add(string? caption, Control c, bool wide = false)
        {
            if (caption != null)
            {
                var l = Label(caption, 13.5f, false, Palette.TextDim, CaptionW - (int)(10 * _s));
                l.Location = new Point(0, y + (Math.Max(c.Height, l.Height) - l.Height) / 2);
                p.Controls.Add(l);
            }
            c.Location = new Point(wide ? 0 : CaptionW, y);
            p.Controls.Add(c);
            y = c.Bottom + (int)(12 * _s);
            p.Height = y;
        }
        return (p, Add);
    }

    /// <summary>
    /// A label sized to its text right away (AutoSize only measures once the label is laid out, which is
    /// too late for positioning the next control below it). maxWidth > 0 wraps it.
    /// </summary>
    private Label Label(string text, float px, bool bold, Color color, int maxWidth = 0)
    {
        var font = Theme.Font(px * _s, bold);
        // measured the way Label draws (with GDI's side padding), plus a little slack against rounding
        var flags = maxWidth > 0 ? TextFormatFlags.WordBreak : TextFormatFlags.SingleLine;
        int slack = (int)Math.Ceiling(font.Size / 3);
        var size = TextRenderer.MeasureText(text, font, new Size(maxWidth > 0 ? maxWidth - slack : int.MaxValue, int.MaxValue), flags);
        return new Label
        {
            Text = text, AutoSize = false, Font = font, ForeColor = color, BackColor = Color.Transparent,
            Size = new Size(size.Width + slack, size.Height + (int)(2 * _s)), Padding = Padding.Empty, Margin = Padding.Empty,
            UseMnemonic = false,
        };
    }

    private Label Note(string text, int maxWidth) => Label(text, 12.5f, false, Palette.TextDim, maxWidth);

    private DarkButton Button(string text, Action click)
    {
        var b = new DarkButton(text, "secondary", _s, 13);
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>Controls side by side, vertically centred.</summary>
    private Panel Row(params Control?[] items)
    {
        var p = new Panel { BackColor = DarkBox.Surface };
        int x = 0, h = items.Where(i => i != null).Max(i => i!.Height), right = 0;
        Control? prev = null;
        foreach (var c in items)
        {
            if (c == null) continue;
            // swatches carry their own padding: packed tight they read as one palette
            if (prev != null) x += prev is DarkSwatch && c is DarkSwatch ? (int)(2 * _s) : (int)(8 * _s);
            c.Location = new Point(x, (h - c.Height) / 2);
            p.Controls.Add(c);
            x = right = c.Right;
            prev = c;
        }
        p.Size = new Size(Math.Max(1, right), h);
        return p;
    }

    private Control FindByTag(string tag)
    {
        Control? Walk(Control c)
        {
            foreach (Control k in c.Controls)
            {
                if (Equals(k.Tag, tag)) return k;
                if (Walk(k) is Control f) return f;
            }
            return null;
        }
        return Walk(this) ?? throw new InvalidOperationException("settings control missing: " + tag);
    }

    // ------------------------------------------------------------------ behaviour

    private void Select(DarkButton tab)
    {
        foreach (var (b, page) in _tabs)
        {
            b.Selected = b == tab;
            page.Visible = b == tab;
            b.Invalidate();
        }
    }

    private void ShowLang()
    {
        _ru.Selected = !_english; _en.Selected = _english;
        _ru.Invalidate(); _en.Invalidate();
    }

    private void ShowAccent()
    {
        foreach (var sw in _swatches) { sw.Selected = sw.Value.ToArgb() == _accent.ToArgb(); sw.Invalidate(); }
    }

    private static void Copy(string text)
    {
        try { Clipboard.SetText(text); }
        catch (Exception e) { Log.Warn("clipboard: " + e.Message); }
    }

    private void PickColor()
    {
        using var dlg = new ColorDialog { Color = _accent, FullOpen = true, AnyColor = true };
        if (dlg.ShowDialog(this) == DialogResult.OK) { _accent = Color.FromArgb(255, dlg.Color); ShowAccent(); }
    }

    private void ShowChords()
    {
        foreach (var (a, f) in _keyBoxes)
            f.Box.Text = _chords[a] is Chord c ? c.ToString() : "— " + _t.KeyNone + " —";
    }

    /// <summary>The field shows whatever chord is pressed in it; only chords with Ctrl/Alt are taken (plain keys would be stolen from every app).</summary>
    private void CaptureChord(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || box.Tag is not string action) return;
        var code = e.KeyCode;
        if (code == System.Windows.Forms.Keys.Tab && !e.Control && !e.Alt) return;    // let Tab move on
        e.SuppressKeyPress = true;
        e.Handled = true;
        if (code is System.Windows.Forms.Keys.ControlKey or System.Windows.Forms.Keys.Menu or System.Windows.Forms.Keys.ShiftKey
            or System.Windows.Forms.Keys.LWin or System.Windows.Forms.Keys.RWin) return;
        if (code is System.Windows.Forms.Keys.Back or System.Windows.Forms.Keys.Delete && !e.Control && !e.Alt)
        {
            _chords[action] = null;
            ShowChords();
            return;
        }
        uint mods = (e.Control ? Native.MOD_CONTROL : 0) | (e.Alt ? Native.MOD_ALT : 0) | (e.Shift ? Native.MOD_SHIFT : 0);
        if ((mods & (Native.MOD_CONTROL | Native.MOD_ALT)) == 0) return;
        var chord = new Chord(mods, (uint)code);
        // only what config.json can store and read back
        if (Keys.Parse(chord.ToString()) != chord) return;
        _chords[action] = chord;
        ShowChords();
    }

    private void ResetDefaults()
    {
        _port.Box.Text = DefaultPort.ToString();
        _english = false; ShowLang();
        _accent = Palette.DefaultAccent; ShowAccent();
        _autoColor.Checked = true;
        _animate.Checked = true;
        _hideInactive.Checked = true;
        _hideCapture.Checked = false;
        foreach (var (a, c) in Settings.DefaultHotkeys()) _chords[a] = c;
        ShowChords();
    }

    private void Save()
    {
        // a port nobody can use is not saved: the next start (or the restart below) would leave no server at all
        if (PortValue is not int port || (port != Program.Port && !Ports.IsFree(port)))
        {
            Select(_tabs[0].tab);
            ShowPortStatus();
            _port.Box.Focus();
            _port.Box.SelectAll();
            return;
        }
        bool captureWas = Settings.Global.HideFromCapture;
        int portWas = Settings.Global.Port ?? DefaultPort;
        try
        {
            JsonObject root;
            try { root = File.Exists(Settings.FilePath) ? JsonNode.Parse(File.ReadAllText(Settings.FilePath)) as JsonObject ?? new() : new(); }
            catch { root = new JsonObject(); }     // unreadable file: start over rather than refuse to save
            root["port"] = port;
            root["lang"] = _english ? "en" : "ru";
            root["accent"] = $"#{_accent.R:X2}{_accent.G:X2}{_accent.B:X2}";
            root["autoColor"] = _autoColor.Checked;
            root["animate"] = _animate.Checked;
            root["hideWhenInactive"] = _hideInactive.Checked;
            root["hideFromCapture"] = _hideCapture.Checked;
            var hk = new JsonObject();
            foreach (var a in Settings.Actions) hk[a] = _chords[a] is Chord c ? c.ToString() : "none";
            root["hotkeys"] = hk;
            Directory.CreateDirectory(Path.GetDirectoryName(Settings.FilePath)!);
            File.WriteAllText(Settings.FilePath, root.ToJsonString(J.Pretty));
        }
        catch (Exception e)
        {
            MessageBox.Show(this, string.Format(_t.SetSaveFailed, e.Message), Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        Settings.LoadGlobal();
        bool running = Engine.Tutorial is { Active: true };
        Program.ApplyGlobal(running);
        if (Settings.Global.HideFromCapture != captureWas) Program.SetCapturable(!Settings.Global.HideFromCapture);
        Close();

        var t = Settings.Global.Text;
        // only when the user changed the default, and this run is not already on it
        if (port != portWas && port != Program.Port)
        {
            if (MessageBox.Show(string.Format(t.SetRestartAsk, port), "TutorialOverlay", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                Program.Restart();
        }
        else if (running) MessageBox.Show(t.SetAfterTutorial, "TutorialOverlay", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
