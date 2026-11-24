using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using TutorialOverlay;

namespace TutorialOverlay.Tests;

/// <summary>
/// The mascot's acts as frames, drawn by the real figure code without a lesson — the README's animations:
///   dotnet run --project tests -- --mascot OUTDIR [fps]
/// Each act goes to OUTDIR/&lt;act&gt;/NNNN.png (transparent); tests/make_media.py turns the folders into animations.
/// </summary>
internal static class MascotFilm
{
    private const float Scale = 1.5f;
    private const int W = 400, H = 320;
    private static readonly PointF Pos = new(140, 208);                 // the figure's centre
    private static readonly RectangleF Button = new(258, 168, 96, 40);  // the stand-in target it points at

    /// <summary>One act: its length and how each frame is set up (what the lesson would tell the figure).</summary>
    private sealed record Act(string Name, float Seconds, Action<MascotInput, float> Setup, bool Target = true,
                              string? Say = null, Func<float, string>? Stage = null);

    private static readonly Act[] Acts =
    {
        new("appear", 1.6f, (i, t) => { }, Stage: t => t < 1 ? "appear" : "point"),
        new("point", 2.6f, (i, t) => { }, Say: "Click Save"),
        new("hit", 1.3f, (i, t) => { }, Stage: t => t < 0.9f ? "hit1" : "point"),
        new("thumbs", 1.3f, (i, t) => { }, Stage: t => t < 0.9f ? "hit2" : "point"),
        new("miss", 1.9f, (i, t) => { }, Stage: t => t < 1.5f ? "miss" : "point"),
        new("keys", 4.4f, (i, t) => { i.Keys = "Ctrl+S"; i.KeyChord = TutorialOverlay.Keys.Parse("Ctrl+S"); }, Target: false),
        new("thinks", 2.8f, (i, t) => i.Thinking = true),
        new("scroll", 2.4f, (i, t) => { i.Gesture = "scroll"; i.ScrollDir = "down"; }, Target: false),
        new("drag", 3.6f, (i, t) => i.Gesture = "drag", Target: false),
        new("outro", 2.2f, (i, t) => { i.Outro = true; i.Progress = 1; }, Target: false),
        new("walk", 1.6f, (i, t) => { }, Target: false, Stage: t => "walk"),
        // the autoplay: the cap drops on, the goggles come down while it moves the pointer, the hands go up as it gives
        // the controls back
        new("auto-dress", 1.4f, (i, t) => i.Autoplay = "on", Stage: t => t < 1 ? "dress" : "point"),
        new("auto-drive", 2.4f, (i, t) => i.Autoplay = "drive", Say: "Pressing “Save”"),
        new("auto-handback", 1.6f, (i, t) => i.Autoplay = t < 1.1f ? "on" : "off", Stage: t => t < 1.1f ? "handback" : "point"),
    };

    public static int Run(string[] args)
    {
        if (args.Length == 0) { Console.Error.WriteLine("usage: --mascot OUTDIR [fps] [zoom]"); return 1; }
        var outDir = Path.GetFullPath(args[0]);
        int fps = args.Length > 1 && int.TryParse(args[1], out var f) ? f : 25;
        // zoom: the whole frame drawn this many times larger — sharp on high-DPI screens, where the page shows it shrunk
        int zoom = args.Length > 2 && int.TryParse(args[2], out var z) && z > 0 ? z : 2;
        using var m = new MascotWindow();
        foreach (var act in Acts)
        {
            var dir = Path.Combine(outDir, act.Name);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            int n = (int)MathF.Round(act.Seconds * fps);
            for (int k = 0; k < n; k++)
            {
                float t = k / (float)fps;
                var stage = act.Stage?.Invoke(t) ?? "point";
                float p = stage switch
                {
                    "appear" => t / 1.0f,
                    "hit1" or "hit2" => t / 0.9f,
                    "miss" => t / 1.5f,
                    "walk" => 0.12f + 0.68f * (t / act.Seconds),   // the steps, without the lean in and the stop
                    "dress" => t / 1.0f,
                    "handback" => t / 1.1f,
                    _ => 0,
                };
                var inp = new MascotInput { Scale = Scale, Progress = 0.6f };
                if (act.Target) inp.Target = new PointF(Button.Left + 14, Button.Top + Button.Height / 2);
                act.Setup(inp, t);

                using var bmp = new Bitmap(W * zoom, H * zoom, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    Theme.Quality(g);
                    g.Clear(Color.Transparent);
                    g.ScaleTransform(zoom, zoom);
                    if (act.Target) DrawTarget(g, stage == "hit1" || stage == "hit2", stage == "miss" && p > 0.55f, t);
                    var pos = stage == "walk" ? new PointF(80 + 240 * (t / act.Seconds), Pos.Y) : Pos;
                    var bubble = act.Say != null ? new RectangleF(176, 58, 164, 60) : RectangleF.Empty;
                    m.DrawStill(g, inp, pos, t + 0.3f, stage, Math.Clamp(p, 0, 1), act.Say, bubble);
                }
                bmp.Save(Path.Combine(dir, $"{k:0000}.png"), ImageFormat.Png);
            }
            Console.WriteLine($"{act.Name}: {n} frames");
        }
        return 0;
    }

    /// <summary>A plain button with the lesson's ring round it: green once hit, flashing on a miss.</summary>
    private static void DrawTarget(Graphics g, bool hit, bool flash, float t)
    {
        using (var path = Theme.Rounded(Button, 8))
        using (var fill = new SolidBrush(Color.FromArgb(255, 246, 247, 250)))
        using (var edge = new Pen(Color.FromArgb(255, 200, 204, 214), 1.5f))
        using (var ink = new SolidBrush(Color.FromArgb(255, 42, 46, 54)))
        {
            g.FillPath(fill, path);
            g.DrawPath(edge, path);
            g.DrawString("Save", Theme.Font(14, true), ink, Button, Theme.Center);
        }
        var accent = hit ? Color.FromArgb(255, 46, 204, 113) : Color.FromArgb(255, 255, 77, 109);
        int a = flash ? (int)(255 * (0.4f + 0.6f * MathF.Abs(MathF.Sin(t * 14)))) : 255;
        using var ring = new Pen(Color.FromArgb(a, accent), 3.5f) { LineJoin = LineJoin.Round };
        using var rp = Theme.Rounded(RectangleF.Inflate(Button, 7, 7), 13);
        g.DrawPath(ring, rp);
    }
}
