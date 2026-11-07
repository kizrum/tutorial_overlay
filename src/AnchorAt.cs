using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows.Automation;

namespace TutorialOverlay;

/// <summary>
/// anchor_at: the agent points at a spot on the picture it got from inspect — a dim caption, a vertical mixer name, a
/// number in a box, anything without a number — and gets a checked anchor for it, so it never measures pixels or
/// guesses a template. Tried in order: the named UIA element under the point; the text under it (when the text search
/// will read it there); a template cut around it, made bigger until it occurs once in the window; else an offset
/// from the window or panel, said to be fragile. Each anchor is resolved before it is handed out.
/// </summary>
internal static class AnchorAt
{
    /// <summary>Template sides tried, smallest first: a bigger piece takes in more surroundings and is more often unique.</summary>
    private static readonly int[] TemplateSides = { 40, 64, 96 };
    /// <summary>The runner-up match must be this much worse than the template's own spot for the template to count as unique.</summary>
    private const float UniqueMargin = 0.03f;

    public static (JsonObject info, List<byte[]> pngs) Run(JsonNode args)
    {
        JsonNode? anchorJson = J.Unwrap(J.Get(args, "anchor"));
        if (anchorJson == null && J.Str(args, "app") is string app) anchorJson = new JsonObject { ["process"] = app };
        if (anchorJson == null && Engine.Tutorial is { Active: true, Defaults.Process: string proc }) anchorJson = new JsonObject { ["process"] = proc };
        if (anchorJson is not JsonObject baseJson) throw new ArgumentException("app (or anchor) is required, e.g. {app:'FL64', at:[x,y]}");
        var a = AnchorSpec.Parse(baseJson, Engine.Tutorial?.Defaults).WithoutFind();
        var r = Resolver.ResolveNow(a);
        if (!r.Found) throw new InvalidOperationException($"anchor not found: {r.Reason} ({a.Describe()})");
        if (Native.IsIconic(r.Root)) throw new InvalidOperationException("the window is minimized");
        var frame = Native.FrameRect(r.Root);
        var process = a.Process ?? Engine.ProcName(Native.ProcessId(r.Root));

        // the points: on the last inspect picture of this app (default), or screen pixels
        var raw = J.Get(args, "at") as JsonArray ?? throw new ArgumentException("at: [x,y] or [[x,y], …] is required");
        var pts = raw.Count >= 2 && raw[0] is JsonValue ? new List<JsonArray> { raw } : raw.OfType<JsonArray>().ToList();
        bool screen = (J.Str(args, "space") ?? "image").Equals("screen", StringComparison.OrdinalIgnoreCase);
        (Rectangle area, float scale) pic = default;
        if (!screen && !Inspect.Pictures.TryGetValue(process.ToLowerInvariant(), out pic))
            throw new InvalidOperationException($"no inspect picture of {process} yet: call inspect(app:'{process}') first, or pass space:'screen' with screen pixels");

        var baseAnchor = (JsonObject)baseJson.DeepClone();
        foreach (var k in new[] { "offset", "origin", "find" }) baseAnchor.Remove(k);
        baseAnchor["process"] ??= process;
        var basePath = J.Items(J.Unwrap(J.Get(baseAnchor, "element"))).Select(n => n.DeepClone()).ToList();

        // the UIA tree under the anchor once, for elements under the points and for panels to look inside
        var nodes = new List<Inspect.Node>();
        try
        {
            AutomationElement? root = r.Rel == null ? AutomationElement.FromHandle(r.Root) : Resolver.FindPath(r.Root, a.Path, out _);
            if (root != null) Resolver.Walk(root, Resolver.NewCacheRequest(), 30, 4000, (_, info, d) => { nodes.Add(new Inspect.Node(info, d)); return true; });
        }
        catch (Exception e) when (Resolver.IsGone(e) || e is InvalidOperationException) { }

        var bounds = Rectangle.Intersect(frame, Native.VirtualScreen());
        using var shot = ScreenCapture.CaptureWindow(r.Root, bounds, maxAgeMs: 0) ?? throw new InvalidOperationException("could not capture the window");
        var gray = Template.GrayFull(shot);
        var results = new JsonArray();
        var pngs = new List<byte[]>();
        foreach (var pa in pts)
        {
            float x = (float)(J.AsNum(pa[0]) ?? 0), y = (float)(J.AsNum(pa[1]) ?? 0);
            var p = screen ? new PointF(x, y) : new PointF(pic.area.X + x / pic.scale, pic.area.Y + y / pic.scale);
            var o = new JsonObject { ["at"] = pa.DeepClone(), ["point"] = J.PtJson(p) };
            try
            {
                One(p, o, baseAnchor, basePath, nodes, r.Root, frame, bounds, shot);
                o["shape"] = ShapeFor(o, p, gray, shot.Width, shot.Height, bounds);
            }
            catch (Exception e) { o["error"] = e.Message; }
            results.Add(o);
            pngs.Add(Around(shot, bounds, p));
        }
        return (new JsonObject
        {
            ["points"] = results,
            ["note"] = "use each point's anchor as is; leave shapes out (or {type:'auto'}): the target is framed at its own size. how: element / text (robust) · template (found by its look) · offset (fragile). The images show each point, a cross on the spot.",
        }, pngs);
    }

    private static void One(PointF p, JsonObject o, JsonObject baseAnchor, List<JsonNode> basePath, List<Inspect.Node> nodes,
                            IntPtr root, Rectangle frame, Rectangle bounds, Bitmap shot)
    {
        if (!bounds.Contains(Point.Round(p))) throw new ArgumentException("the point is outside the window");
        float winArea = (float)frame.Width * frame.Height;
        JsonObject WithElement(JsonObject seg)
        {
            var an = (JsonObject)baseAnchor.DeepClone();
            an["element"] = basePath.Count == 0 ? seg.DeepClone() : new JsonArray(basePath.Select(n => n.DeepClone()).Append(seg.DeepClone()).ToArray());
            return an;
        }

        // 1) the named UIA element under the point, if it is a control and not a whole panel
        int best = -1;
        for (int i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i].Info;
            if (n.Rect is not RectangleF nr || n.Offscreen || !nr.Contains(p)) continue;
            if (n.Name.Trim().Length == 0 && !Inspect.StableId(n.AutomationId)) continue;
            if (nr.Width * nr.Height > 0.05f * winArea) continue;
            if (best < 0 || nr.Width * nr.Height < nodes[best].Info.Rect!.Value.Width * nodes[best].Info.Rect!.Value.Height) best = i;
        }
        if (best >= 0)
        {
            var an = WithElement(Inspect.Disambiguate(nodes, best));
            var rr = Resolver.ResolveNow(AnchorSpec.Parse(an, null));
            if (rr.Found && rr.Rel is RectangleF rel && new RectangleF(frame.X + rel.X, frame.Y + rel.Y, rel.Width, rel.Height).Contains(p))
            {
                o["how"] = "element"; o["anchor"] = an; o["name"] = nodes[best].Info.Name;
                o["robust"] = "follows the element: window moves and layout changes alike";
                return;
            }
        }

        // 2) the text under the point — only when the text search reads it there as it is
        var cut = Rectangle.Intersect(Rectangle.Round(new RectangleF(p.X - 200 - bounds.X, p.Y - 40 - bounds.Y, 400, 80)), new Rectangle(0, 0, shot.Width, shot.Height));
        if (cut.Width > 8 && cut.Height > 8)
        {
            using var part = shot.Clone(cut, shot.PixelFormat);
            var lines = Ocr.Read(part, null, null, part.Width * (float)part.Height * 4);
            var local = new PointF(p.X - bounds.X - cut.X, p.Y - bounds.Y - cut.Y);
            var line = lines.FirstOrDefault(l => RectangleF.Inflate(l.Bounds, 4, 4).Contains(local));
            if (line != null && Inspect.Query(line.Text) is string q)
            {
                var lineScreen = new RectangleF(bounds.X + cut.X + line.Bounds.X, bounds.Y + cut.Y + line.Bounds.Y, line.Bounds.Width, line.Bounds.Height);
                int ci = Inspect.Container(nodes, lineScreen, frame);
                var an = ci >= 0 ? WithElement(Inspect.ContainerSegment(nodes, ci)) : (JsonObject)baseAnchor.DeepClone();
                var find = new JsonObject { ["text"] = q };
                if (q == line.Text.Trim()) find["match"] = "exact";
                an["find"] = find;
                var fr = Finder.Search(AnchorSpec.Parse(an, null), null);
                var hitFrame = fr.Root != IntPtr.Zero ? Native.FrameRect(fr.Root) : frame;
                if (fr.Found && RectangleF.Inflate(lineScreen, 6, 6).Contains(new PointF(hitFrame.X + fr.Rel.X, hitFrame.Y + fr.Rel.Y)))
                {
                    o["how"] = "text"; o["anchor"] = an; o["name"] = q;
                    o["robust"] = "follows the text: window moves, scrolling, layout changes";
                    return;
                }
            }
        }

        // 3) a template around the point, bigger until it occurs once in the window
        var tried = new JsonArray();
        o["templatesTried"] = tried;
        foreach (int side in TemplateSides)
        {
            var box = Rectangle.Intersect(Rectangle.Round(new RectangleF(p.X - bounds.X - side / 2f, p.Y - bounds.Y - side / 2f, side, side)),
                                          new Rectangle(0, 0, shot.Width, shot.Height));
            if (box.Width < 12 || box.Height < 12) continue;
            using var piece = shot.Clone(box, shot.PixelFormat);
            var tpl = Template.From(piece);
            var ranked = Finder.MatchRanked(shot, tpl, 40);
            if (ranked.Count == 0) continue;
            int own = ranked.FindIndex(c => Math.Abs(c.x - box.X) <= 3 && Math.Abs(c.y - box.Y) <= 3 && c.score <= 0.02f);
            if (own < 0) continue;
            var others = ranked.Where((c, i) => i != own).ToList();
            float runnerUp = others.Select(c => c.score).DefaultIfEmpty(1f).Min();
            tried.Add(new JsonObject { ["side"] = side, ["box"] = J.RectJson(box), ["runnerUp"] = Math.Round(runnerUp, 3) });
            int nth = 0;
            if (runnerUp < ranked[own].score + UniqueMargin)
            {
                // look-alikes (identical mixer strips): the n-th of them in reading order — when they are told apart
                // clearly from everything else (no near-miss between "the same" and "different")
                const float same = 0.08f;
                if (others.Any(c => c.score > same && c.score < same + UniqueMargin)) continue;
                var alike = ranked.Where(c => c.score <= same).OrderBy(c => Math.Round(c.y / 8.0)).ThenBy(c => c.x).ToList();
                nth = alike.FindIndex(c => Math.Abs(c.x - box.X) <= 3 && Math.Abs(c.y - box.Y) <= 3);
                if (nth < 0 || alike.Count > 32) continue;
            }
            var id = "auto-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(tpl.Full))[..12].ToLowerInvariant();
            if (!TemplateStore.Exists(id)) TemplateStore.Put(id, tpl, ScreenCapture.Png(piece, 0, out _));
            var centre = new PointF(bounds.X + box.X + box.Width / 2f, bounds.Y + box.Y + box.Height / 2f);
            var an = (JsonObject)baseAnchor.DeepClone();
            an["find"] = nth > 0 ? new JsonObject { ["template"] = id, ["index"] = nth } : new JsonObject { ["template"] = id };
            float dx = p.X - centre.X, dy = p.Y - centre.Y;
            if (Math.Abs(dx) >= 1 || Math.Abs(dy) >= 1) an["offset"] = new JsonArray(Math.Round(dx), Math.Round(dy));
            var fr = Finder.Search(AnchorSpec.Parse(an, null), null);
            if (!fr.Found) { ((JsonObject)tried[^1]!)["verify"] = "not found"; continue; }
            var hit = new PointF(frame.X + fr.Rel.X + dx, frame.Y + fr.Rel.Y + dy);
            if (Math.Abs(hit.X - p.X) > 4 || Math.Abs(hit.Y - p.Y) > 4) { ((JsonObject)tried[^1]!)["verify"] = $"found at {hit}"; continue; }
            o["how"] = "template"; o["anchor"] = an; o["template"] = id; o["templateSize"] = $"{box.Width}x{box.Height}";
            o["robust"] = nth > 0
                ? $"look-alike {nth + 1} in reading order (identical neighbours): follows moves, breaks if look-alikes are added or removed before it"
                : "found by its look anywhere in the window (moves, layout changes); breaks if it is redrawn differently (theme, hover, a value inside)";
            return;
        }

        // 4) nothing distinctive: an offset from the panel around it, or the window
        int pc = Inspect.Container(nodes, new RectangleF(p.X - 2, p.Y - 2, 4, 4), frame);
        var fallback = pc >= 0 ? WithElement(Inspect.ContainerSegment(nodes, pc)) : (JsonObject)baseAnchor.DeepClone();
        var origin = pc >= 0 ? nodes[pc].Info.Rect!.Value.Location : (PointF)frame.Location;
        fallback["offset"] = new JsonArray(Math.Round(p.X - origin.X), Math.Round(p.Y - origin.Y));
        o["how"] = "offset"; o["anchor"] = fallback;
        o["robust"] = "fragile: a fixed offset from " + (pc >= 0 ? $"the panel \"{nodes[pc].Info.Name}\"" : "the window's corner") + " — breaks if the layout inside moves; prefer pick(anchor) to confirm";
    }

    /// <summary>
    /// The shape that marks the target at its own size: an element or a found text is framed as the anchor finds it
    /// (no size given); a spot (a template or an offset) gets a rectangle of the visible thing under the point — the
    /// letters of a vertical mixer name, a button's frame — measured from the picture, with a lower contrast tried for
    /// dim text. Coordinates are relative to the anchor point, which is the pointed spot.
    /// </summary>
    private static JsonObject ShapeFor(JsonObject o, PointF p, byte[] gray, int w, int h, Rectangle bounds)
    {
        // "auto": the overlay frames the target at its own size, measured live (it follows a size change too);
        // the box measured now is given for the agent's information
        var local = new Point((int)Math.Round(p.X - bounds.X), (int)Math.Round(p.Y - bounds.Y));
        var box = Icons.TargetBox(gray, w, h, local) ?? Icons.TargetBox(gray, w, h, local, contrast: 15);
        if (box is Rectangle b) o["box"] = J.RectJson(new RectangleF(bounds.X + b.X, bounds.Y + b.Y, b.Width, b.Height));
        return new JsonObject { ["type"] = "auto" };
    }

    /// <summary>A 120×120 piece of the window around the point, a cross on it: the agent sees what it pointed at.</summary>
    private static byte[] Around(Bitmap shot, Rectangle bounds, PointF p)
    {
        var box = Rectangle.Intersect(new Rectangle((int)p.X - bounds.X - 60, (int)p.Y - bounds.Y - 60, 120, 120), new Rectangle(0, 0, shot.Width, shot.Height));
        if (box.Width < 4 || box.Height < 4) box = new Rectangle(0, 0, Math.Min(120, shot.Width), Math.Min(120, shot.Height));
        using var piece = shot.Clone(box, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(piece))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float cx = p.X - bounds.X - box.X, cy = p.Y - bounds.Y - box.Y;
            using var outline = new Pen(Color.Black, 3);
            using var pen = new Pen(Color.FromArgb(235, 40, 130), 1.5f);
            foreach (var pn in new[] { outline, pen })
            {
                g.DrawLine(pn, cx - 9, cy, cx - 3, cy); g.DrawLine(pn, cx + 3, cy, cx + 9, cy);
                g.DrawLine(pn, cx, cy - 9, cx, cy - 3); g.DrawLine(pn, cx, cy + 3, cx, cy + 9);
            }
        }
        using var ms = new MemoryStream();
        piece.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }
}
