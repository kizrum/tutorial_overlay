using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace TutorialOverlay;

/// <summary>
/// Click-through canvas: WS_EX_LAYERED|TRANSPARENT|TOOLWINDOW|NOACTIVATE|TOPMOST, painted with
/// UpdateLayeredWindow from a premultiplied 32-bit DIB (true per-pixel alpha, no colour-key fringe).
/// The window is sized to the drawn content each frame, so small marks cost almost nothing.
/// </summary>
internal sealed class OverlayWindow : Form
{
    private IntPtr _memDc, _dib, _oldBmp, _bits;
    private Bitmap? _bmp;
    private Graphics? _g;
    private Rectangle _surface;          // virtual-screen rect the DIB covers
    private Rectangle _shown = Rectangle.Empty;
    private bool _visible;
    public bool ExcludedFromCapture { get; private set; }

    public OverlayWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        Text = "TutorialOverlay canvas";
        Bounds = new Rectangle(0, 0, 1, 1);
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
        // optional: keep our drawings out of every capture (see Settings.HideFromCapture)
        if (Settings.Global.HideFromCapture)
        {
            ExcludedFromCapture = Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
            if (!ExcludedFromCapture) Log.Warn("SetWindowDisplayAffinity failed: the overlay stays visible in captures");
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_MOUSEACTIVATE) { m.Result = (IntPtr)Native.MA_NOACTIVATE; return; }
        base.WndProc(ref m);
    }

    private void EnsureSurface()
    {
        var vs = Native.VirtualScreen();
        if (vs == _surface && _g != null) return;
        FreeSurface();
        _surface = vs;
        var bi = new Native.BITMAPINFOHEADER
        {
            biSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
            biWidth = vs.Width, biHeight = -vs.Height, biPlanes = 1, biBitCount = 32,
        };
        var screen = Native.GetDC(IntPtr.Zero);
        _memDc = Native.CreateCompatibleDC(screen);
        _dib = Native.CreateDIBSection(screen, ref bi, 0, out _bits, IntPtr.Zero, 0);
        Native.ReleaseDC(IntPtr.Zero, screen);
        _oldBmp = Native.SelectObject(_memDc, _dib);
        _bmp = new Bitmap(vs.Width, vs.Height, vs.Width * 4, PixelFormat.Format32bppPArgb, _bits);
        _g = Graphics.FromImage(_bmp);
        _g.TranslateTransform(-vs.X, -vs.Y);
    }

    private void FreeSurface()
    {
        _g?.Dispose(); _g = null;
        _bmp?.Dispose(); _bmp = null;
        if (_memDc != IntPtr.Zero)
        {
            Native.SelectObject(_memDc, _oldBmp);
            Native.DeleteDC(_memDc);
            _memDc = IntPtr.Zero;
        }
        if (_dib != IntPtr.Zero) { Native.DeleteObject(_dib); _dib = IntPtr.Zero; }
    }

    public void Present(Scene? scene)
    {
        if (scene == null || (scene.Prims.Count == 0 && scene.Ripples.Count == 0 && scene.Dim <= 0.01f))
        {
            HideCanvas();
            return;
        }
        EnsureSurface();
        Rectangle content;
        if (scene.Dim > 0.01f) content = _surface;
        else
        {
            var cb = Renderer.ContentBounds(scene);
            content = Rectangle.Intersect(Rectangle.Ceiling(RectangleF.FromLTRB((float)Math.Floor(cb.Left), (float)Math.Floor(cb.Top), cb.Right, cb.Bottom)), _surface);
        }
        if (content.Width <= 0 || content.Height <= 0) { HideCanvas(); return; }

        var g = _g!;
        g.SetClip(content);
        g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
        g.Clear(Color.Transparent);
        g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
        Renderer.Draw(g, scene, _surface);
        g.ResetClip();
        g.Flush(System.Drawing.Drawing2D.FlushIntention.Sync);

        var dst = new Native.POINT(content.X, content.Y);
        var size = new Native.SIZE(content.Width, content.Height);
        var src = new Native.POINT(content.X - _surface.X, content.Y - _surface.Y);
        var blend = new Native.BLENDFUNCTION { BlendOp = Native.AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = Native.AC_SRC_ALPHA };
        var screen = Native.GetDC(IntPtr.Zero);
        Native.UpdateLayeredWindow(Handle, screen, ref dst, ref size, _memDc, ref src, 0, ref blend, Native.ULW_ALPHA);
        Native.ReleaseDC(IntPtr.Zero, screen);
        _shown = content;
        if (!_visible)
        {
            Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
            _visible = true;
        }
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    public void HideCanvas()
    {
        if (!_visible) return;
        Native.ShowWindow(Handle, Native.SW_HIDE);
        _visible = false;
        _shown = Rectangle.Empty;
    }

    protected override void Dispose(bool disposing)
    {
        FreeSurface();
        base.Dispose(disposing);
    }
}
