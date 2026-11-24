using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace TutorialOverlay;

/// <summary>
/// The small blue "auto" pill that rides beside the pointer while the autoplay moves it: whoever looks at the pointer
/// knows it is not their hand. A window of its own, painted once: it is only moved, right after each step of the
/// pointer (from the thread that moves it), so it keeps up with the pointer instead of following the canvas's ~30
/// frames a second. Click-through, never activated.
/// </summary>
internal sealed class CursorBadge : Form
{
    private static CursorBadge? _instance;
    private static volatile bool _shown;
    private static IntPtr _hwnd;
    private static Size _size;
    private static float _scale;
    private static string _word = "";

    private const uint SWP_NOZORDER = 0x4, SWP_ASYNCWINDOWPOS = 0x4000;

    public CursorBadge()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        _instance = this;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style = Native.WS_POPUP;
            cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _hwnd = Handle;
        if (Settings.Global.HideFromCapture) Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
    }

    /// <summary>Each frame, on the UI thread: shown while the autoplay has the controls, at the pointer.</summary>
    public void Update(bool on)
    {
        if (!Native.GetCursorPos(out var cur)) return;
        var at = new PointF(cur.X, cur.Y);
        if (on)
        {
            float k = Native.ScaleAt(at);
            string word = Settings.Current.Text.CursorAuto;
            if (!_shown || Math.Abs(k - _scale) > 0.01f || word != _word) Draw(at, k, word);
            if (!_shown)
            {
                _shown = true;
                Native.ShowWindow(Handle, 4);     // SW_SHOWNOACTIVATE
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
            Follow(cur.X, cur.Y);                 // the user's own moves too (a paused step, a resume)
        }
        else if (_shown)
        {
            _shown = false;
            Native.ShowWindow(Handle, 0);         // SW_HIDE
        }
    }

    /// <summary>
    /// The pointer is at (x, y) now: the pill goes along, below-right of the arrow's tip. Any thread; never waits on
    /// the UI thread (an asynchronous move).
    /// </summary>
    public static void Follow(int x, int y)
    {
        if (!_shown || _hwnd == IntPtr.Zero) return;
        int dx = (int)(14 * _scale), dy = (int)(22 * _scale);
        Native.SetWindowPos(_hwnd, IntPtr.Zero, x + dx, y + dy, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | SWP_NOZORDER | SWP_ASYNCWINDOWPOS);
    }

    private void Draw(PointF at, float k, string word)
    {
        _scale = k;
        _word = word;
        var font = Theme.Font(11 * k, true);
        float tw = Theme.Measure(word, font, 200 * k).Width;
        int pad = (int)Math.Ceiling(3 * k);       // room for the shadow
        var pill = new RectangleF(pad, pad, tw + 24 * k, 19 * k);
        _size = new Size((int)Math.Ceiling(pill.Right + 2 * pad), (int)Math.Ceiling(pill.Bottom + 2 * pad));
        using var bmp = new Bitmap(_size.Width, _size.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            using (var shp = Theme.Rounded(new RectangleF(pill.X + 1 * k, pill.Y + 2 * k, pill.Width, pill.Height), pill.Height / 2))
            using (var sh = new SolidBrush(Color.FromArgb(90, 0, 0, 0)))
                g.FillPath(sh, shp);
            using (var tp = Theme.Rounded(pill, pill.Height / 2))
            using (var bg = new SolidBrush(Palette.Machine))
                g.FillPath(bg, tp);
            float cx = pill.X + 10 * k, cy = pill.Y + pill.Height / 2, t = 3.6f * k;
            using var w = new SolidBrush(Color.White);
            g.FillPolygon(w, new[] { new PointF(cx - t * 0.8f, cy - t), new PointF(cx + t, cy), new PointF(cx - t * 0.8f, cy + t) });
            g.DrawString(word, font, w, new RectangleF(pill.X + 16 * k, pill.Y, pill.Width - 18 * k, pill.Height),
                new StringFormat(Theme.Center) { Alignment = StringAlignment.Near });
        }
        int dx = (int)(14 * k), dy = (int)(22 * k);
        Native.PresentLayered(Handle, bmp, new Rectangle((int)at.X + dx, (int)at.Y + dy, _size.Width, _size.Height));
    }
}
