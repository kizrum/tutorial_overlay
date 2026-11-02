using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace TutorialOverlay;

/// <summary>
/// Screen reads. Our own windows are marked WDA_EXCLUDEFROMCAPTURE, so these never see the overlay's
/// drawings or the step card — pixel checks look at the real application.
/// </summary>
internal static class ScreenCapture
{
    public static Color Sample(PointF p, int radius)
    {
        int n = radius * 2 + 1;
        int x = (int)Math.Round(p.X) - radius, y = (int)Math.Round(p.Y) - radius;
        try
        {
            using var bmp = new Bitmap(n, n, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(x, y, 0, 0, new Size(n, n), CopyPixelOperation.SourceCopy);
            long r = 0, gr = 0, b = 0;
            for (int i = 0; i < n; i++)
                for (int k = 0; k < n; k++)
                {
                    var c = bmp.GetPixel(i, k);
                    r += c.R; gr += c.G; b += c.B;
                }
            int cnt = n * n;
            return Color.FromArgb(255, (int)(r / cnt), (int)(gr / cnt), (int)(b / cnt));
        }
        catch (Exception e)
        {
            Log.Debug("sample failed: " + e.Message);
            return Color.Black;
        }
    }

    public static Bitmap Capture(Rectangle r)
    {
        var bmp = new Bitmap(Math.Max(1, r.Width), Math.Max(1, r.Height), PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(r.X, r.Y, 0, 0, bmp.Size, CopyPixelOperation.SourceCopy);
        return bmp;
    }

    /// <summary>PNG bytes, downscaled so the long edge is at most <paramref name="maxEdge"/>.</summary>
    public static byte[] Png(Bitmap src, int maxEdge, out float scale)
    {
        scale = 1f;
        Bitmap img = src;
        int edge = Math.Max(src.Width, src.Height);
        if (maxEdge > 0 && edge > maxEdge)
        {
            scale = maxEdge / (float)edge;
            img = new Bitmap(Math.Max(1, (int)(src.Width * scale)), Math.Max(1, (int)(src.Height * scale)), PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(img);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(src, 0, 0, img.Width, img.Height);
        }
        using var ms = new MemoryStream();
        img.Save(ms, ImageFormat.Png);
        if (!ReferenceEquals(img, src)) img.Dispose();
        return ms.ToArray();
    }
}
