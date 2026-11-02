using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TutorialOverlay;

/// <summary>
/// Small owner-drawn controls in the card's dark style, for the settings window. Stock WinForms controls
/// keep light borders and arrows whatever their colours, so these are drawn from scratch.
/// </summary>
internal abstract class DarkControl : Control
{
    protected readonly float S;
    protected bool Hover, Pressed;

    protected DarkControl(float s)
    {
        S = s;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Palette.Ink;
        ForeColor = Palette.Text;
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Hover = false; Pressed = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { Pressed = true; Invalidate(); } }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); Pressed = false; Invalidate(); }

    protected static void Prepare(Graphics g)
    {
        Theme.Quality(g);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
    }
}

/// <summary>Pill button: primary (accent), secondary (raised), ghost (outline only), or a selected segment.</summary>
internal sealed class DarkButton : DarkControl
{
    public string Style;
    public bool Selected;
    private readonly float _px;

    public DarkButton(string text, string style, float s, float px = 13.5f) : base(s)
    {
        Text = text;
        Style = style;
        _px = px;
        Size = Size.Ceiling(new SizeF(Theme.Measure(text, Theme.Font(px * s, true), 600 * s).Width + 32 * s, 34 * s));
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Width = (int)Math.Ceiling(Theme.Measure(Text, Theme.Font(_px * S, true), 600 * S).Width + 32 * S);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Prepare(g);
        g.Clear(Parent?.BackColor ?? Palette.Ink);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Theme.Rounded(r, r.Height / 2);
        bool primary = Style == "primary" || Selected;
        Color fill = primary ? Palette.Accent : Style == "ghost" ? Color.Transparent : Palette.InkRaised;
        if (Hover) fill = primary ? Color.FromArgb(255, Math.Min(255, fill.R + 20), Math.Min(255, fill.G + 30), Math.Min(255, fill.B + 26)) : Palette.InkHover;
        if (Pressed && !primary) fill = Palette.Line;
        if (!Enabled) fill = Palette.WithAlpha(fill, 0.45f);
        if (fill.A > 0) using (var b = new SolidBrush(fill)) g.FillPath(b, path);
        if (!primary) using (var pen = new Pen(Hover ? Palette.WithAlpha(Palette.Accent, 0.7f) : Palette.Line, 1 * S)) g.DrawPath(pen, path);
        using var tb = new SolidBrush(primary ? Palette.Ink : Enabled ? Palette.Text : Palette.TextDim);
        g.DrawString(Text, Theme.Font(_px * S, true), tb, r, Theme.Center);
    }
}

/// <summary>On/off switch with a caption to its right; the whole row is clickable.</summary>
internal sealed class DarkToggle : DarkControl
{
    private bool _checked;
    public event EventHandler? CheckedChanged;

    public bool Checked
    {
        get => _checked;
        set { if (_checked == value) return; _checked = value; Invalidate(); CheckedChanged?.Invoke(this, EventArgs.Empty); }
    }

    public DarkToggle(string text, bool on, float s, int width) : base(s)
    {
        Text = text;
        _checked = on;
        var th = Theme.Measure(text, Theme.Font(14 * s), width - 60 * s).Height;
        Size = new Size(width, (int)Math.Ceiling(Math.Max(30 * s, th + 10 * s)));
    }

    protected override void OnClick(EventArgs e) { base.OnClick(e); Checked = !Checked; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Prepare(g);
        g.Clear(Parent?.BackColor ?? Palette.Ink);
        float s = S, tw = 40 * s, th = 22 * s, ty = (Height - th) / 2;
        var track = new RectangleF(1, ty, tw, th);
        using (var path = Theme.Rounded(track, th / 2))
        {
            using (var b = new SolidBrush(_checked ? Palette.Accent : Hover ? Palette.InkHover : Palette.InkRaised)) g.FillPath(b, path);
            if (!_checked) using (var pen = new Pen(Palette.Line, 1 * s)) g.DrawPath(pen, path);
        }
        float d = th - 6 * s, kx = _checked ? track.Right - d - 3 * s : track.X + 3 * s;
        using (var kb = new SolidBrush(_checked ? Palette.Ink : Palette.TextDim)) g.FillEllipse(kb, kx, ty + 3 * s, d, d);
        using var tb = new SolidBrush(Palette.Text);
        g.DrawString(Text, Theme.Font(14 * s), tb, new RectangleF(tw + 14 * s, 0, Width - tw - 14 * s, Height),
                     new StringFormat(Theme.Center) { Alignment = StringAlignment.Near, FormatFlags = 0 });
    }
}

/// <summary>A text box inside a rounded dark field; the border lights up in the accent colour while focused.</summary>
internal sealed class DarkField : Panel
{
    public readonly TextBox Box;
    private readonly float _s;

    public DarkField(float s, int width, bool readOnly = false)
    {
        _s = s;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Palette.Ink;
        Size = new Size(width, (int)(36 * s));
        Box = new TextBox
        {
            BorderStyle = BorderStyle.None, BackColor = Palette.InkRaised, ForeColor = Palette.Text, ReadOnly = readOnly,
            Font = Theme.Font(14 * s), Width = width - (int)(24 * s), Left = (int)(12 * s),
        };
        Box.Top = (Height - Box.PreferredHeight) / 2;
        Box.GotFocus += (_, _) => Invalidate();
        Box.LostFocus += (_, _) => Invalidate();
        Controls.Add(Box);
        Cursor = Cursors.IBeam;
        Click += (_, _) => Box.Focus();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Quality(g);
        g.Clear(Parent?.BackColor ?? Palette.Ink);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Theme.Rounded(r, 8 * _s);
        using (var b = new SolidBrush(Palette.InkRaised)) g.FillPath(b, path);
        using var pen = new Pen(Box.Focused ? Palette.Accent : Palette.Line, (Box.Focused ? 1.5f : 1f) * _s);
        g.DrawPath(pen, path);
    }
}

/// <summary>A colour to pick: a filled circle, ringed when selected.</summary>
internal sealed class DarkSwatch : DarkControl
{
    public Color Value { get; }
    public bool Selected;

    public DarkSwatch(Color c, float s) : base(s)
    {
        Value = c;
        Size = new Size((int)(30 * s), (int)(30 * s));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Prepare(g);
        g.Clear(Parent?.BackColor ?? Palette.Ink);
        float s = S, pad = 5 * s;
        using (var b = new SolidBrush(Value)) g.FillEllipse(b, pad, pad, Width - 2 * pad - 1, Height - 2 * pad - 1);
        if (Selected || Hover)
            using (var pen = new Pen(Selected ? Palette.Text : Palette.TextDim, 2 * s)) g.DrawEllipse(pen, 1.5f * s, 1.5f * s, Width - 3 * s - 1, Height - 3 * s - 1);
    }
}

/// <summary>A rounded, outlined panel a shade lighter than the window: groups a page of settings.</summary>
internal sealed class DarkBox : Panel
{
    public static readonly Color Surface = Color.FromArgb(255, 24, 27, 34);
    private readonly float _s;

    public DarkBox(float s)
    {
        _s = s;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Surface;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Quality(g);
        g.Clear(Parent?.BackColor ?? Palette.Ink);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Theme.Rounded(r, 12 * _s);
        using (var b = new SolidBrush(Surface)) g.FillPath(b, path);
        using var pen = new Pen(Palette.Line, 1 * _s);
        g.DrawPath(pen, path);
    }
}

/// <summary>Dark colours for the tray icon's context menu.</summary>
internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkMenuColors()) { RoundedEdges = false; }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Palette.Text : Palette.TextDim;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Palette.TextDim;
        base.OnRenderArrow(e);
    }

    private sealed class DarkMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Palette.Ink;
        public override Color ImageMarginGradientBegin => Palette.Ink;
        public override Color ImageMarginGradientMiddle => Palette.Ink;
        public override Color ImageMarginGradientEnd => Palette.Ink;
        public override Color MenuBorder => Palette.Line;
        public override Color MenuItemBorder => Palette.InkHover;
        public override Color MenuItemSelected => Palette.InkHover;
        public override Color MenuItemSelectedGradientBegin => Palette.InkHover;
        public override Color MenuItemSelectedGradientEnd => Palette.InkHover;
        public override Color MenuItemPressedGradientBegin => Palette.InkRaised;
        public override Color MenuItemPressedGradientEnd => Palette.InkRaised;
        public override Color SeparatorDark => Palette.Line;
        public override Color SeparatorLight => Palette.Ink;
    }
}

internal static class DarkWindow
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_BORDER_COLOR = 34, DWMWA_CAPTION_COLOR = 35;

    private static int ColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

    /// <summary>
    /// Dark title bar (Windows 10 20H1+); on Windows 11 also the caption in the card's ink colour and a visible
    /// outline, so the dark window does not melt into a dark desktop.
    /// </summary>
    public static void TitleBar(IntPtr hwnd)
    {
        int on = 1;
        Native.DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, 4);
        int caption = ColorRef(Palette.Ink);
        Native.DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, 4);
        int border = ColorRef(Palette.Line);
        Native.DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, 4);
    }
}
