using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using TutorialOverlay;

namespace TutorialOverlay.Tests;

/// <summary>
/// A picture of the overlay's shapes, drawn by the real layout and renderer over plain stand-ins for an app (a light
/// window with a few controls) — to look at a shape's design without a live lesson:
///   dotnet run --project tests -- --shapes out.png [filter]
/// Each cell is a mark as a tutorial would write it, with the caption it is listed under.
/// </summary>
internal static class ShapeGallery
{
    private const int CellW = 360, CellH = 200, Cols = 4, Gap = 16;

    private sealed record Cell(string Caption, string Shapes, Rectangle Target, string Ui = "button");

    private static readonly Cell[] Cells =
    {
        new("arrow (as before)", """[{"type":"arrow","from":"sw"}]""", new(250, 50, 70, 32)),
        new("curve 0.2", """[{"type":"arrow","from":"sw","curve":0.2,"length":160}]""", new(250, 50, 70, 32)),
        new("curve + end dot", """[{"type":"arrow","from":"w","curve":0.25,"length":170,"end":"dot"}]""", new(250, 84, 70, 32)),
        new("end chevron", """[{"type":"arrow","from":"w","length":150,"end":"chevron"}]""", new(250, 84, 70, 32)),
        new("end bar", """[{"type":"arrow","from":"w","length":150,"end":"bar"}]""", new(250, 84, 70, 32)),
        new("end ring", """[{"type":"arrow","from":"sw","length":150,"end":"ring"}]""", new(250, 50, 70, 32)),
        new("end none", """[{"type":"arrow","from":"sw","length":150,"end":"none"}]""", new(250, 50, 70, 32)),
        new("taper tail", """[{"type":"arrow","from":"w","length":170,"taper":"tail","thickness":5}]""", new(250, 84, 70, 32)),
        new("taper both + chevron", """[{"type":"arrow","from":"sw","curve":0.3,"length":170,"taper":"both","end":"chevron","thickness":5}]""", new(250, 50, 70, 32)),
        new("curve + label at the tail", """[{"type":"arrow","from":"sw","curve":0.25,"length":130},{"type":"label","text":"Here"}]""", new(250, 40, 70, 32)),
        new("brackets", """[{"type":"brackets"}]""", new(72, 82, 216, 36), "field"),
        new("brackets thin", """[{"type":"brackets","style":"thin"}]""", new(72, 82, 216, 36), "field"),
        new("brackets on an icon", """[{"type":"brackets"}]""", new(164, 84, 32, 32)),
        new("pill", """[{"type":"pill"}]""", new(144, 84, 72, 28), "segments"),
        new("marker", """[{"type":"marker"}]""", new(124, 62, 112, 18), "text"),
        new("elbow, thin, chevron", """[{"type":"rect","style":"thin"},{"type":"arrow","from":[-150,-90],"route":"elbow","end":"chevron","thickness":2}]""", new(220, 128, 40, 16), "grid"),
        new("elbow, level", """[{"type":"arrow","from":"w","route":"elbow","length":140}]""", new(250, 84, 70, 32)),
        new("forbid", """[{"type":"forbid"}]""", new(20, 104, 320, 64), "danger"),
        new("band", """[{"type":"band"}]""", new(20, 103, 320, 27), "table"),
        new("group (3 targets)", "", new(56, 54, 16, 16), "group"),
        new("off screen (right)", "", new(0, 0, 0, 0), "offscreen"),
        new("marker + badge", """[{"type":"marker"},{"type":"badge","text":"2"}]""", new(124, 62, 112, 18), "text"),
    };

    /// <summary>The stand-in for the app under a mark: a button, a text field, a segmented control, a line of text.</summary>
    private static void DrawUi(Graphics g, string ui, Rectangle t, int ox, int oy)
    {
        using var white = new SolidBrush(Color.White);
        using var edge = new Pen(Color.FromArgb(201, 206, 214));
        using var ink = new SolidBrush(Color.FromArgb(42, 46, 54));
        var font = Theme.Font(13);
        switch (ui)
        {
            case "field":
                g.FillRectangle(white, t); g.DrawRectangle(edge, t);
                g.DrawString("Без названия", font, ink, t.X + 10, t.Y + 8);
                break;
            case "segments":
            {
                var bar = new Rectangle(ox + 60, t.Y - 4, 240, t.Height + 8);
                using (var grey = new SolidBrush(Color.FromArgb(225, 228, 234))) g.FillRectangle(grey, bar);
                g.FillRectangle(white, ox + 64, t.Y, 72, t.Height);
                string[] names = { "День", "Неделя", "Месяц" };
                for (int i = 0; i < 3; i++)
                {
                    var cell = new RectangleF(ox + 64 + i * 80, t.Y, 72, t.Height);
                    g.DrawString(names[i], font, ink, cell, Theme.Center);
                }
                break;
            }
            case "text":
                g.DrawString("Включите", font, ink, ox + 48, t.Y);
                using (var bold = new SolidBrush(Color.FromArgb(42, 46, 54))) g.DrawString("Автосохранение,", Theme.Font(13, true), bold, t.X, t.Y);
                g.DrawString("чтобы изменения не терялись", font, ink, ox + 48, t.Y + 26);
                break;
            case "grid":
            {
                using var dark = new SolidBrush(Color.FromArgb(31, 34, 41));
                g.FillRectangle(dark, ox, oy, CellW, CellH);
                using var gl = new Pen(Color.FromArgb(46, 50, 60));
                for (int y = 16; y < CellH; y += 16) g.DrawLine(gl, ox, oy + y, ox + CellW, oy + y);
                for (int x = 20; x < CellW; x += 40) g.DrawLine(gl, ox + x, oy, ox + x, oy + CellH);
                using var note = new SolidBrush(Color.FromArgb(180, 78, 123, 217));
                g.FillRectangle(note, ox + 100, oy + 80, 40, 16); g.FillRectangle(note, ox + 180, oy + 96, 80, 16);
                break;
            }
            case "danger":
            {
                var top = new Rectangle(ox + 20, oy + 24, 320, 56);
                g.FillRectangle(white, top); g.DrawRectangle(edge, top);
                g.DrawString("Выйти из аккаунта", font, ink, ox + 36, oy + 36);
                g.FillRectangle(white, t); g.DrawRectangle(edge, t);
                g.DrawString("Удалить аккаунт", font, ink, t.X + 16, t.Y + 14);
                using var red = new SolidBrush(Color.FromArgb(217, 48, 54));
                g.FillRectangle(red, t.X + 230, t.Y + 18, 76, 28);
                break;
            }
            case "table":
            {
                var box = new Rectangle(ox + 20, oy + 22, 320, 160);
                g.FillRectangle(white, box); g.DrawRectangle(edge, box);
                string[] rows = { "заметки.txt", "план.docx", "бюджет.xlsx", "фото.jpg", "архив.zip" };
                for (int i = 0; i < rows.Length; i++) g.DrawString(rows[i], font, ink, ox + 32, oy + 52 + i * 27);
                break;
            }
            case "group":
            {
                var panel = new Rectangle(ox + 24, oy + 20, 312, 168);
                g.FillRectangle(white, panel); g.DrawRectangle(edge, panel);
                string[] names = { "Субтитры", "Обложка", "Главы", "Водяной знак" };
                for (int i = 0; i < 4; i++)
                {
                    g.DrawRectangle(edge, ox + 56, oy + 54 + i * 34, 16, 16);
                    g.DrawString(names[i], font, ink, ox + 84, oy + 52 + i * 34);
                }
                break;
            }
            case "offscreen":
                g.FillRectangle(white, ox + 16, oy + 20, 300, 160);
                g.DrawRectangle(edge, ox + 16, oy + 20, 300, 160);
                break;
            default:
                g.FillRectangle(white, t); g.DrawRectangle(edge, t);
                break;
        }
    }

    public static int Run(string[] args)
    {
        if (args.Length == 0) { Console.Error.WriteLine("usage: --shapes <out.png> [filter]"); return 1; }
        var cells = Cells.Where(c => args.Length < 2 || c.Caption.Contains(args[1], StringComparison.OrdinalIgnoreCase)).ToArray();
        int rows = (cells.Length + Cols - 1) / Cols;
        int w = Cols * CellW + (Cols + 1) * Gap, h = rows * (CellH + 22) + (rows + 1) * Gap;
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        Theme.Quality(g);
        g.Clear(Color.FromArgb(18, 20, 26));
        // the cells sit where a monitor is, so the layout's "stay on screen" sees room around them
        var mon = Native.MonitorRectAt(new PointF(0, 0));
        for (int i = 0; i < cells.Length; i++)
        {
            var c = cells[i];
            int ox = Gap + i % Cols * (CellW + Gap), oy = Gap + i / Cols * (CellH + 22 + Gap);
            using (var cap = new SolidBrush(Color.FromArgb(181, 186, 200)))
                g.DrawString(c.Caption, Theme.Font(12), cap, ox, oy);
            int cy = oy + 22;
            using (var bg = new SolidBrush(Color.FromArgb(236, 238, 242))) g.FillRectangle(bg, ox, cy, CellW, CellH);
            // filler content, so a curve has something to go round
            using (var line = new SolidBrush(Color.FromArgb(213, 217, 224)))
            {
                g.FillRectangle(line, ox + 20, cy + 20, 150, 8);
                g.FillRectangle(line, ox + 20, cy + 38, 190, 8);
                g.FillRectangle(line, ox + 20, cy + 56, 120, 8);
            }
            var t = c.Target; t.Offset(ox, cy);
            DrawUi(g, c.Ui, t, ox, cy);

            var sc = new Scene { Pulse = 0.5f };
            if (c.Ui == "group")
            {
                // three checkboxes of one step under one outline (what the engine draws for targets sharing "group")
                var box = RectangleF.Union(t, new RectangleF(t.X, t.Y + 68, 16, 16));
                box = RectangleF.Union(box, new RectangleF(t.X + 12, t.Y, 100, 16));
                box = RectangleF.FromLTRB(box.Left - 8, box.Top - 16, box.Right + 8, box.Bottom + 8);
                sc.Prims.Add((new Prim { Kind = PrimKind.Hull, Rect = box, Radius = 12, Width = 2, Scale = 1, Size = 12, Color = Palette.Accent, Text = "все три" }, 1f));
            }
            else if (c.Ui == "offscreen")
                sc.Prims.Add((Engine.EdgeTabFor(new PointF(ox + 500, cy + 120), new RectangleF(ox, cy, 330, CellH), 1) is var tab
                    ? new Prim { Kind = tab.Kind, Rect = tab.Rect, B = tab.B, Vertical = tab.Vertical, Color = tab.Color, Scale = 1, Size = 12, Text = "за краем экрана" } : null!, 1f));
            else
            {
                var m = Mark.Parse(A.Json($$"""{"anchor": {"screen": true}, "shapes": {{c.Shapes}}}"""), null, "g" + i);
                var ag = new AnchorGeo { Bounds = t, P = new PointF(t.X + t.Width / 2f, t.Y + t.Height / 2f), Scale = 1 };
                foreach (var p in Layout.Build(m, ag, false).Prims) sc.Prims.Add((p, 1f));
            }
            var clip = g.Clip;
            g.SetClip(new Rectangle(ox, cy, CellW, CellH));
            Renderer.Draw(g, sc, new RectangleF(ox, cy, CellW, CellH));
            g.Clip = clip;
        }
        bmp.Save(args[0], ImageFormat.Png);
        Console.WriteLine($"{cells.Length} shapes → {args[0]} (monitor {mon})");
        return 0;
    }
}
