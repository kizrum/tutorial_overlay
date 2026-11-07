using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace TutorialOverlay;

/// <summary>A choice offered by ask().</summary>
internal sealed record AskOption(string Id, string Text, string Style);

/// <summary>
/// The agent's question to the user (ask tool): the question, answer buttons and/or a text field.
/// Sits above the step card (or in the corner of the screen). With buttons only it never takes focus
/// from the app; with a text field it does, while the field is in use.
/// </summary>
internal sealed class AskWindow : Form
{
    private static AskWindow? _open;

    private readonly string _question, _title;
    private readonly List<AskOption> _options;
    private readonly bool _input;
    private readonly Action<JsonObject> _answer;
    private readonly float _s;
    private readonly TextBox? _box;
    private readonly List<(AskOption opt, RectangleF rect)> _pills = new();
    private RectangleF _sendRect, _closeRect, _fieldRect;
    private float _questionH;
    private string _hover = "";
    private bool _answered;

    public static void Show(string question, string? title, List<AskOption> options, bool input, string? placeholder, Action<JsonObject> answer)
    {
        CloseCurrent();
        var w = new AskWindow(question, title, options, input, placeholder, answer);
        _open = w;
        w.FormClosed += (_, _) => { if (_open == w) _open = null; };
        w.Show();
        if (input) { w.Activate(); w._box!.Focus(); }
    }

    /// <summary>Close without an answer (the agent's call ended: timeout, superseded, client gone).</summary>
    public static void CloseCurrent()
    {
        var w = _open;
        if (w == null) return;
        w._answered = true;
        w.Close();
    }

    private AskWindow(string question, string? title, List<AskOption> options, bool input, string? placeholder, Action<JsonObject> answer)
    {
        _question = question;
        _title = title ?? Settings.Current.Text.AskTitle;
        _options = options;
        _input = input;
        _answer = answer;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        BackColor = Palette.Ink;
        KeyPreview = true;
        Text = "TutorialOverlay question";

        // the monitor the user works on: the card's, else the cursor's
        Rectangle? card = Engine.CardRect is Rectangle cr && cr.Width > 0 ? cr : null;
        Native.GetCursorPos(out var cp);
        var at = card is Rectangle c0 ? new PointF(c0.X + c0.Width / 2f, c0.Y) : new PointF(cp.X, cp.Y);
        _s = Native.ScaleAt(at);
        float s = _s;
        int w = card is Rectangle c1 ? c1.Width : (int)(420 * s);
        float pad = 18 * s, inner = w - 2 * pad;

        // layout, top to bottom
        float y = 44 * s;
        _questionH = Theme.Measure(question, Theme.Font(15 * s, true), inner).Height;
        y += _questionH + 14 * s;
        if (options.Count > 0)
        {
            float x = pad, h = 34 * s, gap = 8 * s;
            foreach (var o in options)
            {
                float pw = Math.Min(inner, Math.Max(80 * s, Theme.Measure(o.Text, Theme.Font(13.5f * s, true), inner).Width + 30 * s));
                if (x > pad && x + pw > pad + inner) { x = pad; y += h + gap; }
                _pills.Add((o, new RectangleF(x, y, pw, h)));
                x += pw + gap;
            }
            y += h + 12 * s;
        }
        if (input)
        {
            _fieldRect = new RectangleF(pad, y, inner, 64 * s);
            _box = new TextBox
            {
                Multiline = true, BorderStyle = BorderStyle.None, BackColor = Palette.Ink, ForeColor = Palette.Text,
                Font = Theme.Font(14.5f * s), ScrollBars = ScrollBars.None, PlaceholderText = placeholder ?? "",
                Bounds = Rectangle.Round(RectangleF.Inflate(_fieldRect, -10 * s, -8 * s)),
            };
            _box.KeyDown += (_, e) =>
            {
                if (e.KeyCode == System.Windows.Forms.Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; SubmitText(); }
                else if (e.KeyCode == System.Windows.Forms.Keys.Enter && e.Shift) { e.SuppressKeyPress = true; _box.SelectedText = Environment.NewLine; }
                else if (e.KeyCode == System.Windows.Forms.Keys.Escape) { e.SuppressKeyPress = true; Answer(new JsonObject { ["cancelled"] = "user" }); }
            };
            Controls.Add(_box);
            y += _fieldRect.Height + 10 * s;
            _sendRect = new RectangleF(w - pad - 120 * s, y, 120 * s, 34 * s);
            y += 34 * s + 4 * s;
        }
        int h0 = (int)Math.Ceiling(y + 14 * s);
        _closeRect = new RectangleF(w - 40 * s, 10 * s, 28 * s, 28 * s);

        Rectangle bounds;
        if (card is Rectangle c)
        {
            var work = Native.WorkAreaAt(new PointF(c.X + c.Width / 2f, c.Y));
            int top = c.Y - h0 - (int)(10 * s);
            if (top < work.Top) top = Math.Min(c.Bottom + (int)(10 * s), work.Bottom - h0);
            bounds = new Rectangle(c.X, top, w, h0);
        }
        else
        {
            var work = Native.WorkAreaAt(at);
            bounds = new Rectangle(work.Right - w - (int)(24 * s), work.Bottom - h0 - (int)(24 * s), w, h0);
        }
        Bounds = bounds;
    }

    // buttons-only: answer without pulling focus away from the app
    protected override bool ShowWithoutActivation => !_input;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW;
            if (!_input) cp.ExStyle |= Native.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (!_input && m.Msg == Native.WM_MOUSEACTIVATE) { m.Result = (IntPtr)Native.MA_NOACTIVATE; return; }
        base.WndProc(ref m);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2;
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, 4);
        if (Settings.Global.HideFromCapture) Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
    }

    private void Answer(JsonObject a)
    {
        if (_answered) return;
        _answered = true;
        _answer(a);
        Close();
    }

    private void SubmitText()
    {
        var text = _box!.Text.Trim();
        if (text.Length == 0) { _box.Focus(); return; }
        Answer(new JsonObject { ["text"] = text });
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        if (!_answered) { _answered = true; _answer(new JsonObject { ["cancelled"] = "user" }); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Quality(g);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        float s = _s, w = ClientSize.Width, h = ClientSize.Height, pad = 18 * s;
        var loc = Settings.Current.Text;
        g.Clear(Palette.Ink);
        using (var pen = new Pen(Palette.Line, 1)) g.DrawRectangle(pen, 0, 0, w - 1, h - 1);

        // header in the card's manner: an icon that says "from the assistant", the title, the cross
        Theme.IconMessage(g, new PointF(pad + 8 * s, 24 * s), 16 * s, Palette.Named["blue"], 1.7f * s);
        using (var ab = new SolidBrush(Palette.TextDim))
            g.DrawString(_title, Theme.Font(13 * s, true), ab, new RectangleF(pad + 24 * s, 15 * s, w - pad * 2 - 64 * s, 20 * s), Theme.Wrap);
        if (_hover == "close") using (var hb = new SolidBrush(Palette.InkHover)) g.FillEllipse(hb, _closeRect);
        Theme.IconCross(g, new PointF(_closeRect.X + _closeRect.Width / 2, _closeRect.Y + _closeRect.Height / 2), 14 * s,
                        _hover == "close" ? Palette.Text : Palette.TextDim, 1.8f * s);
        using (var tb = new SolidBrush(Palette.Text))
            g.DrawString(_question, Theme.Font(15 * s, true), tb, new RectangleF(pad, 44 * s, w - 2 * pad, _questionH + 4 * s), Theme.Wrap);

        foreach (var (o, r) in _pills) DrawPill(g, r, o.Text, o.Style, _hover == "opt:" + o.Id);

        if (_input)
        {
            using (var path = Theme.Rounded(_fieldRect, 6 * s))
            using (var pen = new Pen(_box!.Focused ? Palette.Accent : Palette.Line, 1.2f * s))
                g.DrawPath(pen, path);
            DrawPill(g, _sendRect, loc.Send, "primary", _hover == "send");
            using var hb = new SolidBrush(Palette.WithAlpha(Palette.TextDim, 0.8f));
            g.DrawString(loc.AskHint, Theme.Font(11 * s), hb, new RectangleF(pad, _sendRect.Y, _sendRect.X - pad - 8 * s, _sendRect.Height),
                         new StringFormat(Theme.Center) { Alignment = StringAlignment.Near, FormatFlags = 0 });
        }
    }

    private void DrawPill(Graphics g, RectangleF r, string text, string style, bool hover) =>
        Theme.OutlineButton(g, r, text, style, hover, true, _s);

    private string HitTest(Point p)
    {
        if (_closeRect.Contains(p)) return "close";
        if (_input && _sendRect.Contains(p)) return "send";
        foreach (var (o, r) in _pills) if (r.Contains(p)) return "opt:" + o.Id;
        return "";
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hv = HitTest(e.Location);
        if (hv != _hover) { _hover = hv; Cursor = hv.Length > 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover.Length > 0) { _hover = ""; Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        var hit = HitTest(e.Location);
        if (hit == "close") Answer(new JsonObject { ["cancelled"] = "user" });
        else if (hit == "send") SubmitText();
        else if (hit.StartsWith("opt:"))
        {
            var o = _options.First(x => "opt:" + x.Id == hit);
            var a = new JsonObject { ["choice"] = o.Id, ["label"] = o.Text };
            // a typed note travels with the chosen button
            if (_box != null && _box.Text.Trim().Length > 0) a["text"] = _box.Text.Trim();
            Answer(a);
        }
    }
}
