using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows.Automation;

namespace TutorialOverlay;

/// <summary>
/// inspect(anchor): one look instead of describe → screenshot → find_text → guessing offsets. The image comes back
/// with numbered frames on the UIA elements (and, for self-drawn UIs, on the text OCR reads), and every number
/// carries an anchor that resolves to exactly that element — the agent picks numbers instead of measuring pixels.
/// </summary>
internal static class Inspect
{
    /// <summary>A safety stop only: everything that passes the visibility checks gets a number.</summary>
    public const int DefaultMaxItems = 400;
    /// <summary>Fewer UIA buttons / captions than this in the area: the UI is probably self-drawn, so OCR is added (ocr: auto).</summary>
    private const int OcrBelow = 8;

    internal sealed record Node(Resolver.ElInfo Info, int Depth);

    /// <summary>El: a text item's container element (its find runs inside it); Exact: the text is the whole line.</summary>
    private sealed record Item(int N, bool Text, string Role, string Name, RectangleF Rect, JsonObject Anchor, JsonObject? El = null, bool Exact = false);

    private static readonly HashSet<string> Interactive = new(StringComparer.OrdinalIgnoreCase)
    {
        "Button", "SplitButton", "MenuItem", "TabItem", "CheckBox", "RadioButton", "ComboBox", "Edit", "ListItem",
        "TreeItem", "Hyperlink", "Slider", "Spinner", "DataItem", "HeaderItem", "Thumb",
    };

    /// <summary>0 = something to click or type into, 1 = a caption or picture, 2 = a container.</summary>
    internal static int Priority(string role) => Interactive.Contains(role) ? 0 : role is "Text" or "Image" ? 1 : 2;

    public static (JsonObject info, byte[]? png) Run(JsonNode args)
    {
        var total = Stopwatch.StartNew();
        JsonNode? anchorJson = J.Unwrap(J.Get(args, "anchor"));
        if (anchorJson == null && J.Str(args, "app") is string app) anchorJson = new JsonObject { ["process"] = app };
        if (anchorJson == null && Engine.Tutorial is { Active: true, Defaults.Process: string proc }) anchorJson = new JsonObject { ["process"] = proc };
        if (anchorJson is not JsonObject baseJson) throw new ArgumentException("anchor (or app) is required, e.g. {process:'notepad'}");
        var a = AnchorSpec.Parse(baseJson, Engine.Tutorial?.Defaults).WithoutFind();
        if (a.IsScreen) throw new ArgumentException("inspect needs a window anchor (process / window / element)");

        var r = Resolver.ResolveNow(a);
        if (!r.Found) throw new InvalidOperationException($"anchor not found: {r.Reason} ({a.Describe()})");
        if (Native.IsIconic(r.Root)) throw new InvalidOperationException("the anchor's window is minimized");
        var frame = Native.FrameRect(r.Root);
        RectangleF bounds = r.Rel is RectangleF rel ? new RectangleF(frame.X + rel.X, frame.Y + rel.Y, rel.Width, rel.Height)
            : a.Client ? Native.ClientScreenRect(r.Root) : frame;
        var area = Rectangle.Intersect(Rectangle.Round(bounds), Native.VirtualScreen());
        if (J.Arr(args, "within") is JsonArray wa && wa.Count >= 4)
            area = Rectangle.Intersect(area, Rectangle.Round(new RectangleF(bounds.X + (float)(J.AsNum(wa[0]) ?? 0), bounds.Y + (float)(J.AsNum(wa[1]) ?? 0),
                                                                            (float)(J.AsNum(wa[2]) ?? 0), (float)(J.AsNum(wa[3]) ?? 0))));
        if (area.Width < 4 || area.Height < 4) throw new InvalidOperationException("area is empty or off screen");

        // the anchor every suggestion starts from: the given one, without its own point / find, with its process
        var baseAnchor = (JsonObject)baseJson.DeepClone();
        foreach (var k in new[] { "offset", "origin", "find" }) baseAnchor.Remove(k);
        if (!baseAnchor.ContainsKey("process") && Engine.ProcName(Native.ProcessId(r.Root)) is { Length: > 0 } pn) baseAnchor["process"] = pn;
        if (a.Title != null && r.Rel == null)
        {
            // "title" matched a top-level window; with an element path it would mean a pane of that name instead
            baseAnchor.Remove("title");
            baseAnchor["window"] = a.Title;
        }
        var basePath = J.Items(J.Unwrap(J.Get(baseAnchor, "element"))).Select(n => n.DeepClone()).ToList();

        // ---- UIA: the subtree under the anchor, in the order FindAll returns it (indexes depend on it)
        var uiaTime = Stopwatch.StartNew();
        var nodes = new List<Node>();
        int depth = Math.Clamp(J.Int(args, "depth") ?? 30, 1, 60), maxNodes = Math.Clamp(J.Int(args, "maxNodes") ?? 4000, 50, 20000);
        int walked = 0;
        AutomationElement? root = null;
        try
        {
            if (r.Rel == null) root = AutomationElement.FromHandle(r.Root);
            else
            {
                var path = new List<ElementSpec>();
                if (a.Title != null) path.Add(new ElementSpec { Name = a.Title, Role = "Window|Pane" });
                path.AddRange(a.Path);
                root = Resolver.FindPath(r.Root, path, out _);
            }
        }
        catch (Exception e) when (Resolver.IsGone(e) || e is InvalidOperationException) { }
        if (root != null)
            walked = Resolver.Walk(root, Resolver.NewCacheRequest(), depth, maxNodes, (_, info, d) => { nodes.Add(new Node(info, d)); return true; });
        long uiaMs = uiaTime.ElapsedMilliseconds;

        // ---- the picture: the window's own pixels (no overlay drawings, works while covered); else the screen without our windows
        Bitmap? shot = ScreenCapture.CaptureWindow(r.Root, area, maxAgeMs: 0);
        if (shot == null)
        {
            bool flip = !Settings.Global.HideFromCapture;
            if (flip) { Program.SetCapturable(false); Thread.Sleep(80); }
            try { shot = ScreenCapture.Capture(area); }
            finally { if (flip) Program.SetCapturable(true); }
        }

        int maxItems = Math.Clamp(J.Int(args, "maxItems") ?? DefaultMaxItems, 1, 1000);
        // UIA calls hover-only buttons visible while they draw nothing: the pixels decide
        var gray = Template.GrayFull(shot);
        int sw = shot.Width, sh = shot.Height;
        var choice = Choose(nodes, area, maxItems, rr => IsBlankElement(gray, sw, sh, Rectangle.Round(new RectangleF(rr.X - area.X, rr.Y - area.Y, rr.Width, rr.Height))));
        var items = new List<Item>();
        foreach (var i in choice.Picked)
        {
            var info = nodes[i].Info;
            var anchor = (JsonObject)baseAnchor.DeepClone();
            var seg = Disambiguate(nodes, i);
            if (basePath.Count == 0) anchor["element"] = seg;
            else anchor["element"] = new JsonArray(basePath.Select(n => n.DeepClone()).Append(seg).ToArray());
            items.Add(new Item(items.Count + 1, false, info.Role, info.Name.Length > 0 ? info.Name : "#" + info.AutomationId, info.Rect!.Value, anchor));
        }

        long ocrMs = 0;
        int texts = 0, repeated = 0;
        bool debug = J.Bool(args, "debug") ?? false;
        bool within = J.Has(args, "within");
        var skipped = new JsonArray();
        using (shot)
        {
            // ---- OCR for what UIA does not name (FL Studio, games, plugin windows)
            // auto: when UIA names next to nothing to click or read (only panes) the UI is drawn by the app itself
            bool ocr = J.Bool(args, "ocr") ?? items.Count(it => Priority(it.Role) < 2) < OcrBelow;
            if (ocr && items.Count < maxItems)
            {
                var ocrTime = Stopwatch.StartNew();
                string? lang = J.Str(args, "lang");
                var lines = Ocr.ReadTiled(shot, lang, (float)Math.Clamp(J.Num(args, "ocrScale") ?? 1.0, 0.5, 2.0));
                // the finder's own read of an element (Finder.Search: that element's pixels, the query's language,
                // 1.5 Mpx), made once per element and language; rectangles relative to the shot
                var finderReads = new Dictionary<(int, string), (List<OcrLine> lines, Rectangle crop)>();
                (List<OcrLine> lines, Rectangle crop) FinderRead(int scopeIdx, string query)
                {
                    var key = (scopeIdx, lang ?? Ocr.AutoLang(query) ?? "");
                    if (finderReads.TryGetValue(key, out var done)) return done;
                    var sr = scopeIdx >= 0 ? RectangleF.Intersect(nodes[scopeIdx].Info.Rect!.Value, area) : (RectangleF)area;
                    var crop = Rectangle.Intersect(Rectangle.Round(new RectangleF(sr.X - area.X, sr.Y - area.Y, sr.Width, sr.Height)), new Rectangle(0, 0, shot.Width, shot.Height));
                    var read = new List<OcrLine>();
                    if (crop.Width >= 4 && crop.Height >= 4)
                    {
                        using var part = shot.Clone(crop, shot.PixelFormat);
                        read = Ocr.Read(part, lang, query, 1_500_000f);
                    }
                    return finderReads[key] = (read, crop);
                }
                foreach (var line in lines)
                {
                    void Skip(string why) { if (debug) skipped.Add(new JsonObject { ["text"] = line.Text, ["why"] = why }); }
                    if (items.Count >= maxItems) break;
                    var lb = line.Bounds;
                    var screen = new RectangleF(area.X + lb.X, area.Y + lb.Y, lb.Width, lb.Height);
                    var c = new PointF(screen.X + screen.Width / 2, screen.Y + screen.Height / 2);
                    // already named by UIA: the number on the element is enough
                    if (items.Any(it => !it.Text && it.Rect.Contains(c) && SameWords(it.Name, line.Text))) { Skip("named by uia"); continue; }
                    var query = Query(line.Text);
                    if (query == null) { Skip("no words"); continue; }
                    // where the anchor's text search will run: the smallest named UIA element around the line (a panel —
                    // "808 Kick" is in the browser and in the channel rack, but once in each), else the whole area.
                    // Each try is checked against the very read the finder will make there (same pixels, same
                    // language, same size budget): a number is given only to an anchor that finds this line again.
                    int ci = within ? -1 : Container(nodes, screen, area);
                    bool whole = query == line.Text.Trim();   // compared whole, "Line" is not found inside "Bassline"
                    string why = "";
                    bool Try(int scopeIdx, string mode, string query)
                    {
                        var (vl, crop) = FinderRead(scopeIdx, query);
                        var found = Ocr.Find(vl, query, mode);
                        string where = scopeIdx >= 0 ? nodes[scopeIdx].Info.Name : "the window";
                        if (found.Count != 1) { why += $" {found.Count}× in {where} ({mode});"; return false; }
                        var m = found[0].Rect;
                        var hit = new RectangleF(crop.X + m.X, crop.Y + m.Y, m.Width, m.Height);
                        var near = lb;
                        near.Inflate(6, 6);
                        if (!near.Contains(new PointF(hit.X + hit.Width / 2, hit.Y + hit.Height / 2))) { why += $" in {where} it finds \"{found[0].Text}\" elsewhere;"; return false; }
                        var anchor = (JsonObject)baseAnchor.DeepClone();
                        JsonObject? el = null;
                        if (scopeIdx >= 0)
                        {
                            el = Disambiguate(nodes, scopeIdx);
                            anchor["element"] = basePath.Count == 0 ? el.DeepClone() : new JsonArray(basePath.Select(n => n.DeepClone()).Append(el.DeepClone()).ToArray());
                        }
                        var find = new JsonObject { ["text"] = query };
                        if (mode == "exact") find["match"] = "exact";
                        if (lang != null) find["lang"] = lang;
                        if (J.Get(args, "within") is JsonNode wn) find["within"] = wn.DeepClone();
                        anchor["find"] = find;
                        // the frame shows what the anchor finds
                        items.Add(new Item(items.Count + 1, true, "text", query, new RectangleF(area.X + hit.X, area.Y + hit.Y, hit.Width, hit.Height), anchor, el, mode == "exact"));
                        return true;
                    }
                    // the finder's own reading of this same line, if it differs ("808 Kick" where the overview read "808 ЮсК")
                    (string q, bool whole)? Theirs(int scopeIdx)
                    {
                        var (vl, crop) = FinderRead(scopeIdx, query);
                        var mine = new RectangleF(lb.X - crop.X, lb.Y - crop.Y, lb.Width, lb.Height);
                        var t = vl.FirstOrDefault(l => { var x = RectangleF.Intersect(l.Bounds, mine); return x.Width * x.Height > 0.5f * Math.Min(mine.Width * mine.Height, l.Bounds.Width * l.Bounds.Height); });
                        var tq = t == null ? null : Query(t.Text);
                        return tq == null || tq == query ? null : (tq, tq == t!.Text.Trim());
                    }
                    bool TryAll(int scopeIdx) =>
                        (whole && Try(scopeIdx, "exact", query)) || Try(scopeIdx, "contains", query)
                        || (Theirs(scopeIdx) is var (tq, tw) && ((tw && Try(scopeIdx, "exact", tq)) || Try(scopeIdx, "contains", tq)));
                    bool ok = (ci >= 0 && TryAll(ci)) || TryAll(-1);
                    if (!ok) { repeated++; Skip("the text search would not find it here:" + why); continue; }
                    texts++;
                }
                ocrMs = ocrTime.ElapsedMilliseconds;
            }

            int maxEdge = J.Int(args, "maxEdge") ?? 1024;
            var png = Annotate(shot, area, items, maxEdge, out float scale);
            // every item's anchor is the shared one plus {name, role} (or find:{text}); only what differs is listed,
            // so numbering everything stays affordable. A nested base path or within: each item carries its own anchor.
            bool composite = basePath.Count > 0 || J.Has(args, "within");
            var list = new JsonArray();
            foreach (var it in items)
            {
                var o = new JsonObject { ["n"] = it.N, ["role"] = it.Role, ["name"] = it.Name, ["rect"] = J.RectJson(it.Rect) };
                if (composite) o["anchor"] = it.Anchor;
                else if (it.Text)
                {
                    if (it.El != null) o["el"] = it.El.DeepClone();
                    if (it.Exact) o["exact"] = true;
                }
                else if (it.Anchor["element"] is JsonObject seg
                         && !(seg.Count == 2 && J.Str(seg, "name") == it.Name && J.Str(seg, "role") == it.Role))
                    o["el"] = seg.DeepClone();
                list.Add(o);
            }
            var info = new JsonObject
            {
                ["screenRect"] = J.RectJson(area),
                ["imageScale"] = Math.Round(scale, 4),
                ["anchor"] = composite ? null : baseAnchor,
                ["items"] = list,
                ["note"] = (composite ? "each item's anchor rings/frames it as is. "
                                      : "item anchor = {...anchor, element: el ?? {name, role}}; text item = {...anchor, element: el (if any), find:{text: name, match:'exact' if exact}}. ") +
                           "It rings/frames the item as is (offset for a point inside); text anchors follow scrolling. " +
                           "Numbers are what could be verified, not the whole UI: no number on your target → describe / find_text / pick, never the nearest number. validate before start_tutorial.",
                ["ms"] = new JsonObject { ["uia"] = uiaMs, ["ocr"] = ocr ? (JsonNode)ocrMs : null, ["total"] = total.ElapsedMilliseconds },
            };
            if (items.Count == 0) info["note"] = "nothing named here; try ocr:true, a smaller anchor, or pick(anchor)";
            if (choice.Blank > 0)
                info["blank"] = $"{choice.Blank} UIA elements draw nothing here right now (buttons that show on hover, empty slots): not numbered";
            if (repeated > 0)
                info["unverifiedText"] = $"{repeated} OCR lines the text search would not find back as they are (repeated, or read differently up close): not numbered — debug:true lists them";
            if (choice.Overlapping > 0)
                info["overlapping"] = $"{choice.Overlapping} UIA elements are covered by look-alikes drawn over them: not numbered";
            if (choice.Omitted.Count > 0)
                info["omitted"] = new JsonObject
                {
                    ["count"] = choice.Omitted.Values.Sum(),
                    ["roles"] = new JsonObject(choice.Omitted.OrderByDescending(kv => kv.Value).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
                    ["hint"] = $"more than maxItems ({maxItems}): inspect a smaller element (or within) to number them",
                };
            if (debug) info["skippedText"] = skipped;
            if (walked >= maxNodes) info["truncated"] = $"UIA walk stopped at {maxNodes} nodes: indexes of look-alike elements may be off, validate them";
            if (scale < 0.6f) info["hint"] = "image strongly reduced: inspect a smaller element (or within) to read small text";
            Log.File($"inspect: {nodes.Count} nodes, {items.Count - texts} uia + {texts} text items, uia {uiaMs} ms, ocr {ocrMs} ms");
            return (info, png);
        }
    }

    /// <summary>What gets numbered (reading order); how many were blank; what did not fit into max, by role.</summary>
    internal sealed record Choice(List<int> Picked, int Blank, Dictionary<string, int> Omitted, int Overlapping = 0);

    /// <summary>
    /// Which walked nodes get a number: named, visible, mostly inside the area, drawing something (<paramref name="blank"/>);
    /// containers only when small; one number per element (a button wins over its own caption and over a same-named
    /// wrapper). Over <paramref name="max"/>: buttons before captions before panels, then reading order — and the rest
    /// is reported, never dropped silently.
    /// </summary>
    internal static Choice Choose(IReadOnlyList<Node> nodes, RectangleF area, int max, Func<RectangleF, bool>? blank = null)
    {
        float areaSize = area.Width * area.Height;
        var cands = new List<int>();
        for (int i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i].Info;
            if (n.Rect is not RectangleF r || n.Offscreen || r.Width < 4 || r.Height < 4) continue;
            if (n.Name.Trim().Length == 0 && !StableId(n.AutomationId)) continue;
            if (n.Role is "Window" or "TitleBar") continue;
            var c = new PointF(r.X + r.Width / 2, r.Y + r.Height / 2);
            if (!area.Contains(c)) continue;
            var inter = RectangleF.Intersect(r, area);
            if (inter.Width * inter.Height < 0.5f * r.Width * r.Height) continue;
            if (Priority(n.Role) == 2 && r.Width * r.Height > 0.25f * areaSize) continue;
            cands.Add(i);
        }
        int blanks = 0;
        if (blank != null)
            cands = cands.Where(i => { bool b = blank(nodes[i].Info.Rect!.Value); if (b) blanks++; return !b; }).ToList();
        RectangleF R(int i) => nodes[i].Info.Rect!.Value;
        string Role(int i) => nodes[i].Info.Role;
        var kept = new List<int>();
        foreach (var i in cands.OrderBy(i => Priority(Role(i))).ThenBy(i => R(i).Width * R(i).Height))
        {
            var r = R(i);
            if (kept.Any(p => Near(R(p), r) || (Nested(R(p), r) && SameWords(nodes[p].Info.Name, nodes[i].Info.Name)))) continue;
            kept.Add(i);
        }
        // a clickable element around two or more others is a wrapper (VS Code's "Click to expand" over a whole message):
        // its number would frame a region, not a control
        kept.RemoveAll(i => Priority(Role(i)) == 0 && kept.Count(j => j != i && Inside(R(i), R(j))) >= 2);
        // look-alikes on top of each other (rows under VS Code's sticky tree header): the one later in the tree is
        // painted over the earlier (document order in Chromium), so the covered, earlier one gets no number
        var overlapping = kept.Where(i => kept.Any(j => j > i && Role(j) == Role(i) && Overlap(R(i), R(j)) > 0.4f)).ToHashSet();
        kept.RemoveAll(overlapping.Contains);

        // over max: buttons before captions before panels, and the numbers spread over the area (3×3 cells taking
        // turns) instead of the top-left corner using them all up
        float Row(int i) => (float)Math.Round(R(i).Y / 12);
        int Cell(int i)
        {
            var c = new PointF(R(i).X + R(i).Width / 2, R(i).Y + R(i).Height / 2);
            int cx = Math.Clamp((int)((c.X - area.X) * 3 / area.Width), 0, 2), cy = Math.Clamp((int)((c.Y - area.Y) * 3 / area.Height), 0, 2);
            return cy * 3 + cx;
        }
        var ranked = new List<int>();
        foreach (var tier in kept.GroupBy(i => Priority(Role(i))).OrderBy(g => g.Key))
        {
            var queues = tier.GroupBy(Cell).OrderBy(g => g.Key).Select(g => new Queue<int>(g.OrderBy(Row).ThenBy(i => R(i).X))).ToList();
            while (queues.Any(q => q.Count > 0))
                foreach (var q in queues)
                    if (q.Count > 0) ranked.Add(q.Dequeue());
        }
        var omitted = new Dictionary<string, int>();
        foreach (var i in ranked.Skip(max)) omitted[Role(i)] = omitted.GetValueOrDefault(Role(i)) + 1;
        var picked = ranked.Take(max).OrderBy(Row).ThenBy(i => R(i).X).ToList();
        return new Choice(picked, blanks, omitted, overlapping.Count);
    }

    /// <summary><paramref name="inner"/> lies within <paramref name="outer"/> (2 px slack) and is smaller.</summary>
    private static bool Inside(RectangleF outer, RectangleF inner) =>
        inner.X >= outer.X - 2 && inner.Y >= outer.Y - 2 && inner.Right <= outer.Right + 2 && inner.Bottom <= outer.Bottom + 2
        && inner.Width * inner.Height < outer.Width * outer.Height;

    /// <summary>The shared part as a share of the smaller rectangle.</summary>
    private static float Overlap(RectangleF a, RectangleF b)
    {
        var x = RectangleF.Intersect(a, b);
        float small = Math.Min(a.Width * a.Height, b.Width * b.Height);
        return x.IsEmpty || small <= 0 ? 0 : x.Width * x.Height / small;
    }

    /// <summary>
    /// Nothing drawn where the element is. An icon-sized element (≤ 64 px) is judged by its middle half: a hover-only
    /// button's frame may graze a neighbour's text, but its icon would sit in the middle.
    /// </summary>
    internal static bool IsBlankElement(byte[] gray, int w, int h, Rectangle r)
    {
        if (r.Width <= 64 && r.Height <= 64) r.Inflate(-r.Width / 4, -r.Height / 4);
        return IsBlank(gray, w, h, r);
    }

    private static bool Near(RectangleF a, RectangleF b) =>
        Math.Abs(a.X - b.X) <= 3 && Math.Abs(a.Y - b.Y) <= 3 && Math.Abs(a.Right - b.Right) <= 3 && Math.Abs(a.Bottom - b.Bottom) <= 3;

    /// <summary>One rectangle inside the other (2 px slack): a wrapper and what it wraps.</summary>
    private static bool Nested(RectangleF a, RectangleF b)
    {
        static bool In(RectangleF o, RectangleF i) => i.X >= o.X - 2 && i.Y >= o.Y - 2 && i.Right <= o.Right + 2 && i.Bottom <= o.Bottom + 2;
        return In(a, b) || In(b, a);
    }

    /// <summary>
    /// The rectangle of a grayscale picture is (nearly) one flat colour: nothing is drawn there. Sampled on a grid
    /// of ≤ ~1600 points; anti-aliasing noise stays under the threshold, the faintest icon or caption does not.
    /// </summary>
    internal static bool IsBlank(byte[] gray, int w, int h, Rectangle r)
    {
        r = Rectangle.Intersect(r, new Rectangle(0, 0, w, h));
        if (r.Width < 2 || r.Height < 2) return false;   // outside the picture: nothing to judge by
        int step = Math.Max(1, (int)Math.Sqrt(r.Width * (double)r.Height / 1600));
        int lo = 255, hi = 0;
        for (int y = r.Y; y < r.Bottom; y += step)
            for (int x = r.X; x < r.Right; x += step)
            {
                int v = gray[y * w + x];
                if (v < lo) lo = v;
                if (v > hi) hi = v;
            }
        return hi - lo <= BlankRange;
    }

    private const int BlankRange = 10;

    /// <summary>
    /// The element segment that finds node <paramref name="target"/> among the walked nodes the way the resolver
    /// does: name + role; exact name, or automationId, when that is ambiguous; else the index among the look-alikes.
    /// </summary>
    internal static JsonObject Disambiguate(IReadOnlyList<Node> nodes, int target)
    {
        var t = nodes[target].Info;
        bool named = t.Name.Trim().Length > 0;
        List<int> Hits(ElementSpec s)
        {
            var res = new List<int>();
            for (int i = 0; i < nodes.Count; i++) if (Resolver.Matches(s, nodes[i].Info)) res.Add(i);
            return res;
        }
        var role = t.Role.Length > 0 ? t.Role : null;
        if (named)
        {
            var hits = Hits(new ElementSpec { Name = t.Name, Role = role });
            var seg = new JsonObject { ["name"] = t.Name, ["role"] = role };
            if (hits.Count <= 1) return seg;
            var exact = Hits(new ElementSpec { Name = t.Name, NameExact = true, Role = role });
            seg["match"] = "exact";
            if (exact.Count <= 1) return seg;
            if (StableId(t.AutomationId) && Hits(new ElementSpec { AutomationId = t.AutomationId, Role = role }).Count == 1)
                return new JsonObject { ["automationId"] = t.AutomationId, ["role"] = role };
            seg["index"] = Math.Max(0, exact.IndexOf(target));
            return seg;
        }
        var byId = Hits(new ElementSpec { AutomationId = t.AutomationId, Role = role });
        var idSeg = new JsonObject { ["automationId"] = t.AutomationId, ["role"] = role };
        if (byId.Count > 1) idSeg["index"] = Math.Max(0, byId.IndexOf(target));
        return idSeg;
    }

    /// <summary>
    /// The smallest named (or stably identified) visible UIA element that holds the text, short of the whole area:
    /// the text anchor's search runs inside it. -1: none.
    /// </summary>
    internal static int Container(IReadOnlyList<Node> nodes, RectangleF text, RectangleF area)
    {
        int best = -1;
        float bestSize = 0.9f * area.Width * area.Height;
        for (int i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i].Info;
            if (n.Rect is not RectangleF r || n.Offscreen || (n.Name.Trim().Length == 0 && !StableId(n.AutomationId))) continue;
            var inter = RectangleF.Intersect(r, text);
            if (inter.Width * inter.Height < 0.8f * text.Width * text.Height) continue;
            float size = r.Width * r.Height;
            // a little bigger than the text itself: a caption element would be its own search area, too tight to follow scrolling
            if (size < bestSize && size > 4 * text.Width * text.Height) { best = i; bestSize = size; }
        }
        return best;
    }

    /// <summary>
    /// An automationId that survives a restart. Win32 apps (FL Studio) report the window handle as a number there,
    /// different on every run: an anchor on it would break next time.
    /// </summary>
    internal static bool StableId(string id) => id.Length > 0 && !id.All(char.IsDigit);

    /// <summary>The words find:{text} looks for: the start of the line (long lines change as OCR re-reads them).</summary>
    internal static string? Query(string line)
    {
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(4).ToList();
        while (words.Count > 1 && string.Join(" ", words).Length > 30) words.RemoveAt(words.Count - 1);
        var q = string.Join(" ", words);
        // two letters at least ("808 CH"): bare numbers (tempo, time, counters) change by themselves
        return q.Count(char.IsLetter) >= 2 ? q : null;
    }

    internal static bool SameWords(string a, string b)
    {
        static string N(string s) => new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        string x = N(a), y = N(b);
        return x.Length > 0 && y.Length > 0 && (x.Contains(y) || y.Contains(x));
    }

    /// <summary>The shot scaled to maxEdge with a frame and a number tag on each item (drawn after scaling, so tags stay legible).</summary>
    private static byte[] Annotate(Bitmap shot, Rectangle area, List<Item> items, int maxEdge, out float scale)
    {
        int edge = Math.Max(shot.Width, shot.Height);
        scale = maxEdge > 0 && edge > maxEdge ? maxEdge / (float)edge : 1f;
        using var img = new Bitmap(Math.Max(1, (int)(shot.Width * scale)), Math.Max(1, (int)(shot.Height * scale)), PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(img))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(shot, 0, 0, img.Width, img.Height);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var uia = Color.FromArgb(235, 40, 130);
            var text = Color.FromArgb(0, 140, 255);
            float s = scale;
            RectangleF Img(RectangleF r) => new((r.X - area.X) * s, (r.Y - area.Y) * s, r.Width * s, r.Height * s);
            foreach (var it in items)
            {
                using var pen = new Pen(it.Text ? text : uia, 1.5f);
                var r = Img(it.Rect);
                g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
            }
            using var font = new Font("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var white = new SolidBrush(Color.White);
            foreach (var it in items)
            {
                var r = Img(it.Rect);
                var label = it.N.ToString();
                var size = g.MeasureString(label, font);
                float w = size.Width + 1, h = 14;
                // inside the frame's top-left corner: a tag above or below would sit on the neighbouring row of a list
                float x = Math.Clamp(r.X + 1, 0, Math.Max(0, img.Width - w));
                float y = Math.Clamp(r.Y + 1, 0, Math.Max(0, img.Height - h));
                if (r.Height < h + 1 || r.Width < w + 1)
                {
                    // too small to hold it: beside it, on the left (or the right at the picture's edge), centred
                    x = r.X - w - 1 >= 0 ? r.X - w - 1 : Math.Min(r.Right + 1, Math.Max(0, img.Width - w));
                    y = Math.Clamp(r.Y + (r.Height - h) / 2, 0, Math.Max(0, img.Height - h));
                }
                if (it.Text)
                {
                    // text sits in rows (lists, menus): a tag above would cover the row above; after the word it covers nothing
                    x = r.Right + 2 + w <= img.Width ? r.Right + 2 : Math.Max(0, r.X - w - 2);
                    y = Math.Clamp(r.Y + (r.Height - h) / 2, 0, Math.Max(0, img.Height - h));
                }
                using var bg = new SolidBrush(it.Text ? text : uia);
                g.FillRectangle(bg, x, y, w, h);
                g.DrawString(label, font, white, x + 0.5f, y - 0.5f);
            }
        }
        using var ms = new MemoryStream();
        img.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }
}
