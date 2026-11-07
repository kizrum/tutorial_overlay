using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TutorialOverlay.Tests;

/// <summary>
/// OCR bench: saved screenshots of real apps, each with a text to look for and where it truly is. Every case goes
/// through the same path as a live text anchor (Finder.FindIn: the read, the wordings, reading order, find.index),
/// so a change to the OCR, its preprocessing or the matching shows up here as numbers instead of a feeling.
///
/// The set is local (examples/ocr/, gitignored like every example): cases.json and the pictures it names.
///   { "cases": [ { "id": "fl-options", "image": "fl_main.png", "find": { "text": "OPTIONS" }, "truth": [x, y, w, h] },
///                { "id": "fl-no-slot", "image": "fl_main.png", "find": { "text": "Slot 9" }, "truth": null } ] }
/// find is an anchor's find as written in a tutorial (text, match, lang, index, within); truth is in the picture's
/// pixels, null when the text must NOT be found. Run:
///   dotnet run --project tests -- --ocr-bench [path/to/cases.json]
/// Each run is saved to examples/ocr/runs/ and compared with the previous one: what changed is listed first.
/// </summary>
internal static class OcrBench
{
    private sealed record Outcome(string Id, string Result, int Occurrences, double? LocError, long Ms, string? Picked, string? Note);

    // hit: the mark lands on the truth; wrong: on something else (the truth among the occurrences → "wrong-index");
    // miss: not found though it is there; false: found where it must not be; reject: rightly not found
    private static readonly string[] Order = { "hit", "reject", "wrong-index", "wrong", "miss", "false" };

    public static int Run(string[] args)
    {
        var path = args.Length > 0 ? Path.GetFullPath(args[0]) : DefaultCases();
        if (path == null || !File.Exists(path))
        {
            Console.WriteLine("no OCR bench set: put cases.json and its screenshots in examples/ocr/ (see tests/OcrBench.cs)");
            return 0;
        }
        var dir = Path.GetDirectoryName(path)!;
        var root = JsonNode.Parse(File.ReadAllText(path));
        var cases = J.Items(J.Get(root, "cases")).ToList();
        var pictures = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        var outcomes = new List<Outcome>();
        try
        {
            foreach (var c in cases)
            {
                string id = J.Str(c, "id") ?? $"#{outcomes.Count}";
                try { outcomes.Add(RunCase(c, id, dir, pictures)); }
                catch (Exception e) { outcomes.Add(new Outcome(id, "error", 0, null, 0, null, e.Message)); }
            }
        }
        finally { foreach (var b in pictures.Values) b.Dispose(); }

        Report(outcomes);
        var runs = Path.Combine(dir, "runs");
        Directory.CreateDirectory(runs);
        var previous = Directory.GetFiles(runs, "*.json").OrderBy(f => f).LastOrDefault();
        if (previous != null) Compare(previous, outcomes);
        var file = Path.Combine(runs, DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
        File.WriteAllText(file, JsonSerializer.Serialize(outcomes, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"saved {file}");
        return 0;
    }

    /// <summary>
    /// --ocr-capture app name [window-title-part]: the app's biggest window (or the one whose title contains the
    /// part) captured the way a live search reads it (its own pixels, full size) to name.png in examples/ocr/, with
    /// what helps to label it: name.uia.txt (describe: elements with rel = position in the picture, a truth that does
    /// not come from OCR) and name.ocr.txt (what OCR reads now — check it by eye before taking it as a truth).
    /// </summary>
    public static int Capture(string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("--ocr-capture <process> <name> [window title part]"); return 1; }
        Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        string process = AnchorSpec.NormalizeProcess(args[0])!, name = args[1];
        string? part = args.Length > 2 ? args[2] : null;
        var dir = Path.GetDirectoryName(DefaultCases() ?? throw new Exception("repository root not found"))!;
        Directory.CreateDirectory(dir);
        var pids = Resolver.Pids(process);
        var win = Resolver.EnumTopWindows()
            .Where(w => pids.Contains(w.Pid) && w.Frame.Width > 1 && w.Frame.Height > 1 && !Native.IsIconic(w.Hwnd))
            .Where(w => part == null || w.Title.Contains(part, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(w => (long)w.Frame.Width * w.Frame.Height).FirstOrDefault();
        if (win == null) { Console.WriteLine($"no visible window of \"{process}\""); return 1; }
        var frame = Native.FrameRect(win.Hwnd);
        using var shot = ScreenCapture.CaptureWindow(win.Hwnd, Rectangle.Round(frame), maxAgeMs: 0);
        if (shot == null) { Console.WriteLine("the window could not be captured"); return 1; }
        shot.Save(Path.Combine(dir, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
        var uia = new JsonObject { ["app"] = process, ["namedOnly"] = true, ["maxNodes"] = 3000 };
        if (part != null) uia["window"] = part;
        File.WriteAllText(Path.Combine(dir, name + ".uia.txt"), Describe.Run(uia, default));
        var lines = Ocr.ReadTiled(shot, null, 1f);
        File.WriteAllLines(Path.Combine(dir, name + ".ocr.txt"),
            lines.Select(l => $"[{l.Bounds.X:0}, {l.Bounds.Y:0}, {l.Bounds.Width:0}, {l.Bounds.Height:0}]  {l.Text}"));
        Console.WriteLine($"{name}.png {shot.Width}x{shot.Height} from \"{win.Title}\" ({frame}); {lines.Count} OCR lines; in {dir}");
        return 0;
    }

    /// <summary>--ocr-read image [query]: the lines as a search for that query reads them (its recognizer, its budget).</summary>
    public static int ReadOut(string[] args)
    {
        if (args.Length < 1) { Console.WriteLine("--ocr-read <image> [query]"); return 1; }
        using var pic = new Bitmap(args[0]);
        var f = FindSpec.Parse(new JsonObject { ["text"] = args.Length > 1 ? args[1] : "a" })!;
        foreach (var l in Finder.ReadForBench(pic, f))
            Console.WriteLine($"[{l.Bounds.X:0}, {l.Bounds.Y:0}, {l.Bounds.Width:0}, {l.Bounds.Height:0}]  " + string.Join(" | ", l.Words.Select(w => w.Text)));
        return 0;
    }

    private static string? DefaultCases()
    {
        for (int up = 0; up < 8; up++)
        {
            var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, string.Concat(Enumerable.Repeat("../", up))));
            if (File.Exists(Path.Combine(dir, "TutorialOverlay.csproj"))) return Path.Combine(dir, "examples", "ocr", "cases.json");
        }
        return null;
    }

    private static Outcome RunCase(JsonNode c, string id, string dir, Dictionary<string, Bitmap> pictures)
    {
        var image = J.Str(c, "image") ?? throw new Exception("no image");
        if (!pictures.TryGetValue(image, out var pic))
            pictures[image] = pic = new Bitmap(Path.Combine(dir, image));
        var f = FindSpec.Parse(J.Get(c, "find")) ?? throw new Exception("no find");
        if (f.Text == null) throw new Exception("find has no text (the bench is for OCR)");
        RectangleF? truth = J.Arr(c, "truth") is JsonArray t && t.Count >= 4
            ? new RectangleF((float)(J.AsNum(t[0]) ?? 0), (float)(J.AsNum(t[1]) ?? 0), (float)(J.AsNum(t[2]) ?? 0), (float)(J.AsNum(t[3]) ?? 0))
            : null;

        // within: only that part of the picture, as a live search reads only that part of the element
        var area = new Rectangle(0, 0, pic.Width, pic.Height);
        if (f.Within is RectangleF w) area = Rectangle.Intersect(area, Rectangle.Round(w));
        using var part = pic.Clone(area, pic.PixelFormat);
        var sw = Stopwatch.StartNew();
        var found = Finder.FindIn(part, f);
        long ms = sw.ElapsedMilliseconds;

        PointF Centre(RectangleF r) => new(area.X + r.X + r.Width / 2, area.Y + r.Y + r.Height / 2);
        bool In(PointF p, RectangleF r) { r.Inflate(4, 4); return r.Contains(p); }
        string? note = J.Str(c, "note");
        if (found.Count <= f.Nth)
            return new Outcome(id, truth == null ? "reject" : "miss", found.Count, null, ms, null, note);
        var pick = Centre(found[f.Nth].Rect);
        string picked = $"\"{found[f.Nth].Text}\" @({pick.X:0},{pick.Y:0})";
        if (truth is not RectangleF tr) return new Outcome(id, "false", found.Count, null, ms, picked, note);
        var tc = new PointF(tr.X + tr.Width / 2, tr.Y + tr.Height / 2);
        double err = Math.Round(Math.Sqrt((pick.X - tc.X) * (pick.X - tc.X) + (pick.Y - tc.Y) * (pick.Y - tc.Y)), 1);
        if (In(pick, tr)) return new Outcome(id, "hit", found.Count, err, ms, picked, note);
        bool elsewhere = found.Any(m => In(Centre(m.Rect), tr));
        return new Outcome(id, elsewhere ? "wrong-index" : "wrong", found.Count, err, ms, picked, note);
    }

    private static void Report(List<Outcome> all)
    {
        Console.WriteLine();
        foreach (var o in all.OrderBy(o => Array.IndexOf(Order, o.Result) is var i && i < 0 ? 99 : i).ThenBy(o => o.Id))
        {
            string rep = o.Occurrences > 1 ? $" ×{o.Occurrences}" : "";
            string loc = o.LocError is double e ? $" off {e:0.#}px" : "";
            Console.WriteLine($"  {o.Result,-11} {o.Id,-28} {o.Ms,5} ms{rep}{loc}{(o.Picked != null ? "  " + o.Picked : "")}{(o.Note != null ? "  — " + o.Note : "")}");
        }
        int there = all.Count(o => o.Result is "hit" or "miss" or "wrong" or "wrong-index");
        int absent = all.Count(o => o.Result is "reject" or "false");
        int hit = all.Count(o => o.Result == "hit");
        int wrong = all.Count(o => o.Result is "wrong" or "wrong-index" or "false");
        int picks = all.Count(o => o.Picked != null);
        var errs = all.Where(o => o.Result == "hit" && o.LocError != null).Select(o => o.LocError!.Value).ToList();
        var times = all.Select(o => o.Ms).OrderBy(x => x).ToList();
        Console.WriteLine();
        Console.WriteLine($"  cases {all.Count} (text there {there}, must not be found {absent}), errors {all.Count(o => o.Result == "error")}");
        Console.WriteLine($"  recall        {Pct(hit, there)}   the mark lands on the target");
        Console.WriteLine($"  false match   {Pct(wrong, picks)}   of the marks placed, on the wrong thing");
        Console.WriteLine($"  repeated      {all.Count(o => o.Occurrences > 1)}   cases where the text is there more than once");
        if (errs.Count > 0) Console.WriteLine($"  centre off    mean {errs.Average():0.#}px, max {errs.Max():0.#}px (hits)");
        if (times.Count > 0) Console.WriteLine($"  time          median {times[times.Count / 2]} ms, max {times[^1]} ms");
    }

    private static string Pct(int n, int of) => of == 0 ? "  —  " : $"{100.0 * n / of,4:0}% ({n}/{of})";

    private static void Compare(string previousFile, List<Outcome> now)
    {
        List<Outcome>? before;
        try { before = JsonSerializer.Deserialize<List<Outcome>>(File.ReadAllText(previousFile)); }
        catch { return; }
        if (before == null) return;
        var was = before.ToDictionary(o => o.Id, o => o);
        var changes = now.Where(o => was.TryGetValue(o.Id, out var b) && b.Result != o.Result)
                         .Select(o => $"  {o.Id}: {was[o.Id].Result} → {o.Result}").ToList();
        long tb = before.Sum(o => o.Ms), tn = now.Sum(o => o.Ms);
        Console.WriteLine();
        Console.WriteLine($"  against {Path.GetFileName(previousFile)}: " +
                          (changes.Count == 0 ? "no case changed" : $"{changes.Count} changed") + $", total time {tb} → {tn} ms");
        foreach (var c in changes) Console.WriteLine(c);
    }
}
