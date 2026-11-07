using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Threading;

namespace TutorialOverlay;

/// <summary>A small grayscale picture of a piece of UI (e.g. the "C5" label of a piano roll keyboard).</summary>
internal sealed class Template
{
    public int W, H;
    public byte[] Px = Array.Empty<byte>();   // grayscale, row-major, 2x downscaled — for the fast coarse pass
    public int FW, FH;
    public byte[] Full = Array.Empty<byte>(); // grayscale at full size — to pick between look-alike candidates

    public static Template From(Bitmap b) => new()
    {
        W = b.Width / 2, H = b.Height / 2, Px = Gray(b, out _, out _),
        FW = b.Width, FH = b.Height, Full = GrayFull(b),
    };

    public static byte[] GrayFull(Bitmap b)
    {
        int w = b.Width, h = b.Height;
        var res = new byte[w * h];
        var data = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                for (int y = 0; y < h; y++)
                {
                    byte* row = (byte*)data.Scan0 + y * data.Stride;
                    for (int x = 0; x < w; x++, row += 4) res[y * w + x] = (byte)((row[2] * 77 + row[1] * 150 + row[0] * 29) >> 8);
                }
            }
        }
        finally { b.UnlockBits(data); }
        return res;
    }

    /// <summary>2x2-averaged grayscale (halves the work, smooths anti-aliasing differences).</summary>
    public static byte[] Gray(Bitmap b, out int w, out int h)
    {
        w = b.Width / 2; h = b.Height / 2;
        var res = new byte[Math.Max(0, w * h)];
        var data = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                byte* p0 = (byte*)data.Scan0;
                int stride = data.Stride;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int sum = 0;
                        for (int dy = 0; dy < 2; dy++)
                        {
                            byte* row = p0 + (2 * y + dy) * stride + 2 * x * 4;
                            for (int dx = 0; dx < 2; dx++)
                            {
                                byte* px = row + dx * 4;
                                sum += (px[2] * 77 + px[1] * 150 + px[0] * 29) >> 8;
                            }
                        }
                        res[y * w + x] = (byte)(sum / 4);
                    }
            }
        }
        finally { b.UnlockBits(data); }
        return res;
    }
}

/// <summary>Where a template was last found, relative to its window's frame (so marks follow window moves).</summary>
/// <summary>Where a template / text was last found; PrevRel is the sighting before LastRel (which way it was moving).</summary>
/// <summary>Size: of the found text / template piece (what a shape without coordinates frames); kept while it is out of view.</summary>
internal sealed record FindResult(bool Found, PointF Rel, float Score, PointF? LastRel, DateTime At, PointF? PrevRel = null, DateTime LastSeenAt = default, bool Moving = false, string? Went = null, SizeF Size = default, IntPtr Root = default, RectangleF? Visible = null, RectangleF? Box = null,
                              PointF[]? Places = null, string? Closed = null);

/// <summary>
/// Visual anchors: finds templates inside the anchor's element using the window's own pixels (PrintWindow),
/// a few times per second, on its own thread. That is what lets marks follow content that scrolls
/// (piano roll keys, playlist bars) in apps whose UIA only exposes big panels.
/// </summary>
internal static class Finder
{
    public static readonly ConcurrentDictionary<string, Template> Templates = new();
    private static readonly ConcurrentDictionary<string, FindResult> Results = new();
    /// <summary>The biggest control measured around a text, and where (window-relative) it was.</summary>
    private static readonly ConcurrentDictionary<string, (PointF at, RectangleF box)> Boxes = new();
    private static readonly ConcurrentDictionary<string, DateTime> TextSearchedAt = new();
    /// <summary>The app is not in front (its marks are usually hidden): templates are looked for only this often.</summary>
    private const int TemplateIdleMs = 1000;
    /// <summary>What the template search last looked at and found: an unchanged picture needs no new search.</summary>
    private static readonly ConcurrentDictionary<string, (int hash, Template tpl, Rectangle area, FindResult res)> LastLook = new();
    /// <summary>OCR is ~10× the cost of template matching: text anchors are re-read less often.</summary>
    private const int TextIntervalMs = 1200, TextConfirmMs = 1500;
    private static volatile AnchorSpec[] _wanted = Array.Empty<AnchorSpec>();
    private static readonly AutoResetEvent Wake = new(false);
    private static volatile bool _stop;

    public static void Start()
    {
        new Thread(Loop) { IsBackground = true, Name = "template-finder" }.Start();
        new Thread(TrackLoop) { IsBackground = true, Name = "text-tracker", Priority = ThreadPriority.BelowNormal }.Start();
    }
    public static void Stop() { _stop = true; Wake.Set(); }

    public static void SetWanted(IEnumerable<AnchorSpec> anchors)
    {
        var list = anchors.Where(a => a.Find != null).GroupBy(a => a.Key).Select(g => g.First()).ToArray();
        bool changed = list.Length != _wanted.Length;
        _wanted = list;
        if (changed) Wake.Set();
    }

    public static FindResult? Get(string key) => Results.TryGetValue(key, out var r) ? r : null;

    /// <summary>An anchor got a new key (pinned to its window): its last result, follower and frame go with it.</summary>
    public static void Carry(string from, string to)
    {
        if (Results.TryGetValue(from, out var r)) Results[to] = r;
        if (Tracks.TryGetValue(from, out var tr)) Tracks[to] = tr;
        if (Boxes.TryGetValue(from, out var b)) Boxes[to] = b;
        if (TextSearchedAt.TryGetValue(from, out var at)) TextSearchedAt[to] = at;
    }

    private static void Loop()
    {
        while (!_stop)
        {
            foreach (var a in _wanted)
            {
                if (a.Find!.Text != null)
                {
                    var now = DateTime.UtcNow;
                    // followed by the tracker, OCR only confirms it now and then; lost, it looks for it a bit more often
                    var g = Get(a.Key);
                    int every = Tracks.ContainsKey(a.Key) && g is { Found: true } ? TextConfirmMs
                              : g is { Found: false } && (now - g.LastSeenAt).TotalSeconds < 4 ? 350   // just lost: look again soon
                              : TextIntervalMs;
                    if (TextSearchedAt.TryGetValue(a.Key, out var at) && (now - at).TotalMilliseconds < every) continue;
                    // the app is not in front: its marks are hidden anyway — no reading until it is
                    if (!AppInFront(a)) continue;
                    TextSearchedAt[a.Key] = now;
                }
                else if (!AppInFront(a))
                {
                    // a template: every pass while its app is in front; behind another app only now and then (its
                    // marks are usually hidden then, but stay shown with hideWhenInactive: false)
                    var now = DateTime.UtcNow;
                    if (TextSearchedAt.TryGetValue(a.Key, out var at) && (now - at).TotalMilliseconds < TemplateIdleMs) continue;
                    TextSearchedAt[a.Key] = now;
                }
                try { Results[a.Key] = Search(a, Get(a.Key)); }
                catch (Exception e) { Log.Debug("finder: " + e.Message); }
            }
            // spots framed at their own size: re-measured now and then while their app is in front
            foreach (var a in _measured)
            {
                if (!AppInFront(a)) continue;
                if (Measured.TryGetValue(MeasureKey(a), out var mm) && (DateTime.UtcNow - mm.at).TotalMilliseconds < MeasureEveryMs) continue;
                try { MeasureSpot(a); } catch (Exception e) { Log.Debug("measure: " + e.Message); }
            }
            var keys = _wanted.Select(a => a.Key).ToHashSet();
            foreach (var k in LastLook.Keys.Where(k => !keys.Contains(k)).ToList()) LastLook.TryRemove(k, out _);
            Wake.WaitOne(_wanted.Length == 0 ? 500 : 180);
        }
    }

    /// <summary>
    /// Synchronous search (template tool, screenshots). A text named only by its app ({process, find:{text}}) is
    /// also looked for in the app's menus and dialogs, topmost first: a path written ahead ("OPTIONS → General
    /// settings") names menu items without knowing their window. Root in the result: the window it was found in
    /// (default: the anchor's own). Where it was found last time is read first.
    /// </summary>
    public static FindResult Search(AnchorSpec a, FindResult? prev)
    {
        if (a.Find?.Text == null || a.Process == null || a.Window != null || a.Title != null || a.WindowClass != null || a.Hwnd != null || a.Path.Count > 0)
            return SearchIn(a, prev);
        var pids = Resolver.Pids(a.Process);
        if (a.Find.HasIn) return SearchInItsWindow(a, prev, pids);
        // the app's windows but its largest per process (as Resolver.Popups, but also for an app no lesson watches:
        // validate before the start)
        var popups = Resolver.RecentWindows(400)
            .Where(w => pids.Contains(w.Pid) && w.Frame.Width > 1 && w.Frame.Height > 1 && !Native.IsIconic(w.Hwnd))
            .GroupBy(w => w.Pid)
            .SelectMany(g => g.OrderByDescending(w => (long)w.Frame.Width * w.Frame.Height).Skip(1))
            .Where(w => Native.IsWindowVisible(w.Hwnd)).Select(w => w.Hwnd).ToList();
        if (popups.Count == 0)
        {
            // where it was seen in a menu that has closed says nothing about the main window: carried over, that spot
            // was "past the window's edge" and the card asked to scroll. A miss still belongs to the closed menu
            var only = SearchIn(a, prev is { Root: var r0 } && r0 == IntPtr.Zero ? prev : null);
            return !only.Found && prev is { Root: var seen } && seen != IntPtr.Zero ? only with { Root = seen } : only;
        }
        // a menu or dialog opened during this step (by the click the step asks for, or the one that ended the
        // previous step) comes first: that is where the user is looking. Then the main window, and only then a dialog
        // that was already open — its "Options" heading is not the menu bar's OPTIONS
        var fromStep = StepSince.AddMilliseconds(-StepGraceMs);
        bool Fresh(IntPtr h) => Resolver.Since.TryGetValue(h, out var at) && at >= fromStep;
        var fresh = popups.Where(Fresh).ToList();
        var stale = popups.Where(h => !Fresh(h)).ToList();
        // the window the previous step's target was in (the settings, for the step after its Theme tab): the lesson
        // goes on there before the main window is read — else "Options" in it went to the menu bar's OPTIONS
        if (ContinueIn is { } ci)
        {
            var cont = stale.Where(h => ci.InMatches(Native.WindowTitle(h), Native.ClassName(h))).ToList();
            foreach (var h in cont) { stale.Remove(h); fresh.Add(h); }
        }
        if (prev is { Found: true, Root: var was } && was != IntPtr.Zero && fresh.Remove(was)) fresh.Insert(0, was);
        string W(IntPtr h) => $"0x{h.ToInt64():X} \"{Native.WindowTitle(h)}\"";
        if (Log.Verbose)
            Log.Debug($"find text \"{a.Find.TextShown}\": order fresh [{string.Join(", ", fresh.Select(W))}] → main → stale [{string.Join(", ", stale.Select(W))}]" +
                      $" (step since {StepSince:HH:mm:ss.fff}, opened: {string.Join(", ", popups.Select(h => Resolver.Since.TryGetValue(h, out var t) ? $"{t:HH:mm:ss.fff}" : "?"))})");
        foreach (var h in fresh)
        {
            var r = SearchIn(a.WithHwnd(h), null);
            if (r.Found) { if (Log.Verbose) Log.Debug($"  found in fresh {W(h)}"); return r with { Root = h }; }
        }
        var main = SearchIn(a, prev is { Root: var pr } && pr == IntPtr.Zero ? prev : null);
        if (main.Found) { if (Log.Verbose) Log.Debug("  found in main"); return main; }
        if (Log.Verbose) Log.Debug("  not in main");
        foreach (var h in stale)
        {
            var r = SearchIn(a.WithHwnd(h), null);
            if (r.Found) { if (Log.Verbose) Log.Debug($"  found in stale {W(h)}"); return r with { Root = h }; }
        }
        var miss = main;
        // last seen in a menu: a miss still belongs to that window (its last position is relative to it)
        return prev is { Root: var seenIn } && seenIn != IntPtr.Zero && miss.Root == IntPtr.Zero ? miss with { Root = seenIn } : miss;
    }

    /// <summary>
    /// A text whose window is known (find.in): looked for there only. Not open → not found, Closed = its title ("" for
    /// a menu without one; the card says to open it) — the same words elsewhere are another thing, and a mark there sends the user wrong.
    /// </summary>
    private static FindResult SearchInItsWindow(AnchorSpec a, FindResult? prev, int[] pids)
    {
        var f = a.Find!;
        if (f.InMain) return SearchIn(a, prev is { Root: var pr } && pr == IntPtr.Zero ? prev : null);
        var mine = Resolver.RecentWindows(400)
            .Where(w => pids.Contains(w.Pid) && w.Frame.Width > 1 && w.Frame.Height > 1 && !Native.IsIconic(w.Hwnd))
            .GroupBy(w => w.Pid)
            .SelectMany(g => g.OrderByDescending(w => (long)w.Frame.Width * w.Frame.Height).Skip(1))   // not the main window
            .Where(w => Native.IsWindowVisible(w.Hwnd) && f.InMatches(Native.WindowTitle(w.Hwnd), w.Class))
            .Select(w => w.Hwnd).ToList();
        if (prev is { Found: true, Root: var was } && mine.Remove(was)) mine.Insert(0, was);
        if (Log.Verbose) Log.Debug($"find text \"{f.TextShown}\" in {f.InShown}: {mine.Count} such window(s) open");
        foreach (var h in mine)
        {
            var r = SearchIn(a.WithHwnd(h), h == (prev?.Root ?? IntPtr.Zero) ? prev : null);
            if (r.Found) return r with { Root = h };
        }
        PointF? last = prev?.Found == true ? prev.Rel : prev?.LastRel;
        return new FindResult(false, default, 1, mine.Count > 0 ? last : null, DateTime.UtcNow, Root: mine.Count > 0 ? prev?.Root ?? IntPtr.Zero : IntPtr.Zero,
                              Closed: mine.Count == 0 ? f.InTitle ?? "" : null);
    }

    /// <summary>How long a text found a moment ago stays put through reads that miss it.</summary>
    private const int HoldMs = 1200;

    /// <summary>The window the previous step's target lives in (its find.in), read right after the step's new windows.</summary>
    public static volatile FindSpec? ContinueIn;

    /// <summary>When the current step began (MaxValue: no lesson running — then the main window comes first).</summary>
    public static DateTime StepSince = DateTime.MaxValue;
    /// <summary>
    /// A window opened this long before the step began still counts as the step's own: the click that ended the
    /// previous step opened it. Not for a step gone back to — the windows later steps opened (the settings dialog
    /// with its own "Options") are not where this step's target is (its menu bar's OPTIONS).
    /// </summary>
    public static int StepGraceMs = OpenedBeforeStepMs;
    internal const int OpenedBeforeStepMs = 5000;

    /// <summary>How long before a step's start a window may have opened and still be the step's own.</summary>
    internal static int GraceFor(bool wentBack) => wentBack ? 0 : OpenedBeforeStepMs;

    /// <summary>A template fits another place (half a template or more away) almost as well as the best one.</summary>
    private const float AmbiguousMargin = 0.012f;

    private static bool Ambiguous(Bitmap shot, Template tpl, int bx, int by, float best, float maxScore)
    {
        foreach (var c in MatchRanked(shot, tpl))
        {
            if (Math.Abs(c.x - bx) < tpl.FW / 2 && Math.Abs(c.y - by) < tpl.FH / 2) continue;   // the best place itself
            if (c.score <= maxScore && c.score - best < AmbiguousMargin) return true;
        }
        return false;
    }

    /// <summary>
    /// The visible thing at a spot of a picture (a template's icon, a caption under a pointed spot), relative to the
    /// window frame (dx, dy: where the picture's corner lies in it): what a shape of its own size frames.
    /// </summary>
    internal static RectangleF? VisibleAt(Bitmap pic, Point at, float dx, float dy)
    {
        var gray = Template.GrayFull(pic);
        var box = Icons.TargetBox(gray, pic.Width, pic.Height, at) ?? Icons.TargetBox(gray, pic.Width, pic.Height, at, contrast: 15);
        return box is Rectangle b ? new RectangleF(b.X + dx, b.Y + dy, b.Width, b.Height) : null;
    }

    private static volatile AnchorSpec[] _measured = Array.Empty<AnchorSpec>();
    private static readonly ConcurrentDictionary<string, (RectangleF rel, DateTime at)> Measured = new();
    private const int MeasureEveryMs = 1500;

    /// <summary>Spots (offset anchors) whose marks frame the visible thing there: measured now and then.</summary>
    public static void SetMeasured(IEnumerable<AnchorSpec> anchors) =>
        _measured = anchors.GroupBy(MeasureKey).Select(g => g.First()).ToArray();

    private static string MeasureKey(AnchorSpec a) => a.Key + "|" + a.Origin + "|" + a.Dx + "," + a.Dy;

    /// <summary>The visible thing at an offset anchor's spot, relative to its window frame, when measured.</summary>
    public static RectangleF? VisibleAtSpot(AnchorSpec a) => Measured.TryGetValue(MeasureKey(a), out var m) ? m.rel : null;

    /// <summary>Measures the thing at an offset anchor's spot from the window's own pixels (validate uses it too).</summary>
    public static RectangleF? MeasureSpot(AnchorSpec a)
    {
        var r = Resolver.Get(a.Key) ?? Resolver.ResolveNow(a);
        if (!r.Found || !Native.IsWindow(r.Root) || Native.IsIconic(r.Root)) return null;
        var frame = Native.FrameRect(r.Root);
        RectangleF b = r.Rel is RectangleF rel ? new RectangleF(frame.X + rel.X, frame.Y + rel.Y, rel.Width, rel.Height) : a.Client ? Native.ClientScreenRect(r.Root) : frame;
        var o = a.OriginOf(b);
        var p = new PointF(o.X + a.Dx, o.Y + a.Dy);
        var around = Rectangle.Intersect(Rectangle.Round(new RectangleF(p.X - 200, p.Y - 200, 400, 400)), frame);
        if (around.Width < 8 || around.Height < 8) return null;
        using var shot = ScreenCapture.CaptureWindow(r.Root, around);
        if (shot == null) return null;
        var vis = VisibleAt(shot, new Point((int)Math.Round(p.X - around.X), (int)Math.Round(p.Y - around.Y)), around.X - frame.X, around.Y - frame.Y);
        if (vis is RectangleF v) Measured[MeasureKey(a)] = (v, DateTime.UtcNow);
        return vis;
    }

    /// <summary>
    /// One read for every wording: in their language, or with both recognizers when they mix scripts. An area bigger
    /// than the budget (a whole app window) is read at full size in tiles: shrunk to fit one run, small UI text such
    /// as FL's "TOOLS" was lost; smaller areas are enlarged up to 2× as before.
    /// </summary>
    private static List<OcrLine> ReadFor(Bitmap shot, FindSpec f, float budget)
    {
        bool oneScript = f.Texts.Select(Ocr.AutoLang).Distinct().Count() <= 1;
        var query = oneScript ? f.Text : null;
        return (float)shot.Width * shot.Height > budget ? Ocr.ReadTiled(shot, f.Lang, 1f, query) : Ocr.Read(shot, f.Lang, query, budget);
    }

    /// <summary>OCR budget for reading a text's whole search area (pixels handed to one recognizer run).</summary>
    internal const float WholeAreaBudget = 1_500_000f;

    /// <summary>
    /// A text's occurrences in a picture, in reading order (top-to-bottom, then left-to-right) — what a search picks
    /// from: find.index counts in this order. The OCR bench reads saved screenshots through this same path.
    /// </summary>
    internal static List<OcrLine> ReadForBench(Bitmap shot, FindSpec f) => ReadFor(shot, f, WholeAreaBudget);

    internal static List<TextMatch> FindIn(Bitmap shot, FindSpec f, float budget = WholeAreaBudget)
    {
        var lines = ReadFor(shot, f, budget);
        var found = MatchAny(lines, f);
        if (found.Count == 0 && f.TextMatch != "regex") found = Closer(shot, lines, f, out lines);
        return Rank(found.OrderBy(m => Math.Round(m.Rect.Y / 8)).ThenBy(m => m.Rect.X).ToList(), lines, f);
    }

    /// <summary>
    /// The text read again right where it stood (picture coordinates): cut out with room around it, enlarged, and once
    /// more inverted — a selected row is light on a strong colour, which the recognizer often drops.
    /// </summary>
    private static RectangleF? ReadAround(Bitmap shot, RectangleF at, FindSpec f)
    {
        var cut = Rectangle.Intersect(Rectangle.Round(RectangleF.Inflate(at, at.Width * 0.4f + 8, at.Height)),
                                      new Rectangle(0, 0, shot.Width, shot.Height));
        if (cut.Width < 8 || cut.Height < 8) return null;
        using var part = shot.Clone(cut, shot.PixelFormat);
        for (int pass = 0; pass < 2; pass++)
        {
            using var big = new Bitmap(part.Width * 2, part.Height * 2);
            using (var g = Graphics.FromImage(big))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                if (pass == 0) g.DrawImage(part, 0, 0, big.Width, big.Height);
                else
                {
                    var inv = new System.Drawing.Imaging.ColorMatrix(new[]
                    {
                        new float[] { -1, 0, 0, 0, 0 }, new float[] { 0, -1, 0, 0, 0 }, new float[] { 0, 0, -1, 0, 0 },
                        new float[] { 0, 0, 0, 1, 0 }, new float[] { 1, 1, 1, 0, 1 },
                    });
                    using var ia = new System.Drawing.Imaging.ImageAttributes();
                    ia.SetColorMatrix(inv);
                    g.DrawImage(part, new Rectangle(0, 0, big.Width, big.Height), 0, 0, part.Width, part.Height, GraphicsUnit.Pixel, ia);
                }
            }
            var lines = ReadFor(big, f, 1_000_000f).Select(x =>
            {
                var ol = new OcrLine();
                foreach (var w in x.Words)
                    ol.Words.Add(new OcrWord(w.Text, new RectangleF(cut.X + w.Rect.X / 2, cut.Y + w.Rect.Y / 2, w.Rect.Width / 2, w.Rect.Height / 2)));
                return ol;
            }).ToList();
            var m = MatchAny(lines, f);
            if (m.Count > 0) return m[0].Rect;
        }
        return null;
    }

    /// <summary>Something is written there (not an empty strip): the gray levels vary like letters do.</summary>
    private static bool HasInk(Bitmap shot, RectangleF at)
    {
        var r = Rectangle.Intersect(Rectangle.Round(at), new Rectangle(0, 0, shot.Width, shot.Height));
        if (r.Width < 4 || r.Height < 4) return false;
        using var part = shot.Clone(r, shot.PixelFormat);
        var g = Template.GrayFull(part);
        double mean = 0; foreach (var v in g) mean += v; mean /= g.Length;
        double var0 = 0; foreach (var v in g) var0 += (v - mean) * (v - mean);
        return Math.Sqrt(var0 / g.Length) > 18;
    }

    /// <summary>
    /// Not found, but a line holds part of it ("B Reset preset..." for "Save preset": white "Save" on FL's dark-orange
    /// button was dropped by the recognizer): that line is read again, cut out with room on both sides and enlarged —
    /// small, dim or low-contrast words read there that a whole-window read loses. At most a few lines, and only
    /// after a miss, so a found text costs nothing more.
    /// </summary>
    private static List<TextMatch> Closer(Bitmap shot, List<OcrLine> lines, FindSpec f, out List<OcrLine> read)
    {
        read = lines;
        var words = f.Texts.SelectMany(t => t.ToLowerInvariant().Split(Spaces, StringSplitOptions.RemoveEmptyEntries))
                           .Where(w => w.Count(char.IsLetterOrDigit) >= 4).Distinct().ToList();
        if (words.Count == 0) return new List<TextMatch>();
        var partial = lines.Where(l => words.Any(w => l.Text.ToLowerInvariant().Contains(w))).Take(3).ToList();
        foreach (var l in partial)
        {
            var b = l.Bounds;
            float pad = b.Height * 6;   // a dropped word sits beside the read ones
            var cut = Rectangle.Intersect(Rectangle.Round(RectangleF.FromLTRB(b.Left - pad, b.Top - b.Height, b.Right + pad, b.Bottom + b.Height)),
                                          new Rectangle(0, 0, shot.Width, shot.Height));
            if (cut.Width < 8 || cut.Height < 8) continue;
            using var part = shot.Clone(cut, shot.PixelFormat);
            using var big = new Bitmap(part.Width * 2, part.Height * 2);
            using (var g = Graphics.FromImage(big))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(part, 0, 0, big.Width, big.Height);
            }
            var again = ReadFor(big, f, 1_000_000f);
            // back to the picture's coordinates
            var mapped = again.Select(x =>
            {
                var ol = new OcrLine();
                foreach (var w in x.Words)
                    ol.Words.Add(new OcrWord(w.Text, new RectangleF(cut.X + w.Rect.X / 2, cut.Y + w.Rect.Y / 2, w.Rect.Width / 2, w.Rect.Height / 2)));
                return ol;
            }).ToList();
            var m = MatchAny(mapped, f);
            if (Log.Verbose) Log.Debug($"find text \"{f.TextShown}\": read closer around \"{l.Text}\": " + string.Join(" / ", mapped.Select(x => x.Text)));
            if (m.Count > 0) { read = lines.Concat(mapped).ToList(); return m; }
        }
        return new List<TextMatch>();
    }

    /// <summary>
    /// Which of several occurrences a text the author did not pin (no index) means: first one that is the whole
    /// caption ("Dark", not the "Dark" of "Dark orange"; the "Ports" tab, not "Ports.cs"), then one in the author's
    /// own letter case ("BassDrum", not "Bassdrum 22"), then reading order. With index, reading order alone — that is
    /// what the index counts in.
    /// </summary>
    internal static List<TextMatch> Rank(List<TextMatch> found, List<OcrLine> lines, FindSpec f)
    {
        if (f.NthGiven || found.Count < 2 || f.TextMatch != "contains") return found;
        var whole = f.Texts.SelectMany(t => Ocr.Find(lines, t, "exact")).Select(m => m.Rect).ToList();
        bool Whole(TextMatch m) => whole.Any(w =>
        {
            var x = RectangleF.Intersect(w, m.Rect);
            return x.Width * x.Height > 0.5f * m.Rect.Width * m.Rect.Height;
        });
        bool OwnCase(TextMatch m) => f.Texts.Any(t => t.Any(char.IsUpper) && m.Text.Contains(t.Trim(), StringComparison.Ordinal));
        return found.OrderBy(m => Whole(m) ? 0 : 1).ThenBy(m => OwnCase(m) ? 0 : 1).ToList();   // stable: reading order within
    }

    /// <summary>
    /// The matches of the first wording that occurs at all (the order is the author's preference). A wording that
    /// is a cut-down form of another one listed ("Snare" beside "808 Snare") is a fallback keyword, and such a one
    /// counts only where it is most of its line — else it catches "AP - Future Bass Snare Reverse" when the item
    /// itself is missing. Wordings of their own ("Preferences" / "Options") match like any text: a menu bar or a
    /// tab strip reads as one line, with the item a small part of it.
    /// </summary>
    internal static List<TextMatch> MatchAny(List<OcrLine> lines, FindSpec f)
    {
        foreach (var t in f.Texts)
        {
            var m = Ocr.Find(lines, t, f.TextMatch);
            if (IsCutDown(t, f.Texts)) m = m.Where(x => MostOfItsLine(t, x, lines)).ToList();
            if (Log.Verbose && m.Count > 0) Log.Debug($"find text \"{t}\": " + string.Join(" | ", m.Select(x => $"{x.Text} @{x.Rect}")));
            if (m.Count > 0) return m;
        }
        return new List<TextMatch>();
    }

    private static readonly char[] Spaces = { ' ', '	' };

    /// <summary>Every word of <paramref name="t"/> is in another, longer wording of the list.</summary>
    internal static bool IsCutDown(string t, List<string> all)
    {
        var words = t.ToLowerInvariant().Split(Spaces, StringSplitOptions.RemoveEmptyEntries);
        return all.Any(o => o != t && o.Length > t.Length
                            && words.All(w => o.ToLowerInvariant().Split(Spaces, StringSplitOptions.RemoveEmptyEntries).Contains(w)));
    }

    private static int Signs(string s) => s.Count(char.IsLetterOrDigit);

    private static bool MostOfItsLine(string wording, TextMatch m, List<OcrLine> lines)
    {
        var c = new PointF(m.Rect.X + m.Rect.Width / 2, m.Rect.Y + m.Rect.Height / 2);
        var line = lines.FirstOrDefault(l => { var b = l.Bounds; b.Inflate(2, 2); return b.Contains(c); });
        return line == null || Signs(wording) >= 0.6 * Signs(line.Text);
    }

    private static FindResult SearchIn(AnchorSpec a, FindResult? prev)
    {
        var f = a.Find!;
        PointF? last = prev?.Found == true ? prev.Rel : prev?.LastRel;
        PointF? before = prev?.Found == true ? prev.LastRel : prev?.PrevRel;
        DateTime seenAt = prev?.Found == true ? prev.At : prev?.LastSeenAt ?? default;
        // was it moving (scrolled) just before it went missing? then it went off; still, it is just unreadable
        Tracks.TryGetValue(a.Key, out var pt);
        string? went = prev?.Found == true ? pt?.Went() : prev?.Went;
        bool moving = prev?.Found == true ? pt != null && pt.Scrolling || went != null
                                          : prev?.Moving ?? false;
        FindResult Miss() => new(false, default, 1, last, DateTime.UtcNow, before, seenAt, moving, went, prev?.Size ?? default);
        Template? tpl = null;
        if (f.Text == null && !TemplateStore.TryGet(f.Template, out tpl)) return Miss();
        var r = Resolver.Get(a.WithoutFind().Key) ?? Resolver.ResolveNow(a.WithoutFind());
        if (!r.Found || Native.IsIconic(r.Root)) return Miss();
        var frame = Native.FrameRect(r.Root);
        RectangleF b = r.Rel is RectangleF rel ? new RectangleF(frame.X + rel.X, frame.Y + rel.Y, rel.Width, rel.Height) : frame;
        // search area: the element, or a part of it ("within" is relative to the element's top-left)
        RectangleF area = f.Within is RectangleF w ? new RectangleF(b.X + w.X, b.Y + w.Y, w.Width, w.Height) : b;
        area.Intersect(b);
        if (tpl != null && (area.Width < tpl.W * 2 || area.Height < tpl.H * 2)) return Miss();
        if (area.Width < 4 || area.Height < 4) return Miss();
        // a text the tracker follows only needs confirming: read a band around it, not the whole area
        Track? followed = f.Text != null && prev is { Found: true } && Tracks.TryGetValue(a.Key, out var tk) ? tk : null;
        var readArea = area;
        if (followed != null)
        {
            float pad = followed.H * 5 + 60;
            readArea = RectangleF.Intersect(new RectangleF(area.X, followed.Screen.Y - pad, area.Width, followed.H + 2 * pad), area);
            if (readArea.Height < followed.H) readArea = area;
        }
        var shotAt = DateTime.UtcNow;
        var shot = ScreenCapture.CaptureWindow(r.Root, Rectangle.Round(readArea));
        if (shot == null) { if (Log.Verbose) Log.Debug($"  no picture of 0x{r.Root.ToInt64():X} {Rectangle.Round(readArea)}"); return Miss(); }
        if (Log.Verbose && f.Text != null) Log.Debug($"  reading 0x{r.Root.ToInt64():X} {shot.Width}x{shot.Height}{(followed != null ? " (band around the followed text)" : "")}");
        try
        {
        if (tpl == null)
        {
            var found = FindIn(shot, f, followed != null ? 1_000_000f : WholeAreaBudget);
            if (followed != null && found.Count == 0)
            {
                // not in the band around where it was followed to: read the whole area before calling it gone
                Tracks.TryRemove(a.Key, out _);
                followed = null;
                readArea = area;
                shot.Dispose();
                shotAt = DateTime.UtcNow;
                shot = ScreenCapture.CaptureWindow(r.Root, Rectangle.Round(readArea));
                if (shot == null) return Miss();
                found = FindIn(shot, f, WholeAreaBudget);
            }
            RectangleF m;
            if (followed != null)
            {
                // confirming: the occurrence nearest to where it is being followed
                var fc = new PointF(followed.Screen.X + followed.W / 2f - readArea.X, followed.Screen.Y + followed.H / 2f - readArea.Y);
                m = found.OrderBy(x => Math.Abs(x.Rect.X + x.Rect.Width / 2 - fc.X) + Math.Abs(x.Rect.Y + x.Rect.Height / 2 - fc.Y)).First().Rect;
            }
            else
            {
                // one read that missed it (a highlighted tab read as "In бате" once) is not a reason to drop or move
                // the mark: what was found a moment ago stays for a little while
                if (found.Count <= f.Nth && prev is { Found: true } && !moving && (DateTime.UtcNow - prev.At).TotalMilliseconds < HoldMs)
                    return prev;
                if (found.Count <= f.Nth) { Tracks.TryRemove(a.Key, out _); return Miss(); }
                // it stood still and now reads only far away: its own place turned unreadable (a list item got
                // selected, white on blue) while the same words show elsewhere ("Counter-Strike 2 Update" in the news
                // beside Steam's library list). Read the old place closer; still nothing, but words there: the mark
                // stays. It moves only when the old place is empty or the content scrolled
                if (prev is { Found: true } && !moving && prev.Size.Width > 0 && f.Nth == 0)
                {
                    var was0 = new PointF(frame.X + prev.Rel.X - readArea.X, frame.Y + prev.Rel.Y - readArea.Y);
                    float lh = Math.Max(prev.Size.Height, 8);
                    if (!found.Any(x => Math.Abs(x.Rect.X + x.Rect.Width / 2 - was0.X) + Math.Abs(x.Rect.Y + x.Rect.Height / 2 - was0.Y) < lh * 3))
                    {
                        var oldRect = new RectangleF(was0.X - prev.Size.Width / 2, was0.Y - lh / 2, prev.Size.Width, lh);
                        if (ReadAround(shot, oldRect, f) is RectangleF again)
                        {
                            if (Log.Verbose) Log.Debug($"find text \"{f.TextShown}\": read again where it stood (the far copies are others)");
                            found = new List<TextMatch> { new(f.Text!, again, 0) };
                        }
                        else if (HasInk(shot, oldRect))
                        {
                            if (Log.Verbose) Log.Debug($"find text \"{f.TextShown}\": unreadable where it stood, words still there — the mark stays");
                            return prev;
                        }
                    }
                }
                m = found[f.Nth].Rect;
                // seen a moment ago: the same occurrence, not whichever comes first in this read (the page's heading
                // "In Game" above the tab "In Game")
                if (prev is { Found: true } && found.Count > 1)
                {
                    var was = new PointF(frame.X + prev.Rel.X - readArea.X, frame.Y + prev.Rel.Y - readArea.Y);
                    var near = found.OrderBy(x => Math.Abs(x.Rect.X + x.Rect.Width / 2 - was.X) + Math.Abs(x.Rect.Y + x.Rect.Height / 2 - was.Y)).First();
                    if (Math.Abs(near.Rect.X + near.Rect.Width / 2 - was.X) + Math.Abs(near.Rect.Y + near.Rect.Height / 2 - was.Y) < near.Rect.Height * 3)
                        m = near.Rect;
                }
            }
            var c = new PointF(readArea.X + m.X + m.Width / 2, readArea.Y + m.Y + m.Height / 2);
            // the tracker moved it since this shot was taken: its position is newer than ours
            if (Tracks.TryGetValue(a.Key, out var live) && live.MovedAt > shotAt && prev is { Found: true }) return prev;
            StartTracking(a.Key, shot, m, readArea, area, r.Root);
            // the control the words sit on (a tab, a drop-down with its arrow): what is framed and clicked,
            // relative to the words' centre so it moves with them while they are followed
            RectangleF? box = null;
            try
            {
                var tr = Rectangle.Round(m);
                if (Icons.ControlBox(Template.GrayFull(shot), shot.Width, shot.Height, tr) is Rectangle cb)
                    box = new RectangleF(cb.X - (m.X + m.Width / 2), cb.Y - (m.Y + m.Height / 2), cb.Width, cb.Height);
            }
            catch { }
            // the same place as a moment ago: the frame keeps the biggest control measured there. Hovered, a tab
            // shows its box; left alone it is a flat row — the frame must not jump between the two
            // (remembered per place, so it also outlives a moment the word was not seen)
            var here0 = new PointF(c.X - frame.X, c.Y - frame.Y);
            // OCR places the same words a few pixels apart from one read to the next (up to most of a line): within
            // a line's height of where it was, it is the same place — the mark does not twitch with every read
            // (scrolling is followed by the tracker, which moves it for real)
            float same = Math.Max(4, m.Height * 0.9f);
            if (prev is { Found: true } && Math.Abs(prev.Rel.X - here0.X) <= same && Math.Abs(prev.Rel.Y - here0.Y) <= same)
                here0 = prev.Rel;
            if (Boxes.TryGetValue(a.Key, out var kept) && Math.Abs(kept.at.X - here0.X) <= same && Math.Abs(kept.at.Y - here0.Y) <= same
                && (box is not RectangleF nb || nb.Width * nb.Height < kept.box.Width * kept.box.Height))
                box = kept.box;
            if (box is RectangleF keep) Boxes[a.Key] = (here0, keep);
            // every occurrence of this read (relative to the window frame, reading order): validate warns when a text
            // the author did not pin with index / within is on screen more than once — the mark may land on the wrong one
            var places = followed == null && found.Count > 1
                ? found.Select(x => new PointF(readArea.X + x.Rect.X + x.Rect.Width / 2 - frame.X, readArea.Y + x.Rect.Y + x.Rect.Height / 2 - frame.Y)).ToArray()
                : null;
            return new FindResult(true, here0, 0, last, DateTime.UtcNow, Size: m.Size, Box: box, Places: places);
        }
        // where it was a moment ago, first: a search around it costs a few milliseconds, the whole window ~200 ms —
        // and a template target is looked for on every pass while its app is in front
        if (prev is { Found: true } && prev.Root == IntPtr.Zero && f.Nth == 0)
        {
            var was = new PointF(frame.X + prev.Rel.X - area.X, frame.Y + prev.Rel.Y - area.Y);
            var near = Rectangle.Intersect(Rectangle.Round(new RectangleF(was.X - tpl.FW - 48, was.Y - tpl.FH - 48, 2 * tpl.FW + 96, 2 * tpl.FH + 96)),
                                           new Rectangle(0, 0, shot.Width, shot.Height));
            if (near.Width >= tpl.FW + 2 && near.Height >= tpl.FH + 2)
            {
                using var part = shot.Clone(near, shot.PixelFormat);
                var pimg = Template.Gray(part, out int pw, out int ph);
                var (px, py, ps) = Match(pimg, pw, ph, tpl, Template.GrayFull(part), part.Width, part.Height);
                if (Log.Verbose) Log.Debug($"template {f.Template}: near the last spot, score {ps:0.000}");
                if (ps <= f.MaxScore)
                {
                    var hitNear = new PointF(area.X + near.X + px + tpl.FW / 2f, area.Y + near.Y + py + tpl.FH / 2f);
                    return new FindResult(true, new PointF(hitNear.X - frame.X, hitNear.Y - frame.Y), ps, last, DateTime.UtcNow, Size: new SizeF(tpl.FW, tpl.FH),
                                          Visible: VisibleAt(part, new Point(px + tpl.FW / 2, py + tpl.FH / 2), area.X + near.X - frame.X, area.Y + near.Y - frame.Y));
                }
            }
        }
        // one of several look-alikes (a mixer strip): the n-th good match in reading order, like a text's index
        if (f.Nth > 0)
        {
            var good = MatchRanked(shot, tpl, f.Nth + 12).Where(c => c.score <= f.MaxScore)
                .OrderBy(c => Math.Round(c.y / 8.0)).ThenBy(c => c.x).ToList();
            if (good.Count <= f.Nth) return Miss();
            var g = good[f.Nth];
            var hitN = new PointF(area.X + g.x + tpl.FW / 2f, area.Y + g.y + tpl.FH / 2f);
            return new FindResult(true, new PointF(hitN.X - frame.X, hitN.Y - frame.Y), g.score, last, DateTime.UtcNow, Size: new SizeF(tpl.FW, tpl.FH),
                                  Visible: VisibleAt(shot, new Point(g.x + tpl.FW / 2, g.y + tpl.FH / 2), area.X - frame.X, area.Y - frame.Y));
        }
        var img = Template.Gray(shot, out int iw, out int ih);
        // the same picture of the same place (relative to the window) as last time: the answer is the same too
        var hc = new HashCode();
        hc.AddBytes(img);
        int hash = hc.ToHashCode();
        var place = new Rectangle((int)Math.Round(area.X - frame.X), (int)Math.Round(area.Y - frame.Y), shot.Width, shot.Height);
        if (LastLook.TryGetValue(a.Key, out var look) && look.hash == hash && ReferenceEquals(look.tpl, tpl) && look.area == place)
            return look.res.Found ? look.res with { LastRel = last, At = DateTime.UtcNow } : Miss();
        long fullAt = Environment.TickCount64;
        var (bx, by, score) = Match(img, iw, ih, tpl, Template.GrayFull(shot), shot.Width, shot.Height);
        // a near tie between two places means the picture fits both (a strip captured while selected, and another
        // strip selected now): a mark there could be on the wrong one, so it counts as not found
        if (score <= f.MaxScore && Ambiguous(shot, tpl, bx, by, score, f.MaxScore))
        {
            if (Log.Verbose) Log.Debug($"template {f.Template}: two places fit almost equally — not trusted");
            score = 1;
        }
        if (Log.Verbose) Log.Debug($"template {f.Template}: whole area {shot.Width}x{shot.Height} in {Environment.TickCount64 - fullAt} ms, score {score:0.000}");
        FindResult res;
        if (score > f.MaxScore) res = Miss();
        else
        {
            // the centre of the match (full-size pixels), like a found text's: a ring without coordinates lands on it,
            // and a piece cut from the very edge of its pane does not count as "scrolled past the edge"
            var hit = new PointF(area.X + bx + tpl.FW / 2f, area.Y + by + tpl.FH / 2f);
            res = new FindResult(true, new PointF(hit.X - frame.X, hit.Y - frame.Y), score, last, DateTime.UtcNow, Size: new SizeF(tpl.FW, tpl.FH),
                                 Visible: VisibleAt(shot, new Point(bx + tpl.FW / 2, by + tpl.FH / 2), area.X - frame.X, area.Y - frame.Y));
        }
        LastLook[a.Key] = (hash, tpl, place, res);
        return res;
        }
        finally { shot?.Dispose(); }
    }

    // =========================================================== following a found text between OCR reads

    /// <summary>The look of a found text (its own pixels) and where it is now on screen.</summary>
    private sealed class Track
    {
        public byte[] Px = Array.Empty<byte>();
        public int[] Ink = Array.Empty<int>();   // the letters' own pixels: what makes it this word, not background
        public int W, H;
        public Rectangle Screen;       // where the text is now
        public RectangleF View;        // the area it lives in (the element / within): never followed outside it
        public IntPtr Root;
        public DateTime MovedAt, MovedBefore;   // its last two steps: two within a second is a scroll, one a jump
        /// <summary>Followed as it scrolled (two steps within the last second), not one jump right before it was lost.</summary>
        public bool Scrolling => (DateTime.UtcNow - MovedBefore).TotalSeconds < 1;
        public DateTime RestUntil;     // after a failed search: a short pause instead of searching 30 times a second
        // the content around a text that stays put: it scrolling past means this is a pinned copy (a sticky header)
        public byte[]? PrevImg;
        public Rectangle PrevRegion;
        public DateTime PrevAt;
        public int ContentAcc;         // px the content moved past it (+ down), since it last moved itself
        public DateTime ContentAt;

        /// <summary>A pinned copy disappearing: the real line went the way the content was going.</summary>
        public string? Went() =>
            Math.Abs(ContentAcc) >= H && (DateTime.UtcNow - ContentAt).TotalSeconds < 3 ? (ContentAcc > 0 ? "down" : "up") : null;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    /// <summary>The anchor's app is the foreground one (anchors without a process always count).</summary>
    private static bool AppInFront(AnchorSpec a)
    {
        if (a.Process == null) return true;
        int pid = Native.ProcessId(GetForegroundWindow());
        // cached name lookup: a Process object per anchor per pass is needless garbage
        return pid != 0 && Engine.ProcName(pid).Equals(a.Process, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly ConcurrentDictionary<string, Track> Tracks = new();
    private const int TrackMs = 33;
    private const float TrackMaxScore = 0.16f;
    private const int RestMs = 250;

    /// <summary>Pixels clearly off the background (the median): the letters.</summary>
    private static int[] InkOf(byte[] px)
    {
        if (px.Length == 0) return Array.Empty<int>();
        var sorted = (byte[])px.Clone();
        Array.Sort(sorted);
        int bg = sorted[sorted.Length / 2];
        var ink = new List<int>();
        for (int i = 0; i < px.Length; i++) if (Math.Abs(px[i] - bg) > 40) ink.Add(i);
        // hardly any ink (very faint text): fall back to every pixel
        return ink.Count >= 12 ? ink.ToArray() : Enumerable.Range(0, px.Length).ToArray();
    }

    /// <param name="shotArea">where <paramref name="shot"/> was taken (screen)</param>
    /// <param name="view">the whole area the text lives in</param>
    private static void StartTracking(string key, Bitmap shot, RectangleF m, RectangleF shotArea, RectangleF view, IntPtr root)
    {
        var crop = Rectangle.Intersect(Rectangle.Round(m), new Rectangle(0, 0, shot.Width, shot.Height));
        if (crop.Width < 6 || crop.Height < 6) return;
        using var part = shot.Clone(crop, PixelFormat.Format32bppArgb);
        var px = Template.GrayFull(part);
        var t = new Track
        {
            Px = px, Ink = InkOf(px), W = crop.Width, H = crop.Height, Root = root, View = view,
            Screen = new Rectangle((int)shotArea.X + crop.X, (int)shotArea.Y + crop.Y, crop.Width, crop.Height),
        };
        // re-confirmed in the same place: keep what was seen of the content scrolling past it
        if (Tracks.TryGetValue(key, out var old) && Math.Abs(old.Screen.Y - t.Screen.Y) <= 3 && Math.Abs(old.Screen.X - t.Screen.X) <= 3)
        {
            t.ContentAcc = old.ContentAcc; t.ContentAt = old.ContentAt; t.MovedAt = old.MovedAt; t.MovedBefore = old.MovedBefore;
        }
        Tracks[key] = t;
    }

    /// <summary>
    /// OCR runs a few times a second — too slow for a highlight to ride along with scrolling text. In between,
    /// the text's own pixels are looked for around where it was, ~30 times a second (plain pixel matching on a
    /// small piece of screen, cheap): a still text costs one comparison, a moving one is followed smoothly, and
    /// the next OCR read confirms it is still the right word. A doubtful match moves nothing.
    /// </summary>
    private static void TrackLoop()
    {
        while (!_stop)
        {
            var wanted = _wanted.Select(a => a.Key).ToHashSet();
            foreach (var key in Tracks.Keys.Where(k => !wanted.Contains(k)).ToList()) Tracks.TryRemove(key, out _);
            foreach (var (key, tr) in Tracks.ToArray())
            {
                try { Follow(key, tr); }
                catch (Exception e) { Log.Debug("tracker: " + e.Message); }
            }
            Thread.Sleep(Tracks.IsEmpty ? 150 : TrackMs);
        }
    }

    private static void Follow(string key, Track tr)
    {
        if (Get(key) is not { Found: true } cur || Native.IsIconic(tr.Root) || DateTime.UtcNow < tr.RestUntil) return;
        // the screen must show the app's own window where the text is (not another window on top, not minimised)
        var mid = new PointF(tr.Screen.X + tr.Screen.Width / 2f, tr.Screen.Y + tr.Screen.Height / 2f);
        if (Native.HitWindow(mid).root != tr.Root) return;
        // look around the last place: far up and down (scrolling), a little sideways
        int my = Math.Max(160, tr.H * 6), mx = Math.Max(40, tr.W / 3);
        var region = Rectangle.Intersect(Rectangle.Inflate(tr.Screen, mx, my), Rectangle.Round(tr.View));
        region.Intersect(Native.FrameRect(tr.Root));
        if (region.Width < tr.W || region.Height < tr.H) return;
        // the app's own pixels, not the screen: our frame and mascot drawn over the word (unless hidden from
        // capture) would not match the word's picture, and the tracker would chase its own reflection
        using var shot = ScreenCapture.CaptureWindow(tr.Root, region, 30);
        if (shot == null || shot.Width != region.Width || shot.Height != region.Height) return;
        var img = Template.GrayFull(shot);
        int iw = shot.Width, ih = shot.Height;
        int lx = tr.Screen.X - region.X, ly = tr.Screen.Y - region.Y;
        // compared on the letters only: mostly-background templates otherwise "match" any near-empty spot, and a
        // changed background (selection, current-line highlight) would otherwise hide the very word
        float Score(int x, int y, int step)
        {
            if (x < 0 || y < 0 || x + tr.W > iw || y + tr.H > ih) return 1;
            long sad = 0, n = 0;
            for (int i = 0; i < tr.Ink.Length; i += step)
            {
                int o = tr.Ink[i], ty = o / tr.W, tx = o % tr.W;
                sad += Math.Abs(img[(y + ty) * iw + x + tx] - tr.Px[o]);
                n++;
            }
            return n == 0 ? 1 : sad / (float)(n * 255);
        }
        // still where it was: only keep an eye on whether the content scrolls past it
        float here = Score(lx, ly, 1);
        if (here < TrackMaxScore * 0.5f) { WatchContent(tr, img, iw, ih, region, ly, false); return; }
        WatchContent(tr, img, iw, ih, region, ly, true);
        int bx = -1, by = -1;
        float best = float.MaxValue;
        void Scan(int x0, int x1)
        {
            for (int y = 0; y + tr.H <= ih; y += 2)
                for (int x = Math.Max(0, x0); x <= Math.Min(iw - tr.W, x1); x += 2)
                {
                    float v = Score(x, y, 3);
                    if (v < best) { best = v; bx = x; by = y; }
                }
        }
        // scrolling moves text up or down: its own column first (a few hundred positions), the whole
        // neighbourhood only if that finds nothing good
        Scan(lx - 4, lx + 4);
        if (best > TrackMaxScore * 1.3f) Scan(0, iw - tr.W);
        // nothing fits better, and the old place still looks like the word on another background (the pointer
        // hovers the tab and highlights it, or a neighbour): it is there — OCR checks it, the mark stays meanwhile.
        // Scrolled away, the old place shows something else entirely and this does not hold
        if (best > TrackMaxScore && here < TrackMaxScore * 2.5f)
        {
            tr.RestUntil = DateTime.UtcNow.AddMilliseconds(RestMs);
            return;
        }
        if (best > TrackMaxScore * 1.3f) { FollowFar(key, tr, cur); return; }
        if (bx < 0) return;
        int fx = bx, fy = by;
        best = float.MaxValue;
        for (int y = by - 2; y <= by + 2; y++)
            for (int x = bx - 2; x <= bx + 2; x++)
            {
                float v = Score(x, y, 1);
                if (v < best) { best = v; fx = x; fy = y; }
            }
        if (best > TrackMaxScore) { if (here >= TrackMaxScore * 2.5f) Lost(key, tr, cur); else tr.RestUntil = DateTime.UtcNow.AddMilliseconds(RestMs); return; }
        var moved = new Rectangle(region.X + fx, region.Y + fy, tr.W, tr.H);
        // the old place only looks a little different (the pointer hovers the tab, it got selected): a spot that
        // fits only somewhat better is a neighbouring row, not the word gone there — stay, OCR decides
        if (moved.Location != tr.Screen.Location && here <= TrackMaxScore && best > here * 0.6f)
        {
            tr.RestUntil = DateTime.UtcNow.AddMilliseconds(RestMs);
            return;
        }
        if (moved.Location == tr.Screen.Location || Jitter(moved.X - tr.Screen.X, moved.Y - tr.Screen.Y, tr.H))
        {
            // it is where it was, only looks a little different (hovered, selected, redrawn): the full search found
            // nothing new — rest instead of repeating it 30 times a second until the look changes back
            tr.RestUntil = DateTime.UtcNow.AddMilliseconds(RestMs);
            return;
        }
        tr.ContentAcc = 0;
        tr.PrevImg = null;
        tr.Screen = moved;
        tr.MovedBefore = tr.MovedAt;
        tr.MovedAt = DateTime.UtcNow;
        var frame = Native.FrameRect(tr.Root);
        var c = new PointF(moved.X + moved.Width / 2f, moved.Y + moved.Height / 2f);
        // the rest of what was found (the control's box, the window) moves along: only the place changes
        Results[key] = cur with { Rel = new PointF(c.X - frame.X, c.Y - frame.Y), LastRel = cur.Rel, At = DateTime.UtcNow };
    }

    /// <summary>
    /// A step of the follower that is a hover animation, not a move: menus and lists scroll up and down, but a hovered
    /// item slides or indents sideways (up to half a line), and its highlight shifts the best fit by a pixel or two
    /// up or down. Followed, each such step made the mark shake. A real scroll is not lost: the offset is measured
    /// from where the mark stays, so it grows past this and is followed then; a sideways scroll moves further at once.
    /// </summary>
    internal static bool Jitter(int dx, int dy, int lineHeight) =>
        dy == 0 ? Math.Abs(dx) <= Math.Max(6, lineHeight / 2) : Math.Abs(dy) <= 2 && Math.Abs(dx) <= 3;

    /// <summary>
    /// A fast flick moves the text further than the near search looks: its own column over the whole area.
    /// </summary>
    private static void FollowFar(string key, Track tr, FindResult cur)
    {
        var col = Rectangle.Intersect(new Rectangle(tr.Screen.X - 6, (int)tr.View.Y, tr.W + 12, (int)tr.View.Height), Rectangle.Round(tr.View));
        col.Intersect(Native.FrameRect(tr.Root));
        if (col.Width < tr.W || col.Height < tr.H) { Lost(key, tr, cur); return; }
        using var shot = ScreenCapture.CaptureWindow(tr.Root, col, 30);
        if (shot == null || shot.Width != col.Width || shot.Height != col.Height) return;
        var img = Template.GrayFull(shot);
        int iw = shot.Width, ih = shot.Height;
        float Score(int x, int y, int step)
        {
            if (x < 0 || y < 0 || x + tr.W > iw || y + tr.H > ih) return 1;
            long sad = 0, n = 0;
            for (int i = 0; i < tr.Ink.Length; i += step)
            {
                int o = tr.Ink[i];
                sad += Math.Abs(img[(y + o / tr.W) * iw + x + o % tr.W] - tr.Px[o]);
                n++;
            }
            return n == 0 ? 1 : sad / (float)(n * 255);
        }
        float best = float.MaxValue; int bx = 0, by = 0;
        for (int y = 0; y + tr.H <= ih; y += 2)
            for (int x = 0; x + tr.W <= iw; x += 2)
            {
                float v = Score(x, y, 3);
                if (v < best) { best = v; bx = x; by = y; }
            }
        int fx = bx, fy = by;
        best = float.MaxValue;
        for (int y = by - 2; y <= by + 2; y++)
            for (int x = bx - 2; x <= bx + 2; x++)
            {
                float v = Score(x, y, 1);
                if (v < best) { best = v; fx = x; fy = y; }
            }
        if (best > TrackMaxScore) { Lost(key, tr, cur); return; }
        var moved = new Rectangle(col.X + fx, col.Y + fy, tr.W, tr.H);
        tr.ContentAcc = 0;
        tr.PrevImg = null;
        tr.Screen = moved;
        tr.MovedBefore = tr.MovedAt;
        tr.MovedAt = DateTime.UtcNow;
        var frame = Native.FrameRect(tr.Root);
        var c = new PointF(moved.X + moved.Width / 2f, moved.Y + moved.Height / 2f);
        // the rest of what was found (the control's box, the window) moves along: only the place changes
        Results[key] = cur with { Rel = new PointF(c.X - frame.X, c.Y - frame.Y), LastRel = cur.Rel, At = DateTime.UtcNow };
    }

    /// <summary>
    /// The tracker cannot see it any more: no mark at a stale place. Say it is not found (and whether it was
    /// moving, for which way it went) and have OCR look over the whole area right away.
    /// </summary>
    private static void Lost(string key, Track tr, FindResult cur)
    {
        // gone the way the content scrolled, or followed as it scrolled — or not gone anywhere: repainted (a new
        // theme, a hovered or pressed button) the words only look different, and "scroll down" sent the user looking
        // for a button that never moved. One step of the follower right before is no evidence: a repaint makes it
        // jump to a look-alike spot first
        string? went = tr.Went();
        bool moving = went != null || tr.Scrolling;
        Tracks.TryRemove(key, out _);
        Results[key] = new FindResult(false, default, 1, cur.Rel, DateTime.UtcNow, cur.LastRel, cur.At, moving, went, cur.Size);
        TextSearchedAt.TryRemove(key, out _);
        Wake.Set();
    }

    /// <summary>
    /// The text stays put: does the content next to it (rows below it, or above near the bottom) scroll? A text
    /// that stays while its neighbourhood scrolls is a pinned copy, and the real line moves with the content.
    /// Costs one comparison while nothing changes; ~10 a second while something does.
    /// </summary>
    private static void WatchContent(Track tr, byte[] img, int iw, int ih, Rectangle region, int ly, bool force)
    {
        var now = DateTime.UtcNow;
        if (tr.PrevImg == null || tr.PrevRegion != region || tr.PrevImg.Length != img.Length)
        {
            tr.PrevImg = img; tr.PrevRegion = region; tr.PrevAt = now;
            return;
        }
        if (!force && (now - tr.PrevAt).TotalMilliseconds < 90) return;
        int below = ih - (ly + tr.H + 2), above = ly - 2;
        int y0 = below >= above ? ly + tr.H + 2 : 0, y1 = below >= above ? ih : above;
        if (y1 - y0 >= 24)
        {
            int d = BandShift(tr.PrevImg, img, iw, y0, y1);
            if (d != 0) { tr.ContentAcc += d; tr.ContentAt = now; }
        }
        tr.PrevImg = img; tr.PrevAt = now;
    }

    /// <summary>Vertical shift of rows y0..y1 between two looks (+ = moved down; 0 = still or not a scroll).</summary>
    private static int BandShift(byte[] a, byte[] b, int w, int y0, int y1)
    {
        int h = y1 - y0, max = h / 2;
        double Diff(int d)
        {
            long sum = 0, n = 0;
            for (int y = Math.Max(0, d); y < Math.Min(h, h + d); y += 2)
            {
                int ra = (y0 + y - d) * w, rb = (y0 + y) * w;
                for (int x = 0; x < w; x += 3) { sum += Math.Abs(a[ra + x] - b[rb + x]); n++; }
            }
            return n < 40 ? double.MaxValue : sum / (double)n;
        }
        double still = Diff(0);
        if (still < 2) return 0;
        int best = 0;
        double bestDiff = still;
        for (int d = -max; d <= max; d++)
        {
            if (d == 0) continue;
            double v = Diff(d);
            if (v < bestDiff) { bestDiff = v; best = d; }
        }
        return bestDiff < still * 0.5 && bestDiff < 12 ? best : 0;
    }

    private const int Candidates = 8, Refine = 3;

    /// <summary>
    /// Best position (full-size pixels) by mean absolute difference (0 = identical … 1 = opposite).
    /// The half-size pass is fast but blurs small differences — "808 Kick" and "808 Snare" buttons score
    /// almost alike, and the 2×2 grid may not line up with the template — so the best few half-size
    /// candidates are compared again at full size, ±3 px around each.
    /// </summary>
    private static (int x, int y, float score) Match(byte[] img, int iw, int ih, Template t, byte[] full, int fw, int fh)
    {
        int mw = iw - t.W + 1, mh = ih - t.H + 1;
        if (mw <= 0 || mh <= 0) return (0, 0, 1f);
        var map = new long[mw * mh];
        unsafe
        {
            fixed (byte* pi = img, pt = t.Px)
                for (int y = 0; y < mh; y++)
                    for (int x = 0; x < mw; x++)
                    {
                        long sad = 0;
                        for (int ty = 0; ty < t.H; ty++) sad += Sad(pi + (y + ty) * iw + x, pt + ty * t.W, t.W);
                        map[y * mw + x] = sad;
                    }
        }

        // the best few half-size positions, at least half a template apart
        var picked = new List<(int x, int y)>();
        var taken = new bool[map.Length];
        int rx = Math.Max(1, t.W / 2), ry = Math.Max(1, t.H / 2);
        for (int k = 0; k < Candidates; k++)
        {
            int bi = -1;
            for (int i = 0; i < map.Length; i++)
                if (!taken[i] && (bi < 0 || map[i] < map[bi])) bi = i;
            if (bi < 0) break;
            int cx = bi % mw, cy = bi / mw;
            picked.Add((cx, cy));
            for (int y = Math.Max(0, cy - ry); y <= Math.Min(mh - 1, cy + ry); y++)
                for (int x = Math.Max(0, cx - rx); x <= Math.Min(mw - 1, cx + rx); x++) taken[y * mw + x] = true;
        }

        // full-size comparison around each candidate
        long best = long.MaxValue;
        int bx = 0, by = 0;
        unsafe
        {
            fixed (byte* pf = full, ptf = t.Full)
                foreach (var (cx, cy) in picked)
                    for (int y = Math.Max(0, cy * 2 - Refine); y <= Math.Min(fh - t.FH, cy * 2 + Refine); y++)
                        for (int x = Math.Max(0, cx * 2 - Refine); x <= Math.Min(fw - t.FW, cx * 2 + Refine); x++)
                        {
                            long sad = 0;
                            for (int ty = 0; ty < t.FH && sad < best; ty++) sad += Sad(pf + (y + ty) * fw + x, ptf + ty * t.FW, t.FW);
                            if (sad < best) { best = sad; bx = x; by = y; }
                        }
        }
        long n = (long)t.FW * t.FH;
        return (bx, by, best == long.MaxValue ? 1f : (float)(best / (double)(n * 255)));
    }

    /// <summary>
    /// Every candidate the search weighs, each refined on its own and scored (0 identical … 1 opposite), best first:
    /// how much worse the runner-up is tells whether a template is unique in the picture (anchor_at).
    /// </summary>
    internal static List<(int x, int y, float score)> MatchRanked(Bitmap picture, Template t, int candidates = Candidates)
    {
        var img = Template.Gray(picture, out int iw, out int ih);
        var full = Template.GrayFull(picture);
        int fw = picture.Width, fh = picture.Height;
        int mw = iw - t.W + 1, mh = ih - t.H + 1;
        var res = new List<(int x, int y, float score)>();
        if (mw <= 0 || mh <= 0) return res;
        var map = new long[mw * mh];
        unsafe
        {
            fixed (byte* pi = img, pt = t.Px)
                for (int y = 0; y < mh; y++)
                    for (int x = 0; x < mw; x++)
                    {
                        long sad = 0;
                        for (int ty = 0; ty < t.H; ty++) sad += Sad(pi + (y + ty) * iw + x, pt + ty * t.W, t.W);
                        map[y * mw + x] = sad;
                    }
        }
        var taken = new bool[map.Length];
        int rx = Math.Max(1, t.W / 2), ry = Math.Max(1, t.H / 2);
        long n = (long)t.FW * t.FH;
        for (int k = 0; k < candidates; k++)
        {
            int bi = -1;
            for (int i = 0; i < map.Length; i++)
                if (!taken[i] && (bi < 0 || map[i] < map[bi])) bi = i;
            if (bi < 0) break;
            int cx = bi % mw, cy = bi / mw;
            for (int y = Math.Max(0, cy - ry); y <= Math.Min(mh - 1, cy + ry); y++)
                for (int x = Math.Max(0, cx - rx); x <= Math.Min(mw - 1, cx + rx); x++) taken[y * mw + x] = true;
            long best = long.MaxValue;
            int bx = 0, by = 0;
            unsafe
            {
                fixed (byte* pf = full, ptf = t.Full)
                    for (int y = Math.Max(0, cy * 2 - Refine); y <= Math.Min(fh - t.FH, cy * 2 + Refine); y++)
                        for (int x = Math.Max(0, cx * 2 - Refine); x <= Math.Min(fw - t.FW, cx * 2 + Refine); x++)
                        {
                            long sad = 0;
                            for (int ty = 0; ty < t.FH && sad < best; ty++) sad += Sad(pf + (y + ty) * fw + x, ptf + ty * t.FW, t.FW);
                            if (sad < best) { best = sad; bx = x; by = y; }
                        }
            }
            res.Add((bx, by, best == long.MaxValue ? 1f : (float)(best / (double)(n * 255))));
        }
        return res.OrderBy(c => c.score).ToList();
    }

    /// <summary>
    /// Sum of absolute differences of n bytes. PSADBW sums 16 byte differences in one instruction, which makes the
    /// template search several times faster than a byte at a time; the result is exactly the same.
    /// </summary>
    internal static unsafe long Sad(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length) throw new ArgumentException("lengths differ");
        fixed (byte* pa = a, pb = b) return Sad(pa, pb, a.Length);
    }

    internal static unsafe long Sad(byte* a, byte* b, int n)
    {
        int i = 0;
        long s = 0;
        if (Sse2.IsSupported)
        {
            var acc = Vector128<ulong>.Zero;
            for (; i + 16 <= n; i += 16)
                acc = Sse2.Add(acc, Sse2.SumAbsoluteDifferences(Sse2.LoadVector128(a + i), Sse2.LoadVector128(b + i)).AsUInt64());
            if (i + 8 <= n)
            {
                // the upper halves load as zero on both sides, so they add nothing
                var va = Sse2.LoadScalarVector128((ulong*)(a + i)).AsByte();
                var vb = Sse2.LoadScalarVector128((ulong*)(b + i)).AsByte();
                acc = Sse2.Add(acc, Sse2.SumAbsoluteDifferences(va, vb).AsUInt64());
                i += 8;
            }
            s = (long)(acc.GetElement(0) + acc.GetElement(1));
        }
        for (; i < n; i++) s += Math.Abs(a[i] - b[i]);
        return s;
    }
}
