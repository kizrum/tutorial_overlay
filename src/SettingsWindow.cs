using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace TutorialOverlay;

/// <summary>
/// Global defaults (config.json) edited by hand: server, look, hotkeys. Opened from the tray icon.
/// Saving rewrites only the fields shown here; anything else in config.json is kept.
/// </summary>
internal sealed class SettingsWindow : Form
{
    private static SettingsWindow? _open;

    private readonly Loc _t = Settings.Global.Text;
    private readonly NumericUpDown _port;
    private readonly TextBox _key;
    private readonly ComboBox _lang;
    private readonly Panel _swatch;
    private readonly CheckBox _autoColor, _animate, _hideInactive, _hideCapture;
    private readonly Dictionary<string, TextBox> _keyBoxes = new();
    private readonly Dictionary<string, Chord?> _chords = new();
    private Color _accent;

    public static void Open()
    {
        if (_open != null)
        {
            if (_open.WindowState == FormWindowState.Minimized) _open.WindowState = FormWindowState.Normal;
            _open.Activate();
            return;
        }
        _open = new SettingsWindow();
        _open.FormClosed += (_, _) => _open = null;
        _open.Show();
        _open.Activate();
    }

    private SettingsWindow()
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = _t.SetTitle;
        Font = SystemFonts.MessageBoxFont ?? Font;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        KeyPreview = true;

        var g = Settings.Global;
        _accent = g.Accent;

        // ---- server
        var server = Grid();
        _port = new NumericUpDown { Minimum = 1024, Maximum = 65535, Value = g.Port ?? Program.Port, Width = 90 };
        server.Controls.Add(Caption(_t.SetPort), 0, 0);
        server.Controls.Add(Row(_port, Program.PortFromArgs ? Note(string.Format(_t.SetPortArg, Program.Port)) : null), 1, 0);
        server.Controls.Add(Note(_t.SetPortNote, 380), 1, 1);
        var url = new TextBox { ReadOnly = true, Width = 230, Text = $"http://127.0.0.1:{Program.Port}/mcp" };
        server.Controls.Add(Caption(_t.SetUrl), 0, 2);
        server.Controls.Add(Row(url, Button(_t.Copy, () => Copy(url.Text))), 1, 2);
        _key = new TextBox { ReadOnly = true, Width = 230, Text = Program.Key, UseSystemPasswordChar = true };
        Button? show = null;
        show = Button(_t.Show, () =>
        {
            _key.UseSystemPasswordChar = !_key.UseSystemPasswordChar;
            show!.Text = _key.UseSystemPasswordChar ? _t.Show : _t.Hide;
        });
        server.Controls.Add(Caption(_t.SetKey), 0, 3);
        server.Controls.Add(Row(_key, show, Button(_t.Copy, () => Copy(Program.Key))), 1, 3);
        server.Controls.Add(Note(string.Format(_t.SetKeySource, Program.KeyFrom), 380), 1, 4);

        // ---- look
        var look = Grid();
        _lang = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
        _lang.Items.AddRange(new object[] { "Русский", "English" });
        _lang.SelectedIndex = g.Lang.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        look.Controls.Add(Caption(_t.SetLang), 0, 0);
        look.Controls.Add(_lang, 1, 0);
        _swatch = new Panel { Size = new Size(36, 23), BackColor = _accent, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(3, 4, 6, 3) };
        look.Controls.Add(Caption(_t.SetAccent), 0, 1);
        look.Controls.Add(Row(_swatch, Button(_t.SetPickColor, PickColor), Button(_t.SetReset, () => SetAccent(Palette.DefaultAccent))), 1, 1);
        _autoColor = Check(_t.SetAutoColor, g.AutoColor);
        _animate = Check(_t.SetAnimate, g.Animate ?? true);
        _hideInactive = Check(_t.SetHideInactive, g.HideWhenInactive ?? true);
        _hideCapture = Check(_t.SetHideCapture, g.HideFromCapture);
        int r = 2;
        foreach (var cb in new[] { _autoColor, _animate, _hideInactive, _hideCapture })
        {
            look.Controls.Add(cb, 0, r);
            look.SetColumnSpan(cb, 2);
            r++;
        }

        // ---- hotkeys
        var keys = Grid();
        var note = Note(_t.SetKeysNote, 380);
        keys.Controls.Add(note, 0, 0);
        keys.SetColumnSpan(note, 2);
        var names = new Dictionary<string, string>
        {
            ["done"] = _t.Done, ["back"] = _t.Back, ["skip"] = _t.Skip, ["undo"] = _t.Undo, ["stop"] = _t.KeyStop, ["help"] = _t.Help,
        };
        r = 1;
        foreach (var a in Settings.Actions)
        {
            _chords[a] = g.Hotkeys.TryGetValue(a, out var c) ? c : null;
            var box = new TextBox { ReadOnly = true, Width = 180, ShortcutsEnabled = false, BackColor = SystemColors.Window, Tag = a };
            box.KeyDown += CaptureChord;
            _keyBoxes[a] = box;
            keys.Controls.Add(Caption(names[a]), 0, r);
            keys.Controls.Add(box, 1, r);
            r++;
        }
        ShowChords();

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(Page(_t.SetTabServer, server));
        tabs.TabPages.Add(Page(_t.SetTabLook, look));
        tabs.TabPages.Add(Page(_t.SetTabKeys, keys));
        int pageW = new[] { server, look, keys }.Max(p => p.PreferredSize.Width) + 30;
        int pageH = new[] { server, look, keys }.Max(p => p.PreferredSize.Height) + 50;
        tabs.Size = new Size(Math.Max(480, pageW), pageH);

        // ---- bottom row
        var save = new Button { Text = _t.SetSave, AutoSize = true, MinimumSize = new Size(96, 28) };
        save.Click += (_, _) => Save();
        var cancel = new Button { Text = _t.Cancel, AutoSize = true, MinimumSize = new Size(96, 28), DialogResult = DialogResult.Cancel };
        cancel.Click += (_, _) => Close();
        var defaults = new Button { Text = _t.SetDefaults, AutoSize = true, MinimumSize = new Size(96, 28) };
        defaults.Click += (_, _) => ResetDefaults();
        AcceptButton = save;
        CancelButton = cancel;
        var bottom = new TableLayoutPanel { ColumnCount = 4, AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 6, 0, 0) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(defaults, 0, 0);
        bottom.Controls.Add(cancel, 2, 0);
        bottom.Controls.Add(save, 3, 0);

        var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Padding = new Padding(10), Dock = DockStyle.Fill };
        root.Controls.Add(tabs, 0, 0);
        root.Controls.Add(bottom, 0, 1);
        Controls.Add(root);
        ResumeLayout(true);
    }

    // ------------------------------------------------------------------ building blocks

    private static TableLayoutPanel Grid()
    {
        var p = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Padding = new Padding(8), Dock = DockStyle.Fill };
        p.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        p.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        return p;
    }

    private static TabPage Page(string title, Control content)
    {
        var page = new TabPage(title) { UseVisualStyleBackColor = true, AutoScroll = true };
        page.Controls.Add(content);
        return page;
    }

    private static Label Caption(string text) =>
        new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 12, 3) };

    private static Label Note(string text, int maxWidth = 0) => new()
    {
        Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 2, 3, 8),
        MaximumSize = maxWidth > 0 ? new Size(maxWidth, 0) : Size.Empty, Anchor = AnchorStyles.Left,
    };

    private static Button Button(string text, Action click)
    {
        var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(0, 25), Margin = new Padding(3, 2, 3, 2) };
        b.Click += (_, _) => click();
        return b;
    }

    private static CheckBox Check(string text, bool on) => new() { Text = text, Checked = on, AutoSize = true, Margin = new Padding(3, 5, 3, 3) };

    private static FlowLayoutPanel Row(params Control?[] items)
    {
        var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty, Anchor = AnchorStyles.Left };
        foreach (var c in items) if (c != null) f.Controls.Add(c);
        return f;
    }

    // ------------------------------------------------------------------ behaviour

    private static void Copy(string text)
    {
        try { Clipboard.SetText(text); }
        catch (Exception e) { Log.Warn("clipboard: " + e.Message); }
    }

    private void SetAccent(Color c)
    {
        _accent = c;
        _swatch.BackColor = c;
    }

    private void PickColor()
    {
        using var dlg = new ColorDialog { Color = _accent, FullOpen = true, AnyColor = true };
        if (dlg.ShowDialog(this) == DialogResult.OK) SetAccent(Color.FromArgb(255, dlg.Color));
    }

    private void ShowChords()
    {
        foreach (var (a, box) in _keyBoxes)
            box.Text = _chords[a] is Chord c ? c.ToString() : "— " + _t.KeyNone + " —";
    }

    /// <summary>The field shows whatever chord is pressed in it; only chords with Ctrl/Alt/Win are taken (plain keys would be stolen from every app).</summary>
    private void CaptureChord(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || box.Tag is not string action) return;
        e.SuppressKeyPress = true;
        e.Handled = true;
        var code = e.KeyCode;
        if (code is System.Windows.Forms.Keys.ControlKey or System.Windows.Forms.Keys.Menu or System.Windows.Forms.Keys.ShiftKey
            or System.Windows.Forms.Keys.LWin or System.Windows.Forms.Keys.RWin) return;
        if (code is System.Windows.Forms.Keys.Back or System.Windows.Forms.Keys.Delete && !e.Control && !e.Alt)
        {
            _chords[action] = null;
            ShowChords();
            return;
        }
        if (code == System.Windows.Forms.Keys.Tab && !e.Control && !e.Alt) { SelectNextControl(box, !e.Shift, true, true, true); return; }
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
        _port.Value = 7788;
        _lang.SelectedIndex = 0;
        SetAccent(Palette.DefaultAccent);
        _autoColor.Checked = true;
        _animate.Checked = true;
        _hideInactive.Checked = true;
        _hideCapture.Checked = false;
        foreach (var (a, c) in Settings.DefaultHotkeys()) _chords[a] = c;
        ShowChords();
    }

    private void Save()
    {
        int port = (int)_port.Value;
        bool captureWas = Settings.Global.HideFromCapture;
        try
        {
            JsonObject root;
            try { root = File.Exists(Settings.FilePath) ? JsonNode.Parse(File.ReadAllText(Settings.FilePath)) as JsonObject ?? new() : new(); }
            catch { root = new JsonObject(); }     // unreadable file: start over rather than refuse to save
            root["port"] = port;
            root["lang"] = _lang.SelectedIndex == 1 ? "en" : "ru";
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
        if (port != Program.Port)
        {
            if (MessageBox.Show(string.Format(t.SetRestartAsk, port), "TutorialOverlay", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                Program.Restart();
        }
        else if (running) MessageBox.Show(t.SetAfterTutorial, "TutorialOverlay", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
