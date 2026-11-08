using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace TutorialOverlay.Tests;

/// <summary>
/// Screen-independent tests. Each public static void method named Test* runs; a failed Check throws.
/// Exit code = number of failed tests.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--ocr-bench") return OcrBench.Run(args.Skip(1).ToArray());
        if (args.Length > 0 && args[0] == "--ocr-read") return OcrBench.ReadOut(args.Skip(1).ToArray());
        if (args.Length > 0 && args[0] == "--ocr-capture") return OcrBench.Capture(args.Skip(1).ToArray());
        if (args.Length > 0 && args[0] == "--autopilot") return Autopilot.Run(args.Skip(1).ToArray());
        var filter = args.Length > 0 ? args[0] : null;
        var tests = typeof(Program).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            .Where(m => m.Name.StartsWith("Test") && m.GetParameters().Length == 0)
            .Where(m => filter == null || $"{m.DeclaringType!.Name}.{m.Name}".Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.DeclaringType!.Name).ThenBy(m => m.Name)
            .ToList();
        int failed = 0;
        foreach (var t in tests)
        {
            var name = $"{t.DeclaringType!.Name}.{t.Name}";
            try
            {
                t.Invoke(null, null);
                Console.WriteLine($"  ok    {name}");
            }
            catch (System.Reflection.TargetInvocationException e)
            {
                failed++;
                Console.WriteLine($"  FAIL  {name}: {e.InnerException?.Message}");
            }
        }
        Console.WriteLine(failed == 0 ? $"all {tests.Count} passed" : $"{failed} of {tests.Count} failed");
        return failed;
    }
}

internal static class A
{
    public static void Check(bool cond, string what)
    {
        if (!cond) throw new Exception(what);
    }

    public static void Eq<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{what}: expected <{expected}>, got <{actual}>");
    }

    public static JsonNode Json(string s) => JsonNode.Parse(s)!;
}

public static class JsonTests
{
    public static void TestNumbersFromStringsAndNumbers()
    {
        var o = A.Json("""{"a": 3, "b": "4.5", "c": "x", "d": true}""");
        A.Eq(3.0, J.Num(o, "a"), "number");
        A.Eq(4.5, J.Num(o, "b"), "numeric string");
        A.Eq(null, J.Num(o, "c"), "text");
        A.Eq(true, J.Bool(o, "d"), "bool");
        A.Eq(5, J.Int(A.Json("""{"v": 4.6}"""), "v"), "rounded int");
    }

    public static void TestBoolFromStrings()
    {
        A.Eq(true, J.Bool(A.Json("""{"v": "yes"}"""), "v"), "yes");
        A.Eq(false, J.Bool(A.Json("""{"v": "no"}"""), "v"), "no");
        A.Eq(true, J.Bool(A.Json("""{"v": 1}"""), "v"), "1");
    }

    public static void TestUnwrapDoubleEncoded()
    {
        var o = A.Json("""{"spec": "{\"title\":\"T\"}"}""");
        A.Eq("T", J.Str(J.Unwrap(J.Get(o, "spec")), "title"), "double-encoded object");
        A.Eq(2, J.Items(A.Json("[1, 2]")).Count(), "items of an array");
        A.Eq(1, J.Items(A.Json("""{"x":1}""")).Count(), "single object as one item");
    }

    public static void TestPoints()
    {
        A.Eq(new PointF(1, 2), J.Pt(A.Json("""{"p": [1, 2]}"""), "p"), "array point");
        A.Eq(new PointF(3, 4), J.Pt(A.Json("""{"p": {"x": 3, "y": 4}}"""), "p"), "object point");
    }

    public static void TestColors()
    {
        A.Eq(Color.FromArgb(255, 0x1A, 0xD2, 0x8F).ToArgb(), J.ParseColor("#1AD28F", Color.Black).ToArgb(), "hex");
        A.Eq(Color.FromArgb(255, 0xFF, 0x00, 0xFF).ToArgb(), J.ParseColor("#F0F", Color.Black).ToArgb(), "short hex");
        A.Eq(Color.Black.ToArgb(), J.ParseColor("not a colour", Color.Black).ToArgb(), "fallback");
    }
}

public static class PixelTests
{
    private static bool Eval(string expr, int r, int g, int b) => new PixelCheck { Expr = expr }.Evaluate(Color.FromArgb(r, g, b));

    public static void TestComparisons()
    {
        A.Check(Eval("r>140 && g<100", 200, 50, 0), "and: both true");
        A.Check(!Eval("r>140 && g<100", 200, 150, 0), "and: one false");
        A.Check(Eval("r>250 || b>=100", 0, 0, 100), "or");
        A.Check(Eval("g==128", 0, 128, 0), "equals");
        A.Check(Eval("g!=128", 0, 129, 0), "not equals");
    }

    public static void TestChannelsAndDifference()
    {
        A.Check(Eval("r-g>50", 200, 100, 0), "difference");
        A.Check(Eval("sat>100", 250, 100, 100), "saturation");
        A.Check(Eval("lum<30", 10, 10, 10), "luminance");
        A.Check(!Eval("foo>1", 255, 255, 255), "unknown channel is never true");
    }

    public static void TestColourDistance()
    {
        A.Check(Eval("~#1AD28F:40", 0x1A + 10, 0xD2 - 10, 0x8F), "near the colour");
        A.Check(!Eval("~#1AD28F:10", 0, 0, 0), "far from the colour");
    }
}

public static class KeyTests
{
    public static void TestParse()
    {
        var c = Keys.Parse("Ctrl+Alt+Right")!.Value;
        A.Eq(Native.MOD_CONTROL | Native.MOD_ALT, c.Mods, "mods");
        A.Eq(0x27u, c.Vk, "right arrow");
        A.Eq(0x75u, Keys.Parse("F6")!.Value.Vk, "F6");
        A.Eq((uint)'S', Keys.Parse("ctrl+s")!.Value.Vk, "letter");
        A.Eq(0xBBu, Keys.Parse("Ctrl++")!.Value.Vk, "plus key");
        A.Eq(null, Keys.Parse("Ctrl+Foo"), "unknown key");
        A.Eq(null, Keys.Parse("Ctrl"), "modifier only");
    }

    public static void TestFormatRoundTrip()
    {
        foreach (var s in new[] { "Ctrl+Alt+Z", "Shift+F6", "Ctrl+S" })
            A.Eq(Keys.Parse(s), Keys.Parse(Keys.Parse(s)!.Value.ToString()), "round trip " + s);
    }

    public static void TestSettingsHotkeys()
    {
        var s = new Settings().Apply(A.Json("""{"hotkeys": {"done": "Ctrl+Shift+D", "stop": "none"}, "lang": "en", "port": 99999}"""));
        A.Eq(Keys.Parse("Ctrl+Shift+D"), (Chord?)s.Hotkeys["done"], "hotkey replaced");
        A.Check(!s.Hotkeys.ContainsKey("stop"), "'none' turns a hotkey off");
        A.Eq("en", s.Lang, "language");
        A.Eq(null, s.Port, "out-of-range port ignored");
    }
}

public static class ModelTests
{
    public static void TestGridExpands()
    {
        var cells = Grid.Expand(A.Json("""{"id":"c","name":"Step {col}","grid":{"from":[10,20],"step":[24,0],"cells":[1,3]}}""")).ToList();
        A.Eq(2, cells.Count, "two cells");
        A.Eq("c-1-3", J.Str(cells[1], "id"), "cell id");
        A.Eq("Step 3", J.Str(cells[1], "name"), "name template");
        A.Eq(new PointF(58, 20), J.Pt(cells[1], "__cell"), "cell centre = from + (col-1)·step");
    }

    public static void TestStepDefaults()
    {
        var s = Step.Parse(A.Json("""{"title":"T","numbered":true,"targets":[{"anchor":{"process":"x"}},{"anchor":{"process":"x"},"optional":true}]}"""), null, 0);
        A.Eq(2, s.Targets.Count, "targets");
        A.Eq("all_clicked", s.Until.Single().Type, "default condition");
        A.Check(s.Targets.All(t => t.Shapes.Any(x => x.Type == "badge")), "numbered: badges added");
        A.Eq(1, s.Required.Count(), "optional target not required");
    }

    public static void TestAnchorInheritsTutorialApp()
    {
        var d = AnchorSpec.Parse(A.Json("""{"process":"FL64.exe"}"""), null);
        A.Eq("FL64", d.Process, ".exe dropped");
        var a = AnchorSpec.Parse(A.Json("""{"element":{"name":"Channel rack"},"offset":[5,6]}"""), d);
        A.Eq("FL64", a.Process, "inherited process");
        A.Eq(5f, a.Dx, "offset x");
        var screen = AnchorSpec.Parse(A.Json("""{"screen":true,"offset":[100,200]}"""), d);
        A.Check(screen.IsScreen, "screen:true is not inherited");
    }

    public static void TestConditionAliases()
    {
        A.Eq("appear", Condition.Norm("window_opened"), "alias");
        A.Eq("scroll", Condition.Norm("Wheel"), "case-insensitive");
        A.Eq("untill", Condition.Norm("untill"), "unknown stays as is");
        var key = Condition.Parse(A.Json("""{"type":"key","keys":["F6","Ctrl+S"]}"""), null);
        A.Eq(2, key.Keys.Count, "key chords");
        var thrown = false;
        try { Condition.Parse(A.Json("""{"type":"key","keys":"Ctrl+Nope"}"""), null); } catch (ArgumentException) { thrown = true; }
        A.Check(thrown, "bad key name is an error");
    }

    public static void TestTutorialState()
    {
        var t = new Tutorial();
        A.Check(t.Active, "running is active");
        A.Eq("running", t.StateName, "reported name");
        t.State = TutorialState.Waiting;
        A.Check(t.Active, "waiting is active");
        t.State = TutorialState.Stopped;
        A.Check(!t.Active, "stopped is not");
    }
}

public static class LintTests
{
    public static void TestCleanSpecHasNoWarnings()
    {
        var spec = A.Json("""
        { "title": "T", "app": "notepad", "steps": [
            { "id": "a", "title": "Click", "targets": [ { "name": "Bold", "anchor": { "element": [{ "name": "Bold", "role": "Button" }], "offset": [2, 3] },
                                                         "shapes": [{ "type": "circle", "r": 20 }], "check": { "at": [1, 2], "expr": "g>140" } } ],
              "until": [ "all_clicked", { "type": "appear", "title": "Font" }, { "type": "scroll", "in": { "element": { "name": "List" } }, "notches": 3 } ],
              "buttons": [{ "id": "y", "text": "Yes" }], "controls": ["back", "done"] } ] }
        """);
        var w = SpecLint.CheckSpec(spec);
        A.Eq(0, w.Count, "warnings: " + string.Join("; ", w));
    }

    public static void TestTyposAreReportedWithAGuess()
    {
        var spec = A.Json("""{ "title": "T", "steps": [ { "title": "x", "untill": ["all_clicked"], "tagrets": [] , "targets": [ { "ancor": {} } ] } ] }""");
        var w = SpecLint.CheckSpec(spec);
        A.Check(w.Any(x => x.StartsWith("steps[0].untill") && x.Contains("'until'")), "untill → until: " + string.Join("; ", w));
        A.Check(w.Any(x => x.StartsWith("steps[0].tagrets") && x.Contains("'targets'")), "swapped letters → targets");
        A.Check(w.Any(x => x.StartsWith("steps[0].targets[0].ancor") && x.Contains("'anchor'")), "nested target field");
    }

    public static void TestUnknownTypes()
    {
        var spec = A.Json("""{ "steps": [ { "until": [{ "type": "aper" }], "targets": [ { "shapes": [{ "type": "circel" }] } ], "controls": ["dne"] } ] }""");
        var w = SpecLint.CheckSpec(spec);
        A.Check(w.Any(x => x.Contains("unknown condition 'aper'")), "condition type");
        A.Check(w.Any(x => x.Contains("unknown shape 'circel'") && x.Contains("'circle'")), "shape type");
        A.Check(w.Any(x => x.Contains("unknown control 'dne'")), "control");
    }

    public static void TestDrawArguments()
    {
        A.Eq(0, SpecLint.CheckDraw(A.Json("""{"anchor":{"process":"x"},"shapes":[{"type":"label","text":"hi"}],"id":"m"}""")).Count, "clean draw");
        A.Check(SpecLint.CheckDraw(A.Json("""{"shapes":[],"dismisOnClick":false}""")).Any(x => x.Contains("'dismissOnClick'")), "draw typo");
    }

    public static void TestDistance()
    {
        A.Eq(1, SpecLint.Distance("tagrets", "targets"), "transposition is one edit");
        A.Eq(1, SpecLint.Distance("untill", "until"), "one extra letter");
    }
}

public static class ScaleTests
{
    public static void TestPixelFieldsScaled()
    {
        var steps = A.Json("""
        [ { "title": "x", "anchor": { "process": "p", "offset": [10, 20], "hwnd": 1234 },
            "targets": [ { "shapes": [{ "type": "circle", "x": 4, "y": 6, "r": 10, "thickness": 3 }], "grid": { "from": [2, 2], "step": [10, 0], "cols": 4 } } ],
            "until": [ { "type": "trace", "path": [[0, 0], [100, 50]], "tolerance": 24 }, { "type": "drag", "from": { "anchor": { "process": "p" }, "rect": [0, 0, 10, 10] } } ] } ]
        """);
        SpecScale.Apply(steps, 2);
        var s = steps[0]!;
        A.Eq("[20,40]", s["anchor"]!["offset"]!.ToJsonString(), "anchor offset");
        A.Eq(1234.0, J.Num(s["anchor"], "hwnd"), "hwnd untouched");
        var sh = s["targets"]![0]!["shapes"]![0]!;
        A.Eq(8.0, J.Num(sh, "x"), "shape x");
        A.Eq(20.0, J.Num(sh, "r"), "radius");
        A.Eq(3.0, J.Num(sh, "thickness"), "line width (already DPI-scaled) untouched");
        A.Eq("[20,0]", s["targets"]![0]!["grid"]!["step"]!.ToJsonString(), "grid step");
        A.Eq(4.0, J.Num(s["targets"]![0]!["grid"], "cols"), "count untouched");
        A.Eq("[[0,0],[200,100]]", s["until"]![0]!["path"]!.ToJsonString(), "trace path");
        A.Eq(24.0, J.Num(s["until"]![0], "tolerance"), "tolerance (scaled at run time) untouched");
        A.Eq("[0,0,20,20]", s["until"]![1]!["from"]!["rect"]!.ToJsonString(), "drag rect inside an object");
        A.Eq("p", J.Str(s["until"]![1]!["from"]!["anchor"], "process"), "anchor object itself untouched");
    }

    public static void TestArrowDirectionUntouched()
    {
        var n = A.Json("""[{ "shapes": [{ "type": "arrow", "from": "nw" }] }]""");
        SpecScale.Apply(n, 1.5);
        A.Eq("nw", J.Str(n[0]!["shapes"]![0], "from"), "direction word");
    }
}

public static class LibraryTests
{
    public static void TestSlug()
    {
        A.Eq("fl-studio-урок-1", Tutorials.Slug("  FL Studio: урок #1 "), "letters of any script kept");
        A.Eq("con-1", Tutorials.Slug("Con"), "reserved device name");
        A.Eq("nul-1", Tutorials.Slug("NUL"), "reserved, any case");
        A.Check(Tutorials.Slug(new string('a', 100)).Length <= 60, "length limit");
    }

    public static void TestTemplatesUsedBySpec()
    {
        var spec = A.Json("""
        { "steps": [ { "anchor": { "find": { "template": "kick" } },
                       "targets": [ { "anchor": { "find": "snare" } }, { "anchor": { "find": { "text": "Kick" } } } ] } ],
          "templates": { "old": "AAAA" } }
        """);
        var ids = TemplateStore.UsedBy(spec);
        A.Check(ids.SetEquals(new[] { "kick", "snare" }), "template ids: " + string.Join(",", ids));
    }

    public static void TestTemplateFileNamesKeepIdsApart()
    {
        A.Check(TemplateStore.FileOf("C5") != TemplateStore.FileOf("c5"), "case matters");
        A.Check(TemplateStore.FileOf("a/b") != TemplateStore.FileOf("a-b"), "punctuation matters");
    }
}

public static class RankTests
{
    private static List<OcrLine> Lines(params (string text, float y)[] rows)
    {
        var res = new List<OcrLine>();
        foreach (var (t, y) in rows)
        {
            var l = new OcrLine();
            float x = 0;
            foreach (var w in t.Split(' ')) { l.Words.Add(new OcrWord(w, new RectangleF(x, y, w.Length * 8, 14))); x += w.Length * 8 + 6; }
            res.Add(l);
        }
        return res;
    }

    /// <summary>The line the mark goes to (by its y), as the search picks: Rank, then find.index.</summary>
    private static float Pick(List<OcrLine> lines, string find)
    {
        var f = FindSpec.Parse(A.Json(find))!;
        var found = Finder.MatchAny(lines, f).OrderBy(m => Math.Round(m.Rect.Y / 8)).ThenBy(m => m.Rect.X).ToList();
        return Finder.Rank(found, lines, f)[f.Nth].Rect.Y;
    }

    public static void TestWholeCaptionBeforePartOfALongerOne()
    {
        var themes = Lines(("Dark orange", 0), ("Dark", 100));
        A.Eq(100f, Pick(themes, "{\"text\":\"Dark\"}"), "the theme Dark, not Dark orange above it");
        A.Eq(0f, Pick(themes, "{\"text\":\"Dark\",\"index\":0}"), "index counts in reading order");
    }

    public static void TestOwnLetterCaseBreaksATie()
    {
        var browser = Lines(("Bassdrum 22", 0), ("W BassDrum", 100));
        A.Eq(100f, Pick(browser, "{\"text\":\"BassDrum\"}"), "the author's case");
        A.Eq(0f, Pick(browser, "{\"text\":\"bassdrum\"}"), "no capitals given: reading order");
    }
}

public static class ControlBoxTests
{
    /// <summary>A grey picture (bg 40) with a text block and full-width border lines at the given rows.</summary>
    private static byte[] Pic(int w, int h, Rectangle text, params int[] borderRows)
    {
        var g = new byte[w * h];
        Array.Fill(g, (byte)40);
        foreach (var y in borderRows) for (int x = 0; x < w; x++) g[y * w + x] = 160;
        for (int y = text.Y; y < text.Bottom; y++) for (int x = text.X; x < text.Right; x += 3) g[y * w + x] = 220;
        return g;
    }

    public static void TestCentredCaptionIsItsButton()
    {
        var t = new Rectangle(100, 50, 80, 20);
        var box = Icons.ControlBox(Pic(300, 120, t, 41, 79), 300, 120, t);
        A.Check(box is Rectangle b && b.Top <= 42 && b.Bottom >= 79, "a box around the caption: " + box);
    }

    public static void TestTwoButtonsOnOneStripAreTwo()
    {
        // one strip (borders above and below), "Reset" and "Save preset" on it with a faint divider: no edge between
        var t = new Rectangle(160, 50, 80, 20);
        var g = Pic(300, 120, t, 41, 79);
        for (int y = 50; y < 70; y++) for (int x = 60; x < 120; x += 3) g[y * 300 + x] = 220;   // the other caption
        var box = Icons.ControlBox(g, 300, 120, t);
        A.Check(box is Rectangle b && b.Left > 120, "the frame stops before the other caption: " + box);
    }

    public static void TestDropDownKeepsItsArrow()
    {
        var t = new Rectangle(100, 50, 80, 20);
        var g = Pic(300, 120, t, 41, 79);
        for (int y = 56; y < 64; y++) for (int x = 200; x < 208; x++) g[y * 300 + x] = 220;   // a small arrow
        for (int y = 41; y < 80; y++) g[y * 300 + 230] = 160;                                  // the box's right edge
        var box = Icons.ControlBox(g, 300, 120, t);
        A.Check(box is Rectangle b && b.Right >= 208, "the arrow is part of the drop-down: " + box);
    }

    public static void TestCaptionPressedToOneEdgeIsNotABox()
    {
        // a tile caption: its picture 6 px above, the next tile 30 px below
        var t = new Rectangle(100, 50, 80, 20);
        var box = Icons.ControlBox(Pic(300, 140, t, 43, 100), 300, 140, t);
        A.Check(box is not Rectangle b || b.Bottom < 90, "the frame must not reach the next tile: " + box);
    }
}

public static class ContentShiftTests
{
    /// <summary>A picture: a still sidebar on the left quarter, a striped page on the rest, the page moved by dy.</summary>
    private static byte[] Pic(int w, int h, int dy)
    {
        var g = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                g[y * w + x] = x < w / 4 ? (byte)((y / 7) % 2 == 0 ? 60 : 120)          // the sidebar's own rows, still
                                         : (byte)(((y + dy) / 5 * 37 + x / 9 * 11) % 200 + 20);   // the page, moving
        return g;
    }

    public static void TestPageScrollingBesideAStillSidebar()
    {
        int w = 200, h = 160;
        int shift = Engine.ContentShift(Pic(w, h, 0), Pic(w, h, 20), w, h, true, out _, out _);
        A.Check(shift != 0, "the page moved while the list of sections stayed: " + shift);
        A.Check(shift < 0, "content moved up = scrolled down: " + shift);
    }

    public static void TestNothingMoved()
    {
        int w = 200, h = 160;
        A.Eq(0, Engine.ContentShift(Pic(w, h, 0), Pic(w, h, 0), w, h, true, out _, out _), "still");
    }
}

public static class JitterTests
{
    public static void TestHoverShakeIsNotAMove()
    {
        A.Check(Finder.Jitter(5, 0, 16), "a hovered item sliding sideways");
        A.Check(Finder.Jitter(8, 0, 16), "up to half a line sideways");
        A.Check(Finder.Jitter(2, 1, 16), "the highlight shifting the fit a pixel");
    }

    public static void TestScrollIsStillFollowed()
    {
        A.Check(!Finder.Jitter(0, 3, 16), "a slow scroll step");
        A.Check(!Finder.Jitter(0, -24, 16), "a line up");
        A.Check(!Finder.Jitter(30, 0, 16), "a sideways scroll");
    }
}

public static class FindInTests
{
    private static FindSpec F(string json) => FindSpec.Parse(A.Json(json))!;

    public static void TestWindowByTitleStartAndClass()
    {
        var f = F("{\"text\":\"Theme\",\"in\":{\"title\":\"Settings\",\"class\":\"TMIDIForm\"}}");
        A.Check(f.InMatches("Settings - Look and feel", "TMIDIForm"), "another tab of the same window");
        A.Check(f.InMatches("Settings - Audio devices / mixer", "TMIDIForm"), "and another");
        A.Check(!f.InMatches("", "TQuickPopupMenuWindow"), "not a menu");
        A.Check(!f.InMatches("Settings - x", "TOther"), "class differs");
    }

    public static void TestMenuByClassAndMain()
    {
        var menu = F("{\"text\":\"Audio settings\",\"in\":{\"class\":\"TQuickPopupMenuWindow\"}}");
        A.Check(menu.InMatches("", "TQuickPopupMenuWindow"), "a menu without a title");
        A.Check(!menu.InMatches("Settings - Look and feel", "TMIDIForm"), "not the dialog");
        var main = F("{\"text\":\"OPTIONS\",\"in\":\"main\"}");
        A.Check(main.InMain && !main.InMatches("", "TQuickPopupMenuWindow"), "main: only the main window");
        A.Eq("main", main.InJson()!.ToString(), "written back as main");
        A.Check(F("{\"text\":\"OPTIONS\"}").Key != main.Key, "in is part of the key");
    }

    public static void TestLintKnowsIn()
    {
        var w = SpecLint.CheckSteps(A.Json("[{\"title\":\"t\",\"targets\":[{\"anchor\":{\"process\":\"FL64\",\"find\":{\"text\":\"OPTIONS\",\"in\":\"main\"}}}]}]"), 0);
        A.Eq(0, w.Count, "find.in is a known field: " + string.Join("; ", w));
    }
}

public static class RepeatTests
{
    private static FindSpec F(string json) => FindSpec.Parse(A.Json(json))!;
    private static readonly List<PointF> Two = new() { new PointF(10, 20), new PointF(300, 400) };

    public static void TestRepeatedTextIsFlagged()
    {
        var note = Engine.RepeatNote(F("{\"text\":\"Save\"}"), Two);
        A.Check(note != null && note.Contains("2 times") && note.Contains("(300,400)"), "two places, both listed: " + note);
    }

    public static void TestPinnedOrSingleTextIsNot()
    {
        A.Eq(null, Engine.RepeatNote(F("{\"text\":\"Save\",\"index\":1}"), Two), "index chosen by the author");
        A.Eq(null, Engine.RepeatNote(F("{\"text\":\"Save\",\"index\":0}"), Two), "index 0 given explicitly");
        A.Eq(null, Engine.RepeatNote(F("{\"text\":\"Save\"}"), new List<PointF> { new(1, 1) }), "only once");
        A.Eq(null, Engine.RepeatNote(F("{\"text\":\"Save\"}"), null), "followed / not counted");
        A.Eq(null, Engine.RepeatNote(F("{\"template\":\"play\"}"), Two), "templates have their own tie check");
    }
}

public static class OcrTests
{
    private static List<OcrLine> Lines(params string[] texts)
    {
        var res = new List<OcrLine>();
        int y = 0;
        foreach (var t in texts)
        {
            var l = new OcrLine();
            int x = 0;
            foreach (var w in t.Split(' ')) { l.Words.Add(new OcrWord(w, new RectangleF(x, y, w.Length * 8, 14))); x += w.Length * 8 + 6; }
            res.Add(l);
            y += 20;
        }
        return res;
    }

    public static void TestContainsAndExact()
    {
        var lines = Lines("808 Kick", "808 Snare", "Kicks");
        A.Eq(2, Ocr.Find(lines, "kick").Count, "contains: 'Kick' and the 'Kick' inside 'Kicks'");
        A.Eq("808 Kick", Ocr.Find(lines, "808 kick", "exact").Single().Text, "exact line");
    }

    public static void TestLookAlikeLettersForgivenInWholeWords()
    {
        var lines = Lines("Channel rack", "Resample");
        A.Eq(1, Ocr.Find(lines, "Channe1 rack").Count, "1 read for l");
        A.Eq(0, Ocr.Find(lines, "Sample").Count(m => m.Score > 0), "no fuzzy match inside a longer word");
    }

    public static void TestOtherCharactersAreNotForgiven()
    {
        // FL's channel menu: while "Fill each 2 steps" is hovered OCR may not read it, and the mark must not jump to 8.
        var menu = Lines("Fill each 4 steps", "Fill each 8 steps");
        A.Eq(0, Ocr.Find(menu, "Fill each 2 steps").Count, "8 is not a look-alike of 2");
        A.Eq(0, Ocr.Find(menu, "Fill each 2 steps", "exact").Count, "exact: 8 is not a look-alike of 2");
        A.Eq(1, Ocr.Find(Lines("Fill each Z steps"), "Fill each 2 steps").Count, "Z read for 2 is forgiven");
        A.Eq(0, Ocr.Find(Lines("Pattern 12"), "Pattern 1", "exact").Count, "an extra digit is a different line");
        A.Eq(1, Ocr.Find(Lines("Pattern 1"), "Pattern l", "exact").Count, "l read for 1");
        // a menu bar read as one line: items 10 px apart (a word space here is 6 px)
        var bar = new OcrLine();
        int bx = 0;
        foreach (var w in new[] { "Steam", "View", "Friends", "Games", "Help" }) { bar.Words.Add(new OcrWord(w, new RectangleF(bx, 0, w.Length * 8, 14))); bx += w.Length * 8 + 10; }
        var hit = Ocr.Find(new List<OcrLine> { bar }, "Steam", "exact");
        A.Eq(1, hit.Count, "an item of a menu bar matches exactly");
        A.Check(hit[0].Rect.Width == 40, "framed on the item alone");
        A.Eq(0, Ocr.Find(Lines("Audio settings"), "Audio", "exact").Count, "a word of a phrase is still not the whole line");
        A.Eq(1, Ocr.Find(Lines("Мaстер"), "Мастер").Count, "Latin a read for Cyrillic а");
        A.Eq(1, Ocr.Find(Lines("Presets", "Save preset...", "Reset"), "Reset").Count, "not inside another word: only the button");
        A.Eq(1, Ocr.Find(Lines("Bender Image"), "Render Image").Count, "an underlined shortcut letter read as another");
        A.Eq(1, Ocr.Find(Lines("Bender Image"), "Render Image", "exact").Count, "exact too");
        A.Eq(0, Ocr.Find(Lines("Bender"), "Render").Count, "a short word: no");
        A.Eq(0, Ocr.Find(Lines("Render Imade"), "Render Image").Count, "only a word's first letter");
        A.Eq(2, Ocr.Find(Lines("Kicks", "808 Kick"), "Kick").Count, "from a word's start on it still counts");
        A.Eq(1, Ocr.Find(Lines("(none)"), "none").Count, "after punctuation is a word's start");
        var title = new OcrLine();
        float tx = 0;
        foreach (var w in new[] { "Mixer", "-", "Master" }) { title.Words.Add(new OcrWord(w, new RectangleF(tx, 0, w.Length * 8, 14))); tx += w.Length * 8 + 12; }
        A.Eq(0, Ocr.Find(new List<OcrLine> { title }, "Master", "exact").Count, "a dashed title is one line, not menu items");
        A.Eq(1, Ocr.Find(new List<OcrLine> { title }, "Mixer - Master", "exact").Count, "the whole title still matches");
    }

    public static void TestCaptionStandsInOnlyForCaptionedElements()
    {
        AnchorSpec Anc(string json) => AnchorSpec.Parse(JsonNode.Parse(json), null);
        var tab = Engine.TextStandIn(Anc("""{"process":"steamwebhelper","window":"Steam Settings","element":{"name":"Interface","role":"TabItem","match":"exact"}}"""));
        A.Check(tab?.Find is { Text: "Interface", TextMatch: "exact", Lang: "en-US" }, "an exactly named tab: its caption, read in English");
        A.Check(tab!.Path.Count == 0 && tab.Window == "Steam Settings" && tab.Process == "steamwebhelper", "same window, no UIA path");
        A.Check(Engine.TextStandIn(Anc("""{"process":"x","element":{"name":"Настройки","role":"MenuItem","match":"exact"}}"""))?.Find?.Lang == null, "a Cyrillic caption: the user's languages");
        A.Check(Engine.TextStandIn(Anc("""{"process":"x","element":{"name":"Interface","role":"TabItem"}}""")) == null, "a name that only contains: no");
        A.Check(Engine.TextStandIn(Anc("""{"process":"x","element":{"name":"24-hour clock","role":"CheckBox","match":"exact"}}""")) == null, "a check box: its name is a label beside it");
        A.Check(Engine.TextStandIn(Anc("""{"process":"x","element":{"name":"PLAY","role":"Button","match":"exact","index":1}}""")) == null, "the second of several: no");
        A.Check(Engine.TextStandIn(Anc("""{"process":"x","element":{"name":"Seven","role":"Button","match":"exact"},"offset":[10,0]}""")) == null, "a point beside it: no");
        A.Check(Engine.TextStandIn(Anc("""{"process":"x","element":{"name":"OK","role":"Button","match":"exact"}}""")) != null, "a short caption still counts");
        A.Check(Engine.TextStandIn(Anc("""{"process":"x","element":{"name":"7","role":"Button","match":"exact"}}""")) == null, "no letters: no");
    }

    public static void TestCardSpeaksTheStepsLanguage()
    {
        JsonNode Spec(params string[] says) => new JsonObject
        {
            ["steps"] = new JsonArray(says.Select(s => (JsonNode)new JsonObject { ["title"] = s }).ToArray()),
        };
        A.Eq("en", Loc.LangOf(Spec("Click Torus", "Point at the 3D view and press Delete")), "English steps");
        A.Eq("ru", Loc.LangOf(Spec("Наведите на Mesh", "Нажмите Torus — это наш бублик")), "Russian steps naming Latin items");
        A.Eq("en", Loc.LangOf(Spec("Click «Файл», then choose Save as and pick a folder")), "an English step quoting a Russian button");
        A.Eq(null, Loc.LangOf(Spec("OK", "7")), "too few letters to tell");
    }

    public static void TestMenuGlyphsDoNotMoveTheMark()
    {
        // Blender's menu rows: OCR reads the submenu arrow and the icon now as a word of their own, now glued to the
        // caption, now not at all. The mark must stay on the caption every time.
        OcrLine Row(params (string t, float x, float w)[] words)
        {
            var l = new OcrLine();
            foreach (var (t, x, w) in words) l.Words.Add(new OcrWord(t, new RectangleF(x, 0, w, 14)));
            return l;
        }
        var plain = Ocr.Find(new List<OcrLine> { Row(("Mesh", 30, 34)) }, "Mesh", "exact").Single().Rect;
        var arrow = Ocr.Find(new List<OcrLine> { Row(("Mesh", 30, 34), (">", 40 + 34, 8)) }, "Mesh", "exact");
        A.Eq(1, arrow.Count, "an arrow as a word of its own beside the caption");
        A.Eq(plain, arrow[0].Rect, "framed on the caption alone");
        var glued = Ocr.Find(new List<OcrLine> { Row(("Mesh>", 30, 120)) }, "Mesh", "exact");
        A.Eq(1, glued.Count, "an arrow glued to the caption across the row");
        A.Check(Math.Abs(glued[0].Rect.X - plain.X) < 1 && glued[0].Rect.Width < 50, $"glued: the letters' part, not the row ({glued[0].Rect})");
        var icon = Ocr.Find(new List<OcrLine> { Row(("©Torus", 12, 60)) }, "Torus").Single().Rect;
        A.Check(Math.Abs(icon.Right - 72) < 1 && icon.Width < 55, $"a leading icon glued: the right part ({icon})");
        var both = Ocr.Find(new List<OcrLine> { Row(("○", 12, 10), ("Shade", 30, 40), ("Smooth", 76, 50), ("▸", 200, 8)) }, "Shade Smooth", "exact");
        A.Eq(1, both.Count, "an icon and an arrow around a two-word caption");
        A.Check(both[0].Rect.X == 30 && both[0].Rect.Right == 126, $"framed on the two words ({both[0].Rect})");
        A.Eq(0, Ocr.Find(new List<OcrLine> { Row(("Mesh", 30, 34), ("Tools", 70, 40)) }, "Mesh", "exact").Count, "a word beside it is still another line");
    }

    public static void TestYoAndRegex()
    {
        A.Eq(1, Ocr.Find(Lines("Ещё раз"), "еще").Count, "ё = е");
        A.Eq(1, Ocr.Find(Lines("Pattern 12"), @"pattern \d+", "regex").Count, "regex");
    }
}

public static class EventTests
{
    public static void TestSinceAndGap()
    {
        Events.ResetForTests();
        for (int i = 0; i < 5; i++) Events.Emit("t", new JsonObject { ["i"] = i });
        var got = Events.Since(2, out bool gap);
        A.Eq(3, got.Count, "after seq 2");
        A.Check(!gap, "no gap");
        got = Events.Since(0, out gap, max: 2);
        A.Eq(2, got.Count, "limited");
        A.Eq(5L, (long)got[^1]["seq"]!, "newest kept");
        A.Check(gap, "gap when limited");
    }

    public static void TestGapWhenOlderThanTheRing()
    {
        Events.ResetForTests();
        for (int i = 0; i < 1005; i++) Events.Emit("t");
        Events.Since(0, out bool gap, max: 2000);
        A.Check(gap, "the first events fell out of the buffer");
        Events.Since(1004, out gap);
        A.Check(!gap, "recent ones are all there");
    }

    public static void TestEmitMovesFields()
    {
        Events.ResetForTests();
        var data = new JsonObject { ["a"] = 1 };
        Events.Emit("t", data);
        A.Eq(0, data.Count, "fields moved into the event");
        A.Eq(1, J.Int(Events.Since(0)[0], "a"), "event has the field");
    }
}

public static class FileTests
{
    public static void TestWriteAtomicCreatesAndReplaces()
    {
        var dir = Path.Combine(Path.GetTempPath(), "to-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var f = Path.Combine(dir, "sub", "x.json");
            Files.WriteAtomic(f, "one");
            A.Eq("one", File.ReadAllText(f), "created (with its folder)");
            Files.WriteAtomic(f, "two");
            A.Eq("two", File.ReadAllText(f), "replaced");
            A.Eq(1, Directory.GetFiles(Path.Combine(dir, "sub")).Length, "no temporary file left");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    public static void TestPruneKeepsNewest()
    {
        var dir = Path.Combine(Path.GetTempPath(), "to-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            for (int i = 0; i < 5; i++)
            {
                var f = Path.Combine(dir, $"sketch-{i}.png");
                File.WriteAllText(f, "");
                File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddMinutes(-i));
            }
            var old = Path.Combine(dir, "sketch-old.png");
            File.WriteAllText(old, "");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-30));
            File.WriteAllText(Path.Combine(dir, "other.txt"), "");
            A.Eq(3, Files.Prune(dir, "sketch-*.png", 3, TimeSpan.FromDays(14)), "two over the limit and one too old");
            A.Check(File.Exists(Path.Combine(dir, "sketch-0.png")) && !File.Exists(old), "newest kept, old removed");
            A.Check(File.Exists(Path.Combine(dir, "other.txt")), "other files untouched");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}

/// <summary>
/// The local examples/ folder: real tutorials kept on this computer only (gitignored — tutorial specs never go into
/// the repository). Where it is absent these tests have nothing to check and pass.
/// </summary>
public static class ExampleTests
{
    private static string? Dir()
    {
        for (int up = 0; up < 8; up++)
        {
            var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, string.Concat(Enumerable.Repeat("../", up)), "examples"));
            if (Directory.Exists(dir) && File.Exists(Path.Combine(dir, "..", "TutorialOverlay.csproj"))) return dir;
        }
        return null;
    }

    /// <summary>The examples are real, working tutorials: the lint must find nothing in them.</summary>
    public static void TestExamplesHaveNoUnknownFields()
    {
        if (Dir() is not string dir) return;
        var problems = new List<string>();
        foreach (var f in Directory.GetFiles(dir, "*.json"))
        {
            var n = JsonNode.Parse(File.ReadAllText(f));
            var w = J.Has(n, "steps") || J.Has(n, "spec") ? SpecLint.CheckSpec(J.Get(n, "spec") ?? n) : SpecLint.CheckSteps(J.Get(n, "steps") ?? n, 0);
            problems.AddRange(w.Select(x => $"{Path.GetFileName(f)}: {x}"));
        }
        A.Check(problems.Count == 0, string.Join("\n    ", problems.Prepend("")));
    }

    /// <summary>Pixel offsets fit one display scale: every example must say which, or another screen breaks it.</summary>
    public static void TestExamplesSayTheirScale()
    {
        if (Dir() is not string dir) return;
        var missing = Directory.GetFiles(dir, "*.json").Where(f => J.Num(JsonNode.Parse(File.ReadAllText(f)), "scale") is not > 0).Select(Path.GetFileName).ToList();
        A.Check(missing.Count == 0, "no \"scale\" in: " + string.Join(", ", missing));
    }
}

public static class EventRestartTests
{
    public static void TestSinceFromBeforeARestart()
    {
        Events.ResetForTests();
        Events.Emit("ready");
        var got = Events.Since(500, out bool gap);
        A.Eq(1, got.Count, "everything there is");
        A.Check(gap, "flagged");
        var (list, gap2) = Events.WaitSince(500, 5000, System.Threading.CancellationToken.None).GetAwaiter().GetResult();
        A.Eq(1, list.Count, "long-poll returns at once instead of waiting");
        A.Check(gap2, "flagged too");
    }
}

public static class LeaseTests
{
    private sealed class Clock { public DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc); }

    public static void TestOneClientAtATime()
    {
        var c = new Clock();
        var l = new ClientLease(() => c.Now);
        A.Check(l.TryUse("a", out _), "first agent takes the server");
        A.Check(!l.TryUse("b", out var busy) && busy!.Contains("another agent"), "second agent is refused");
        A.Check(l.TryUse("a", out _), "the holder keeps using it");
        A.Eq("a", l.Holder, "holder");
    }

    public static void TestReleaseLetsTheNextOneIn()
    {
        var l = new ClientLease();
        l.TryUse("a", out _);
        A.Check(!l.Release("b", "x"), "only the holder can release");
        A.Check(l.Release("a", "session ended"), "holder releases");
        A.Check(l.TryUse("b", out _), "next agent gets it");
    }

    public static void TestSilentHolderExpires()
    {
        var c = new Clock();
        var l = new ClientLease(() => c.Now);
        l.TryUse("a", out _);
        c.Now += ClientLease.IdleTimeout - TimeSpan.FromSeconds(1);
        A.Check(!l.TryUse("b", out _), "still within the idle time");
        c.Now += TimeSpan.FromSeconds(2);
        A.Check(l.TryUse("b", out _), "silent holder expired");
    }

    public static void TestHolderWithATutorialKeptLonger()
    {
        var c = new Clock();
        var l = new ClientLease(() => c.Now) { HolderHasTutorial = s => s == "a" };
        l.TryUse("a", out _);
        c.Now += ClientLease.IdleTimeout + TimeSpan.FromMinutes(1);
        A.Check(!l.TryUse("b", out _), "the user is still on a's tutorial");
        c.Now += ClientLease.TutorialTimeout;
        A.Check(l.TryUse("b", out _), "but not forever");
    }

    public static void TestOpenStreamKeepsTheHolder()
    {
        var c = new Clock();
        var l = new ClientLease(() => c.Now);
        l.TryUse("a", out _);
        l.StreamOpened("a");
        c.Now += ClientLease.TutorialTimeout + TimeSpan.FromHours(1);
        A.Check(!l.TryUse("b", out _), "a quiet agent whose client is still connected keeps the server");
        A.Check(!l.ReleaseIfDisconnected("a", "x"), "not released while its stream is open");
    }

    public static void TestClosedStreamFreesTheServer()
    {
        var l = new ClientLease();
        l.TryUse("a", out _);
        l.StreamOpened("a");
        l.StreamOpened("a");
        l.StreamClosed("a");
        A.Check(l.HasStream("a"), "one of two streams still open");
        l.StreamClosed("a");
        A.Check(l.ReleaseIfDisconnected("a", "client disconnected"), "last stream closed: released");
        A.Check(l.TryUse("b", out _), "next agent gets it at once");
    }

    public static void TestReconnectWithinGraceKeepsIt()
    {
        var l = new ClientLease();
        l.TryUse("a", out _);
        l.StreamOpened("a");
        l.StreamClosed("a");
        l.StreamOpened("a");          // reconnected before the grace check
        A.Check(!l.ReleaseIfDisconnected("a", "client disconnected"), "kept");
        A.Eq("a", l.Holder, "holder");
    }

    public static void TestChangeEvents()
    {
        var log = new List<string>();
        var l = new ClientLease();
        l.Changed += (now, was, why) => log.Add($"{now ?? "-"}<{was ?? "-"}:{why}");
        l.TryUse("a", out _);
        l.TryUse("b", out _);
        l.Release("a", "session ended");
        A.Eq("a<-:first request|-<a:session ended", string.Join("|", log), "events");
    }
}

public static class PerfPathTests
{
    /// <summary>The PSADBW template comparison gives exactly the byte-by-byte sum, for every length and tail.</summary>
    public static void TestSadMatchesScalar()
    {
        var rnd = new Random(7);
        for (int n = 0; n <= 70; n++)
        {
            var a = new byte[n];
            var b = new byte[n];
            rnd.NextBytes(a);
            rnd.NextBytes(b);
            long expected = 0;
            for (int i = 0; i < n; i++) expected += Math.Abs(a[i] - b[i]);
            A.Eq(expected, Finder.Sad(a, b), $"SAD of {n} bytes");
        }
        var hi = Enumerable.Repeat((byte)255, 64).ToArray();
        A.Eq(64L * 255, Finder.Sad(hi, new byte[64]), "SAD at the extremes");
    }

}

public static class CanvasTests
{
    private static Scene Ring(float pulse, float x = 100)
    {
        var sc = new Scene { Pulse = pulse };
        sc.Prims.Add((new Prim { Kind = PrimKind.Circle, A = new PointF(x, 100), R = 20, Width = 3, Scale = 1, Color = Color.Red }, 1f));
        sc.Prims.Add((new Prim { Kind = PrimKind.Label, Rect = new RectangleF(400, 400, 120, 30), Text = "hi", Scale = 1, Color = Color.Red }, 1f));
        return sc;
    }

    /// <summary>A breathing ring repaints only itself; the still label next to it is left alone.</summary>
    public static void TestPulseRepaintsOnlyAnimated()
    {
        var d = Renderer.Dirty(Renderer.Items(Ring(0.2f)), Renderer.Items(Ring(0.6f)));
        A.Eq(1, d.Count, "dirty parts");
        A.Check(d[0].Contains(new PointF(100, 100)) && !d[0].IntersectsWith(new RectangleF(400, 400, 120, 30)), "only the ring");
        A.Eq(0, Renderer.Dirty(Renderer.Items(Ring(0.6f)), Renderer.Items(Ring(0.6f))).Count, "same frame: nothing");
    }

    /// <summary>A mark that moves repaints where it was and where it is now.</summary>
    public static void TestMovedMarkRepaintsBothPlaces()
    {
        var d = Renderer.Merge(Renderer.Dirty(Renderer.Items(Ring(0.5f, 100)), Renderer.Items(Ring(0.5f, 300))), new Rectangle(0, 0, 2000, 2000));
        A.Check(d.Any(r => r.Contains(100, 100)) && d.Any(r => r.Contains(300, 100)), "old and new place");
        A.Check(!d.Any(r => r.IntersectsWith(new Rectangle(400, 400, 120, 30))), "label untouched");
    }

    /// <summary>A pulsing frame repaints its border strips, not its whole (possibly panel-sized) inside.</summary>
    public static void TestFramePulseRepaintsBorderOnly()
    {
        Scene F(float pulse)
        {
            var sc = new Scene { Pulse = pulse };
            sc.Prims.Add((new Prim { Kind = PrimKind.Rect, Rect = new RectangleF(100, 100, 1900, 200), Width = 3, Radius = 8, Scale = 1, Color = Color.Red }, 1f));
            return sc;
        }
        var d = Renderer.Merge(Renderer.Dirty(Renderer.Items(F(0.1f)), Renderer.Items(F(0.9f))), new Rectangle(0, 0, 3000, 3000));
        long area = d.Sum(r => (long)r.Width * r.Height);
        A.Check(area < 1900L * 200 / 2, $"border only ({area} px)");
        A.Check(!d.Any(r => r.Contains(1000, 200)), "middle not repainted");
    }

    public static void TestMergeJoinsOverlapping()
    {
        var m = Renderer.Merge(new[] { new RectangleF(0, 0, 10, 10), new RectangleF(5, 5, 10, 10), new RectangleF(100, 100, 5, 5) }, new Rectangle(0, 0, 50, 50));
        A.Eq(1, m.Count, "far one clipped away, near ones merged");
    }
}

public static class LayoutTests
{
    private static Prim Shape(string anchorJson, RectangleF? found = null)
    {
        var m = Mark.Parse(A.Json($$"""{"anchor": {{anchorJson}}}"""), null, "t");
        // the Mixer panel; the text was found near its top-right corner
        var ag = new AnchorGeo { Bounds = new RectangleF(439, 730, 1951, 750), P = new PointF(2136, 753), Scale = 1, Found = found };
        return Layout.Build(m, ag, false).Prims[0];
    }

    /// <summary>A text / template anchor frames what was found, not the whole panel it is searched in.</summary>
    public static void TestFindAnchorFramesTheFoundPiece()
    {
        var found = new RectangleF(2106, 743, 60, 20);
        var p = Shape("""{"process": "FL64", "element": {"name": "Mixer"}, "find": {"text": "Master"}}""", found);
        A.Eq(PrimKind.Rect, p.Kind, "default shape of a find anchor");
        A.Check(p.Rect.Contains(found) && p.Rect.Width < 120, $"frames the found text ({p.Rect})");
        // size unknown: still on the found spot, never round the panel
        var q = Shape("""{"process": "FL64", "element": {"name": "Mixer"}, "find": {"text": "Master"}}""");
        A.Check(q.Rect.Contains(new PointF(2136, 753)) && q.Rect.Width < 120, $"at the spot ({q.Rect})");
    }

    /// <summary>An element anchor without a point still frames the element.</summary>
    public static void TestElementAnchorStillWraps()
    {
        var p = Shape("""{"process": "FL64", "element": {"name": "Mixer"}}""");
        A.Eq(PrimKind.Rect, p.Kind, "default shape of an element anchor");
        A.Check(p.Rect.Contains(new RectangleF(439, 730, 1951, 750)), "frames the panel");
    }

    /// <summary>A point inside an element (an offset) gets a ring, not a 40 px square.</summary>
    /// <summary>A spot gets a frame of the visible thing there (measured live); unmeasured, a small square at the spot.</summary>
    public static void TestPointInElementGetsAFrameAtTheSpot()
    {
        var p = Shape("""{"process": "FL64", "element": {"name": "Mixer"}, "offset": [224, 273]}""");
        A.Eq(PrimKind.Rect, p.Kind, "default shape of a point: a frame, never a ring around a tall caption");
    }
}

public static class ToolAnnotationTests
{
    /// <summary>Plugin directories flag tools without a title and a read-only or destructive hint.</summary>
    public static void TestEveryToolIsAnnotated()
    {
        var tools = TutorialOverlay.Tools.List();
        A.Check(tools.Count > 0, "tools listed");
        foreach (var t in tools)
        {
            var name = J.Str(t, "name")!;
            var ann = t!["annotations"];
            A.Check(!string.IsNullOrWhiteSpace(J.Str(t, "title")), name + ": title");
            A.Check(ann?["readOnlyHint"] != null, name + ": readOnlyHint");
            if (ann!["readOnlyHint"]!.GetValue<bool>() == false) A.Check(ann["destructiveHint"] != null, name + ": destructiveHint");
            A.Eq(false, ann["openWorldHint"]!.GetValue<bool>(), name + ": local only");
        }
    }

    public static void TestOnlyLibraryChangesAreDestructive()
    {
        var destructive = TutorialOverlay.Tools.List()
            .Where(t => t!["annotations"]!["destructiveHint"]?.GetValue<bool>() == true)
            .Select(t => J.Str(t, "name")).OrderBy(n => n).ToArray();
        A.Eq("delete_tutorial,save_tutorial", string.Join(",", destructive), "destructive tools");
        foreach (var n in new[] { "status", "describe", "screenshot", "find_text", "events", "list_tutorials" })
            A.Check(TutorialOverlay.Tools.List().First(t => J.Str(t, "name") == n)!["annotations"]!["readOnlyHint"]!.GetValue<bool>(), n + " read-only");
    }
}

public static class InspectTests
{
    private static Inspect.Node N(string role, string name, float x, float y, float w, float h, string id = "", bool off = false) =>
        new(new Resolver.ElInfo(name, role, id, "", new RectangleF(x, y, w, h), off), 1);

    public static void TestChooseKeepsNamedVisibleAndSmall()
    {
        var area = new RectangleF(0, 0, 1000, 800);
        var nodes = new List<Inspect.Node>
        {
            N("Pane", "Editor", 0, 0, 900, 700),        // 0: big container: no number
            N("Button", "Save", 10, 10, 30, 30),        // 1
            N("Button", "", 50, 10, 30, 30),            // 2: nameless
            N("Button", "Hidden", 90, 10, 30, 30, off: true), // 3: offscreen
            N("Button", "Away", 2000, 10, 30, 30),      // 4: outside the area
            N("Pane", "Small panel", 10, 100, 200, 100),// 5: small container
            N("Text", "Save", 11, 11, 28, 28),          // 6: the button's own caption: same rectangle
            N("Pane", "", 300, 300, 50, 50, id: "91163828"), // 7: id is a window handle
            N("Edit", "", 300, 400, 100, 20, id: "SearchBox"), // 8: stable id
        };
        var all = Inspect.Choose(nodes, area, 50);
        A.Eq("1,5,8", string.Join(",", all.Picked), "picked, reading order");
        A.Eq(0, all.Omitted.Count, "nothing omitted");
        // over max: buttons before panels, and what did not fit is reported by role
        var one = Inspect.Choose(nodes, area, 2);
        A.Eq("1,8", string.Join(",", one.Picked), "interactive first");
        A.Eq(1, one.Omitted["Pane"], "omitted pane reported");
    }

    public static void TestChooseDropsWrappersAndBlankElements()
    {
        var area = new RectangleF(0, 0, 1000, 800);
        var nodes = new List<Inspect.Node>
        {
            N("Button", "Toggle Chat", 100, 10, 56, 38),   // 0: a wrapper ...
            N("Button", "Toggle Chat", 110, 10, 37, 37),   // 1: ... around the real button
            N("Button", "Copy code", 500, 300, 39, 39),    // 2: shows on hover only
            N("Button", "Run", 200, 10, 30, 30),           // 3: inside nothing of its name
        };
        var c = Inspect.Choose(nodes, area, 50, r => r.X == 500);
        A.Eq("1,3", string.Join(",", c.Picked), "inner button kept, blank dropped");
        A.Eq(1, c.Blank, "blank counted");
    }

    public static void TestChooseDropsClickableWrappersAndStackedLookAlikes()
    {
        var area = new RectangleF(0, 0, 1000, 800);
        var nodes = new List<Inspect.Node>
        {
            N("Button", "Click to expand", 500, 0, 400, 300),  // 0: around two real buttons
            N("Button", "Copy", 520, 20, 30, 30),               // 1
            N("Button", "Retry", 560, 20, 30, 30),              // 2
            N("TreeItem", "Mascot.cs", 0, 139, 300, 37),        // 3: a row scrolled under ...
            N("TreeItem", "Model.cs", 0, 300, 300, 37),         // 4: a row on its own
            N("TreeItem", "src", 0, 157, 300, 38),              // 5: ... the sticky header, later in the tree: drawn on top
        };
        var c = Inspect.Choose(nodes, area, 50);
        A.Eq("1,2,5,4", string.Join(",", c.Picked), "wrapper and the covered row dropped, the header kept");
        A.Eq(1, c.Overlapping, "covered row reported");
    }

    public static void TestChooseSpreadsNumbersOverTheArea()
    {
        var area = new RectangleF(0, 0, 900, 900);
        var nodes = new List<Inspect.Node>();
        for (int k = 0; k < 10; k++) nodes.Add(N("TreeItem", "file" + k, 10, 10 + 25 * k, 200, 20));   // a long list top-left
        nodes.Add(N("Button", "Send", 800, 850, 40, 30));                                              // bottom-right
        nodes.Add(N("Button", "Stop", 850, 850, 40, 30));
        // the list's cell and the buttons' cell take turns: two of each, not four rows of the list
        var c = Inspect.Choose(nodes, area, 4);
        A.Eq("0,1,10,11", string.Join(",", c.Picked), "cells take turns");
        A.Eq(8, c.Omitted["TreeItem"], "omitted rows reported");
        var tree = Inspect.Choose(nodes.Take(10).Append(N("TreeItem", "far", 800, 800, 60, 20)).ToList(), area, 2);
        A.Check(tree.Picked.Contains(10), "the far cell gets a turn before the list's second row");
        A.Eq(9, tree.Omitted["TreeItem"], "the rest is reported");
    }

    public static void TestIsBlankElementLooksAtTheMiddleOfIcons()
    {
        int w = 60, h = 60;
        var g = new byte[w * h];
        for (int i = 0; i < g.Length; i++) g[i] = 30;
        for (int x = 0; x < 12; x++) g[2 * w + x] = 200;            // a neighbour's caption grazes the corner
        A.Check(!Inspect.IsBlank(g, w, h, new Rectangle(0, 0, 40, 40)), "whole rect is not flat");
        A.Check(Inspect.IsBlankElement(g, w, h, new Rectangle(0, 0, 40, 40)), "but the icon's middle is empty");
        g[20 * w + 20] = 200;                                       // an icon in the middle
        A.Check(!Inspect.IsBlankElement(g, w, h, new Rectangle(0, 0, 40, 40)), "icon");
    }

    public static void TestIsBlank()
    {
        int w = 40, h = 40;
        var g = new byte[w * h];
        for (int i = 0; i < g.Length; i++) g[i] = (byte)(30 + i % 3);   // flat background with a little noise
        A.Check(Inspect.IsBlank(g, w, h, new Rectangle(0, 0, 40, 40)), "flat");
        for (int y = 15; y < 25; y++) g[y * w + 20] = 110;              // a faint vertical stroke: an icon
        A.Check(!Inspect.IsBlank(g, w, h, new Rectangle(0, 0, 40, 40)), "icon");
        A.Check(Inspect.IsBlank(g, w, h, new Rectangle(0, 0, 10, 10)), "the empty corner");
        A.Check(!Inspect.IsBlank(g, w, h, new Rectangle(100, 100, 10, 10)), "outside the picture is not judged");
    }

    public static void TestDisambiguateLikeTheResolver()
    {
        var nodes = new List<Inspect.Node>
        {
            N("Button", "Copy", 0, 0, 10, 10),           // 0
            N("Button", "Copy link", 0, 20, 10, 10),     // 1: contains "Copy"
            N("Button", "Copy", 0, 40, 10, 10),          // 2
            N("Button", "Copy link", 0, 60, 10, 10, id: "link2"), // 3
            N("Button", "Paste", 0, 80, 10, 10),         // 4
            N("Button", "Run", 0, 100, 10, 10, id: "123"),// 5
            N("Button", "Run", 0, 120, 10, 10, id: "456"),// 6: numeric ids are no way out
        };
        A.Eq("{\"name\":\"Paste\",\"role\":\"Button\"}", Inspect.Disambiguate(nodes, 4).ToJsonString(), "unique");
        A.Eq("{\"name\":\"Copy\",\"role\":\"Button\",\"match\":\"exact\",\"index\":1}", Inspect.Disambiguate(nodes, 2).ToJsonString(), "index among exact");
        A.Eq("{\"automationId\":\"link2\",\"role\":\"Button\"}", Inspect.Disambiguate(nodes, 3).ToJsonString(), "stable id");
        A.Eq("{\"name\":\"Run\",\"role\":\"Button\",\"match\":\"exact\",\"index\":1}", Inspect.Disambiguate(nodes, 6).ToJsonString(), "numeric id ignored");
    }

    public static void TestTextHelpers()
    {
        A.Eq("Adam Szabo & Johan", Inspect.Query("Adam Szabo & Johan Vi..berg - Knock Me Out"), "start of a long line");
        A.Eq(null, Inspect.Query("O •"), "no words");
        A.Eq(null, Inspect.Query("130.000"), "a number changes by itself");
        A.Eq("808 CH", Inspect.Query("808 CH"), "two letters are enough");
        A.Check(Inspect.SameWords("Toggle Panel (Ctrl+J)", "Toggle Panel"), "caption of a named element");
        A.Check(!Inspect.SameWords("Save", "Open"), "different");
        A.Check(!Inspect.StableId("91163828") && Inspect.StableId("SearchBox") && !Inspect.StableId(""), "stable ids");
    }
}

public static class OcrMergeTests
{
    private static OcrLine L(string text, float x, float y, float w = 80, float h = 16)
    {
        var l = new OcrLine();
        var words = text.Split(' ');
        float ww = w / words.Length;
        for (int i = 0; i < words.Length; i++) l.Words.Add(new OcrWord(words[i], new RectangleF(x + i * ww, y, ww - 2, h)));
        return l;
    }

    public static void TestFoldMakesLookAlikesEqual()
    {
        A.Eq(Ocr.Fold("Track 6"), Ocr.Fold("ТгасК 6"), "Track / ТгасК");
        A.Eq(Ocr.Fold("808 CH"), Ocr.Fold("808 СН"), "CH / СН");
        A.Check(Ocr.Fold("Файл") != Ocr.Fold("Fain"), "real Cyrillic stays different");
    }

    public static void TestLookAlikeLinesFollowTheUnambiguousOnes()
    {
        // an English UI: "Track 6" read by the Cyrillic recognizer as "ТгасК 6" stays Latin
        var latin = new List<OcrLine> { L("Track 6", 0, 0), L("Browser", 0, 30), L("Mixer", 0, 60) };
        var cyr = new List<OcrLine> { L("ТгасК 6", 0, 0), L("Вгоwsег", 0, 30), L("Міхег", 0, 60) };
        var en = Ocr.Merge(latin, cyr);
        A.Check(en.Any(l => l.Text == "Track 6") && !en.Any(l => l.Text == "ТгасК 6"), "English UI keeps Latin twins");
        // a Russian UI: the same twin goes Cyrillic, because the sure lines are Cyrillic
        var latin2 = new List<OcrLine> { L("HeT", 0, 0), L("Fanл", 0, 30), L("Npaвka", 0, 60) };
        var cyr2 = new List<OcrLine> { L("Нет", 0, 0), L("Файл", 0, 30), L("Правка", 0, 60) };
        var ru = Ocr.Merge(latin2, cyr2);
        A.Check(ru.Any(l => l.Text == "Нет") && ru.Any(l => l.Text == "Файл"), "Russian UI keeps Cyrillic");
    }
}

public static class InspectContainerTests
{
    public static void TestTextIsSearchedInsideTheSmallestNamedPanel()
    {
        var area = new RectangleF(0, 0, 2560, 1500);
        var nodes = new List<Inspect.Node>
        {
            new(new Resolver.ElInfo("FL Studio", "Pane", "", "", new RectangleF(0, 0, 2560, 1500)), 1),   // the whole area
            new(new Resolver.ElInfo("Browser", "Pane", "", "", new RectangleF(0, 120, 400, 1400)), 2),
            new(new Resolver.ElInfo("Channel rack", "Pane", "", "", new RectangleF(415, 120, 1900, 370)), 2),
            new(new Resolver.ElInfo("", "Pane", "91163828", "", new RectangleF(0, 200, 400, 300)), 3),     // window-handle id: not a container
        };
        A.Eq(1, Inspect.Container(nodes, new RectangleF(79, 280, 67, 16), area), "browser row");
        A.Eq(2, Inspect.Container(nodes, new RectangleF(608, 208, 61, 13), area), "channel rack row");
        A.Eq(-1, Inspect.Container(nodes, new RectangleF(2400, 1400, 50, 14), area), "only the whole window: none");
    }
}

public static class MissingTargetTests
{
    public static void TestOnlyUnresolvedAnchorsCountAsMissing()
    {
        foreach (var v in new[] { "element not found", "window not found", "text not visible", "template not visible", "no visible window" })
            A.Check(Engine.IsMissing(v), v);
        // resolved but not shown right now: the window is there, the target too — nothing to report
        foreach (var v in new[] { "visible", "occluded", "inactive", "offscreen", "scroll-down", "later", "resolving", "minimized", "searching" })
            A.Check(!Engine.IsMissing(v), v);
    }
}

public static class ScrollHintTests
{
    public static void TestNoScrollHintForTextInAWholeApp()
    {
        var loose = AnchorSpec.Parse(A.Json("{\"process\":\"FL64\",\"find\":{\"text\":\"General settings\"}}"), null);
        var inList = AnchorSpec.Parse(A.Json("{\"process\":\"FL64\",\"element\":{\"name\":\"Browser\"},\"find\":{\"text\":\"808 Kick\"}}"), null);
        var inPart = AnchorSpec.Parse(A.Json("{\"process\":\"FL64\",\"find\":{\"text\":\"Track 5\",\"within\":[0,100,300,800]}}"), null);
        A.Check(!Engine.ScrollsToFind(loose), "menu item looked for in the whole app: no wheel");
        A.Check(Engine.ScrollsToFind(inList) && Engine.ScrollsToFind(inPart), "inside a list or part: scroll hint as before");
        var strip = AnchorSpec.Parse(A.Json("{\"process\":\"steamwebhelper\",\"find\":{\"text\":\"Steam\",\"within\":[0,0,300,60]}}"), null);
        A.Check(!Engine.ScrollsToFind(strip), "a menu-bar strip does not scroll");
    }
}

public static class OcrShortWordTests
{
    private static OcrLine L(string text)
    {
        var l = new OcrLine();
        float x = 0;
        foreach (var w in text.Split(' ')) { l.Words.Add(new OcrWord(w, new RectangleF(x, 0, 10 * w.Length, 14))); x += 10 * w.Length + 6; }
        return l;
    }

    public static void TestOneLookAlikeSlipInShortWords()
    {
        var lines = new List<OcrLine> { L("MIDl"), L("Audio"), L("Pattern 12") };
        A.Eq(1, Ocr.Find(lines, "MIDI").Count, "MIDl read for MIDI");
        A.Eq(1, Ocr.Find(lines, "MIDI", "exact").Count, "exact too");
        A.Eq(0, Ocr.Find(lines, "MIDX").Count, "a real difference is not forgiven");
        A.Eq(0, Ocr.Find(lines, "Pattern 1", "exact").Count, "Pattern 12 is not Pattern 1");
        A.Eq(0, Ocr.Find(lines, "Aud").Count == 1 ? 0 : 1, "a short query still finds its word");
    }
}

public static class TextVariantTests
{
    private static OcrLine L(string text)
    {
        var l = new OcrLine();
        float x = 0;
        foreach (var w in text.Split(' ')) { l.Words.Add(new OcrWord(w, new RectangleF(x, 0, 10 * w.Length, 14))); x += 10 * w.Length + 6; }
        return l;
    }

    public static void TestOneOrSeveralWordings()
    {
        var one = FindSpec.Parse(A.Json("{\"text\":\"OPTIONS\"}"))!;
        A.Eq("OPTIONS", one.Text, "single text");
        A.Eq(1, one.Texts.Count, "one wording");
        var many = FindSpec.Parse(A.Json("{\"text\":[\"General preferences\",\"General settings\",\" \"],\"match\":\"exact\"}"))!;
        A.Eq("General preferences", many.Text, "first wording is the text");
        A.Eq(2, many.Texts.Count, "blank ones dropped");
        A.Eq("General preferences / General settings", many.TextShown, "shown in messages");
        A.Check(one.Key != many.Key && many.Key != FindSpec.Parse(A.Json("{\"text\":\"General preferences\",\"match\":\"exact\"}"))!.Key, "keys differ");
    }

    public static void TestFirstWordingThatOccursWins()
    {
        var menu = new List<OcrLine> { L("MIDI settings"), L("General settings"), L("File settings") };
        var f = FindSpec.Parse(A.Json("{\"text\":[\"General preferences\",\"General settings\"],\"match\":\"exact\"}"))!;
        var m = Finder.MatchAny(menu, f);
        A.Eq(1, m.Count, "found by the second wording");
        A.Eq("General settings", m[0].Text, "the right line");
        var both = FindSpec.Parse(A.Json("{\"text\":[\"File settings\",\"General settings\"],\"match\":\"exact\"}"))!;
        A.Eq("File settings", Finder.MatchAny(menu, both)[0].Text, "the author's order decides");
        var none = FindSpec.Parse(A.Json("{\"text\":[\"Preferences\",\"Options\"]}"))!;
        A.Eq(0, Finder.MatchAny(menu, none).Count, "none occurs");
    }
}

public static class SharedPrefixTests
{
    private static OcrLine L(string text)
    {
        var l = new OcrLine();
        float x = 0;
        foreach (var w in text.Split(' ')) { l.Words.Add(new OcrWord(w, new RectangleF(x, 0, 10 * w.Length, 14))); x += 10 * w.Length + 6; }
        return l;
    }

    /// <summary>FL's browser: every item starts with "808". A missing one must not turn into a neighbour.</summary>
    public static void TestMissingItemIsNotTakenForANeighbour()
    {
        var list = new List<OcrLine> { L("808 Clav"), L("808 CH"), L("808 Kick"), L("808kit_01"), L("Basic 808"), L("808 Clap"), L("808 HiHat") };
        foreach (var q in new[] { "808 Snare", "808 Ride", "808 Crash", "808 Clop" })
        {
            A.Eq(0, Ocr.Find(list, q).Count, q + " (contains)");
            A.Eq(0, Ocr.Find(list, q, "exact").Count, q + " (exact)");
        }
        A.Eq(1, Ocr.Find(list, "808 Clap").Count, "the real one");
        A.Check(Ocr.Find(list, "808").Count > 1, "only the shared prefix: several lines — ambiguous");
        A.Eq(1, Ocr.Find(list, "808 Kik", "exact").Count, "one dropped letter is an OCR slip, forgiven on purpose");
    }

    /// <summary>Wordings written ahead: a short one must not catch an item that merely contains it.</summary>
    public static void TestAShortWordingDoesNotCatchALongerItem()
    {
        var list = new List<OcrLine> { L("808 Clap"), L("808 Kick"), L("AP - Future Bass Snare Reverse"), L("O Subkicker") };
        var f = FindSpec.Parse(A.Json("{\"text\":[\"808 Snare\",\"Snare\"]}"))!;
        A.Eq(0, Finder.MatchAny(list, f).Count, "Snare is a word inside another item, not that item");
        var g = FindSpec.Parse(A.Json("{\"text\":[\"Sub kicker\",\"Subkicker\"]}"))!;
        A.Eq(1, Finder.MatchAny(list, g).Count, "an icon read as O before the name still counts");
    }

    /// <summary>A menu bar or a tab strip is one OCR line: wordings of their own still find their word in it.</summary>
    public static void TestWordingsOfTheirOwnFindAWordInAMenuBar()
    {
        var bar = new List<OcrLine> { L("FILE EDIT ADD PATTERNS VIEW OPTIONS TOOLS HELP"), L("MIDI Audio General File Theme") };
        A.Eq(1, Finder.MatchAny(bar, FindSpec.Parse(A.Json("{\"text\":[\"preferences\",\"options\"]}"))!).Count, "options in the menu bar");
        A.Eq(1, Finder.MatchAny(bar, FindSpec.Parse(A.Json("{\"text\":[\"Themes\",\"Theme\"]}"))!).Count, "Theme tab (Themes is not a word there)");
        A.Check(Finder.IsCutDown("Snare", new List<string> { "808 Snare", "Snare" }) && !Finder.IsCutDown("options", new List<string> { "preferences", "options" }), "cut-down detection");
    }
}

public static class CaseTests
{
    private static OcrLine L(string text)
    {
        var l = new OcrLine();
        float x = 0;
        foreach (var w in text.Split(' ')) { l.Words.Add(new OcrWord(w, new RectangleF(x, 0, 10 * w.Length, 14))); x += 10 * w.Length + 6; }
        return l;
    }

    public static void TestLetterCaseNeverMatters()
    {
        var lines = new List<OcrLine> { L("OPTIONS"), L("General settings"), L("ЁЛКА"), L("Track 5") };
        foreach (var (q, mode) in new[] { ("options", "contains"), ("OpTiOnS", "exact"), ("GENERAL SETTINGS", "exact"), ("ёлка", "exact"), ("елка", "contains"), ("track 5", "exact") })
            A.Eq(1, Ocr.Find(lines, q, mode).Count, $"{q} ({mode})");
        var f = FindSpec.Parse(A.Json("{\"text\":[\"general Preferences\",\"GENERAL settings\"]}"))!;
        A.Eq(1, Finder.MatchAny(lines, f).Count, "variants too");
        var save = new Resolver.ElInfo("Save As", "Button", "", "", new RectangleF(0, 0, 10, 10));
        A.Check(Resolver.Matches(new ElementSpec { Name = "save as", Role = "button" }, save), "element name and role");
        A.Check(Resolver.Matches(new ElementSpec { Name = "SAVE AS", NameExact = true }, save), "exact element name");
    }
}

public static class IconTests
{
    // a picture: background 30, a box of 70 (a drop-down) with "text" ink 220 and an arrow
    private static byte[] Pic(int w, int h, byte bg) { var g = new byte[w * h]; Array.Fill(g, bg); return g; }
    private static void Fill(byte[] g, int w, Rectangle r, byte v) { for (int y = r.Y; y < r.Bottom; y++) for (int x = r.X; x < r.Right; x++) g[y * w + x] = v; }

    public static void TestControlBoxTakesTheDropDownWithItsArrow()
    {
        int w = 400, h = 120;
        var g = Pic(w, h, 30);
        Fill(g, w, new Rectangle(200, 40, 110, 44), 70);              // the drop-down box
        var text = new Rectangle(230, 53, 32, 18);
        for (int x = text.X; x < text.Right; x += 3) Fill(g, w, new Rectangle(x, text.Y, 1, text.Height), 220);   // letters
        Fill(g, w, new Rectangle(285, 58, 8, 6), 220);                 // the arrow
        var box = Icons.ControlBox(g, w, h, text);
        A.Check(box is Rectangle b && b.X <= 202 && b.Right >= 300 && b.Y <= 42 && b.Bottom >= 82, $"the whole box, arrow included: {box}");
    }

    public static void TestControlBoxOnAFlatBarStopsAtTheNextCaption()
    {
        int w = 400, h = 60;
        var g = Pic(w, h, 30);
        var steam = new Rectangle(40, 20, 50, 14);
        var view = new Rectangle(110, 20, 40, 14);
        foreach (var t in new[] { steam, view }) for (int x = t.X; x < t.Right; x += 3) Fill(g, w, new Rectangle(x, t.Y, 1, t.Height), 220);
        Fill(g, w, new Rectangle(10, 18, 18, 18), 220);                // the app icon left of "Steam"
        var box = Icons.ControlBox(g, w, h, steam);
        A.Check(box is Rectangle b && b.X > 28 && b.X < 40 && b.Right > 90 && b.Right < 110, $"from the icon's gap to the next caption's gap: {box}");
    }

    public static void TestControlBoxNotForTextInAPage()
    {
        int w = 900, h = 300;
        var g = Pic(w, h, 30);
        var text = new Rectangle(400, 140, 60, 16);
        for (int x = text.X; x < text.Right; x += 3) Fill(g, w, new Rectangle(x, text.Y, 1, text.Height), 220);
        A.Check(Icons.ControlBox(g, w, h, text) == null, "nothing around it: the words are the target");
    }

    // a dark picture with marks drawn on it: gray and rgb buffers like inspect's
    private static (byte[] gray, byte[] rgb, int w, int h) Canvas(int w, int h, Action<Action<int, int, byte, byte, byte>> draw)
    {
        var gray = new byte[w * h]; var rgb = new byte[w * h * 3];
        for (int i = 0; i < w * h; i++) { gray[i] = 30; rgb[i * 3] = rgb[i * 3 + 1] = rgb[i * 3 + 2] = 30; }
        draw((x, y, r, g, b) => { int i = y * w + x; gray[i] = (byte)((r * 77 + g * 150 + b * 29) >> 8); rgb[i * 3] = r; rgb[i * 3 + 1] = g; rgb[i * 3 + 2] = b; });
        return (gray, rgb, w, h);
    }

    private static void Square(Action<int, int, byte, byte, byte> px, int x0, int y0, int s, byte r, byte g, byte b)
    {
        for (int y = y0; y < y0 + s; y++) for (int x = x0; x < x0 + s; x++) px(x, y, r, g, b);
    }

    public static void TestRepeatedMarksAreDroppedTheOneOffKept()
    {
        var (gray, rgb, w, h) = Canvas(600, 200, px =>
        {
            for (int k = 0; k < 10; k++) Square(px, 20 + k * 40, 120, 16, 200, 200, 200);         // ten step buttons
            for (int y = 0; y < 20; y++) for (int x = 0; x <= y; x++) px(30 + x, 20 + y, 220, 220, 220);   // a play triangle
        });
        var blobs = Icons.FindBlobs(gray, w, h);
        A.Check(blobs.Count >= 11, $"triangle and squares found ({blobs.Count})");
        var sigs = blobs.Select(b => Icons.Signature(gray, w, h, b, rgb)).ToList();
        var unique = Icons.Unique(blobs, sigs);
        A.Eq(1, unique.Count, "only the one-off mark");
        A.Check(blobs[unique[0]].Y < 60, "it is the triangle");
    }

    public static void TestSameShapeOtherColourOrSizeIsAnotherIcon()
    {
        var (gray, rgb, w, h) = Canvas(400, 120, px =>
        {
            Square(px, 20, 20, 20, 210, 40, 40);     // a red record circle's stand-in
            Square(px, 100, 20, 20, 120, 120, 120);  // a grey one of the same shape
            Square(px, 180, 20, 14, 120, 120, 120);  // a grey smaller one
        });
        var blobs = Icons.FindBlobs(gray, w, h);
        var sigs = blobs.Select(b => Icons.Signature(gray, w, h, b, rgb)).ToList();
        A.Eq(blobs.Count, Icons.Unique(blobs, sigs).Count, "colour and size tell them apart");
    }

    public static void TestFramesLinesAndSpecksAreNotIcons()
    {
        var (gray, _, w, h) = Canvas(500, 300, px =>
        {
            for (int x = 10; x < 300; x++) { px(x, 10, 200, 200, 200); px(x, 200, 200, 200, 200); }   // a panel frame
            for (int y = 10; y <= 200; y++) { px(10, y, 200, 200, 200); px(299, y, 200, 200, 200); }
            for (int x = 350; x < 480; x++) px(x, 50, 200, 200, 200);                                 // a long line
            px(400, 250, 200, 200, 200);                                                                // a speck
        });
        A.Eq(0, Icons.FindBlobs(gray, w, h).Count, "no icons");
    }
}

public static class TemplateRankTests
{
    private static Bitmap Picture()
    {
        var b = new Bitmap(400, 120);
        using var g = Graphics.FromImage(b);
        g.Clear(Color.FromArgb(30, 30, 30));
        using var light = new SolidBrush(Color.FromArgb(220, 220, 220));
        for (int k = 0; k < 3; k++) g.FillRectangle(light, 40 + k * 100, 40, 20, 20);   // three identical strips
        g.FillEllipse(light, 350, 40, 24, 24);                                          // something else
        return b;
    }

    public static void TestRankedMatchesListLookAlikesBestFirst()
    {
        using var pic = Picture();
        using var piece = pic.Clone(new Rectangle(140 - 6, 40 - 6, 32, 32), pic.PixelFormat);   // the middle strip
        var ranked = Finder.MatchRanked(pic, Template.From(piece), 12);
        var same = ranked.Where(c => c.score <= 0.02f).OrderBy(c => c.x).ToList();
        A.Eq(3, same.Count, "all three strips are perfect matches");
        A.Check(Math.Abs(same[1].x - 134) <= 2, "the middle one is among them, in reading order second");
        A.Check(ranked.First(c => c.score > 0.02f).score > 0.05f, "anything else is clearly worse");
    }
}

public static class AutoShapeTests
{
    private static Mark Target(string json) => Mark.Parse(A.Json(json), null, "t");

    public static void TestNoShapesMeansFramedAtItsOwnSize()
    {
        var m = Target("{\"anchor\":{\"process\":\"FL64\",\"find\":{\"template\":\"auto-1\"}}}");
        A.Eq("auto", m.Shapes[0].Type, "default shape");
        A.Eq("auto", Target("{\"anchor\":{\"process\":\"FL64\"},\"shapes\":[{\"type\":\"fit\"}]}").Shapes[0].Type, "fit is auto");
    }

    public static void TestMisfittingShapesAreReported()
    {
        var tall = new System.Text.Json.Nodes.JsonObject { ["visible"] = new System.Text.Json.Nodes.JsonArray(712, 723, 22, 53) };
        A.Check(Engine.ShapeMisfit(Target("{\"anchor\":{\"process\":\"FL64\"},\"shapes\":[{\"type\":\"circle\",\"r\":16}]}"), tall) is string s1 && s1.Contains("ring"), "ring around a tall caption");
        A.Check(Engine.ShapeMisfit(Target("{\"anchor\":{\"process\":\"FL64\"},\"shapes\":[{\"type\":\"rect\",\"w\":90,\"h\":90}]}"), tall) is string s2 && s2.Contains("x the target"), "a frame far bigger than the target");
        A.Eq(null, Engine.ShapeMisfit(Target("{\"anchor\":{\"process\":\"FL64\"}}"), tall), "auto always fits");
        A.Eq(null, Engine.ShapeMisfit(Target("{\"anchor\":{\"process\":\"FL64\"},\"shapes\":[{\"type\":\"rect\",\"w\":24,\"h\":56}]}"), tall), "a fitting frame");
    }

    public static void TestPanelNameKeepsItsStablePart()
    {
        Inspect.Node N(string name) => new(new Resolver.ElInfo(name, "Pane", "", "", new RectangleF(0, 0, 100, 100)), 1);
        var nodes = new List<Inspect.Node> { N("Mixer - Master"), N("Browser"), N("Mixer - Master") };
        var seg = Inspect.ContainerSegment(nodes, 2);
        A.Eq("Mixer -", J.Str(seg, "name"), "the detail after the dash is dropped");
        A.Eq(1, J.Int(seg, "index"), "the same pane among the 'Mixer -' ones");
        A.Eq("Browser", J.Str(Inspect.ContainerSegment(nodes, 1), "name"), "a plain name stays");
    }
}

public static class ChatTests
{
    private static readonly DateTime Now = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);
    private static Step NewStep() => Step.Parse(A.Json("""{"title":"T","targets":[{"anchor":{"process":"x"}}]}"""), null, 0);

    public static void TestSomethingElseStartsAConversation()
    {
        var s = NewStep();
        A.Check(s.Ask("other", "Где кнопка?", "pic.png", 2, true, "away", Now), "own words go into the conversation");
        A.Eq(1, s.Chat.Count, "one message, no note while the agent listens");
        A.Check(s.Chat[0].FromUser && s.Chat[0].Image == "pic.png" && s.Chat[0].Shapes == 2, "the question with its picture");
        A.Check(s.HelpPending, "waiting for the answer");
        s.Answer("Вот она", "Кнопка №1");
        A.Eq("agent", s.Chat[1].Who, "the answer is the next message");
        A.Eq("Кнопка №1", s.Chat[1].Title, "with its title");
        A.Check(s.Hint == null && !s.HelpPending, "not a card hint, nothing pending");
    }

    public static void TestAwayAgentGetsANoteInTheConversation()
    {
        var s = NewStep();
        s.Ask("other", "Не работает", null, 0, false, "away note", Now);
        A.Eq("note", s.Chat[^1].Who, "the card says nobody is listening");
        A.Eq("away note", s.Chat[^1].Text, "its words");
    }

    public static void TestMenuHelpStaysACardHint()
    {
        var s = NewStep();
        A.Check(!s.Ask("cant_find", null, null, 0, true, "away", Now), "not a conversation");
        A.Eq(0, s.Chat.Count, "no messages");
        s.Answer("Слева вверху", null);
        A.Eq("Слева вверху", s.Hint, "the answer is the card's hint");
    }

    public static void TestLateAnswerWaitsInItsStep()
    {
        var s = NewStep();
        s.Ask("cant_find", null, null, 0, true, "away", Now);
        s.ResetRuntime();                         // the user moved on (and maybe back)
        A.Check(s.AnswerLate("Слева вверху", null, Now.AddMinutes(2), "не вижу", "не понимаю"), "accepted");
        A.Eq(2, s.Chat.Count, "the question, then the answer");
        A.Eq("не вижу", s.Chat[0].Text, "the menu choice stands for the question");
        A.Eq("agent", s.Chat[1].Who, "answer");
    }

    public static void TestUnaskedOrOldLateHintIsRefused()
    {
        var s = NewStep();
        A.Check(!s.AnswerLate("x", null, Now, "a", "b"), "nobody asked");
        s.Ask("dont_understand", null, null, 0, true, "away", Now);
        A.Check(!s.AnswerLate("x", null, Now + Step.LateAnswer + TimeSpan.FromMinutes(1), "a", "b"), "asked too long ago");
    }

    public static void TestConversationOutlivesTheVisit()
    {
        var s = NewStep();
        s.Ask("other", "Вопрос", null, 0, true, "away", Now);
        s.ResetRuntime();
        A.Eq(1, s.Chat.Count, "the conversation stays with its step");
        A.Check(s.AnswerLate("Ответ", null, Now.AddHours(2), "a", "b"), "a conversation takes an answer at any time");
    }
}

public static class MascotReachTests
{
    public static void TestAppearFallsSquashesSettles()
    {
        var P = new MascotWindow.Pose();
        MascotWindow.Appear(P, 0, 140);
        A.Check(P.Hop == -140 && P.Sy > 1 && P.Eyes == MascotWindow.Eyes.Wide, "up in the air, stretched, wide-eyed");
        P = new MascotWindow.Pose();
        MascotWindow.Appear(P, 0.425f, 140);
        A.Check(P.Hop == 0 && P.Sy < 0.85f && P.Sx > 1.15f && P.Dust != null, "squashed on the ground, dust");
        P = new MascotWindow.Pose();
        MascotWindow.Appear(P, 1, 140);
        A.Check(P.Hop == 0 && P.Sx == 1 && P.Sy == 1 && P.Point, "settled, pointing");
    }

    public static void TestNoBubbleThatRepeatsThePlate()
    {
        A.Check(MascotWindow.SaysKeys("Нажмите Ctrl+Shift+P", "Ctrl+Shift+P"), "the line is the plate");
        A.Check(MascotWindow.SaysKeys("Press ctrl + s", "Ctrl+S"), "spaces and case do not matter");
        A.Check(!MascotWindow.SaysKeys("Сохраните файл, чтобы изменения не пропали", "Ctrl+S"), "a line that adds something stays");
        A.Check(!MascotWindow.SaysKeys("Нажмите Ctrl+Shift+P", null), "no plate: the bubble stays");
    }

    public static void TestHitsTakeTurns()
    {
        var a = new MascotWindow.Pose(); MascotWindow.Hit(a, 0.3f, 1);
        var b = new MascotWindow.Pose(); MascotWindow.Hit(b, 0.3f, 2);
        var c = new MascotWindow.Pose(); MascotWindow.Hit(c, 0.125f, 3);
        A.Check(a.ArmsUp && a.Hop < 0 && a.Sparkle, "first: a jump with the arms up");
        A.Check(b.Thumb > 0 && b.Wink && !b.Point, "second: a thumbs-up with a wink");
        A.Check(c.FaceY > 4 && c.Eyes == MascotWindow.Eyes.Happy, "third: a nod");
    }

    public static void TestMissShakesThenInsists()
    {
        var a = new MascotWindow.Pose(); MascotWindow.Miss(a, 0.1f);
        A.Check(!a.Point && a.FaceX != 0 && a.Sx == 1, "the face shakes, the body stays");
        var b = new MascotWindow.Pose(); MascotWindow.Miss(b, 0.7f);
        A.Check(b.Point && b.TapK > 2 && b.Brow, "then points again, harder");
    }

    public static void TestFarFromTheCardIsFree()
    {
        A.Check(!MascotWindow.Touches(new PointF(200, 200), new PointF(260, 120), 1, new Rectangle(800, 500, 400, 300)), "far away");
    }

    public static void TestHandsCountNotOnlyTheBody()
    {
        // the body (±33 px at 100 %) would clear a card 40 px to the right; its hands and props (±69 px) do not
        var c = new PointF(500, 500);
        var card = new Rectangle(540, 400, 300, 300);
        A.Check(MascotWindow.Touches(c, null, 1, card), "a hand on the card");
        A.Check(!MascotWindow.Touches(c, null, 1, new Rectangle(600, 400, 300, 300)), "clear with a gap");
    }

    public static void TestPointingArmCounts()
    {
        // a small card just past the figure's box at shoulder height: only the arm pointing straight at it reaches it
        var c = new PointF(300, 500);
        var card = new Rectangle(375, 490, 20, 14);
        A.Check(MascotWindow.Touches(c, new PointF(900, 497), 1, card), "the arm crosses the card");
        A.Check(!MascotWindow.Touches(c, new PointF(0, 497), 1, card), "pointing the other way: clear");
    }

    public static void TestReachGrowsWithScale()
    {
        var card = new Rectangle(580, 400, 300, 300);
        A.Check(!MascotWindow.Touches(new PointF(500, 500), null, 1, card), "clear at 100 %");
        A.Check(MascotWindow.Touches(new PointF(500, 500), null, 1.5f, card), "touches at 150 %");
    }
}

public static class CardDragTests
{
    private static readonly Rectangle Work = new(0, 0, 1920, 1040);   // 40 px taskbar below
    private static readonly Size Card = new(420, 300);

    public static void TestFollowsTheHandInside() =>
        A.Eq(new Point(700, 300), CardWindow.DragTo(new Point(700, 300), Card, Work), "unchanged");

    public static void TestStopsAtTheTaskbar() =>
        A.Eq(new Point(700, 740), CardWindow.DragTo(new Point(700, 900), Card, Work), "bottom edge on the work area");

    public static void TestStopsAtEveryEdge()
    {
        A.Eq(new Point(0, 0), CardWindow.DragTo(new Point(-300, -50), Card, Work), "top left");
        A.Eq(new Point(1500, 0), CardWindow.DragTo(new Point(1800, -1), Card, Work), "top right");
    }

    public static void TestSecondMonitor() =>
        A.Eq(new Point(1920, 100), CardWindow.DragTo(new Point(1900, 100), Card, new Rectangle(1920, 0, 1280, 1024)), "kept on the monitor under the hand");
}

public static class VolatileNameTests
{
    public static void TestNumbersThatChangeAreFlagged()
    {
        A.Check(Engine.VolatileNote("Ln 36, Col 1") != null, "cursor position");
        A.Check(Engine.VolatileNote("10:54 PM") != null, "time");
        A.Check(Engine.VolatileNote("3 unread messages")?.Contains("unread messages") == true, "suggests the steady part");
    }

    public static void TestShortcutsAndPlainNamesAreNot()
    {
        A.Check(Engine.VolatileNote("Split Terminal (Ctrl+Shift+5)") == null, "shortcut digits");
        A.Check(Engine.VolatileNote("Run (F5)") == null, "function key");
        A.Check(Engine.VolatileNote("Explorer (Ctrl+Shift+E)") == null, "no digits");
        A.Check(Engine.VolatileNote(null) == null, "no name");
    }
}
