using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.Json.Nodes;

namespace TutorialOverlay;

/// <summary>validate: resolve every anchor of a tutorial right now, before the user meets a broken step.</summary>
internal static partial class Engine
{
    /// <summary>
    /// Checks a spec (or the running tutorial): for each step, whether its targets and condition anchors are
    /// on screen right now, and if not — why. Later steps often point at things earlier steps open, so a
    /// "not found" there is a warning, not necessarily a bug.
    /// </summary>
    /// <summary>
    /// Window and text lookups made during one validate, by anchor key (offsets are not in it): a grid of 68 cells on
    /// one "808 Kick" caption reads that caption once, not 68 times (15 s → well under one).
    /// </summary>
    [ThreadStatic] private static Dictionary<string, AnchorResult>? _resolved;
    [ThreadStatic] private static Dictionary<string, FindResult>? _found;

    public static JsonObject Validate(JsonNode args)
    {
        _resolved = new(); _found = new();
        try { return ValidateCore(args); }
        finally { _resolved = null; _found = null; }
    }

    /// <summary>
    /// A shape given by hand that does not fit the target it marks: a ring around a tall or wide target (a vertical
    /// mixer name), a frame several times the target's size, or much smaller. Null when it fits, or when the target's
    /// size is not known. "auto" (the default) always fits.
    /// </summary>
    internal static string? ShapeMisfit(Mark m, JsonObject check)
    {
        if (J.Arr(check, "visible") is not JsonArray v || v.Count < 4) return null;
        float tw = (float)(J.AsNum(v[2]) ?? 0), th = (float)(J.AsNum(v[3]) ?? 0);
        if (tw < 2 || th < 2) return null;
        string size = $"{tw:0}x{th:0}";
        foreach (var sp in m.Shapes)
        {
            if (!sp.IsTarget) continue;
            float aspect = Math.Max(tw, th) / Math.Min(tw, th);
            if (sp.Type == "circle" && aspect > 1.8f)
                return $"a ring around a target {size} (tall or wide): leave shapes out (or type 'auto') and it is framed at its own size";
            float area = tw * th;
            float? shapeArea = sp.Type == "circle" && sp.R is float r ? 4 * r * r
                             : sp.Type is "rect" or "checkbox" && sp.W is float w && sp.H is float h ? w * h : null;
            if (shapeArea is float sa && (sa > 3 * area || sa < area / 3))
                return $"the shape is {sa / area:0.#}x the target's size ({size}): leave shapes out (or type 'auto') and it is framed at its own size";
        }
        return null;
    }

    // shortcuts in a name ("Split Terminal (Ctrl+Shift+5)", "Run (F5)") are stable: their digits do not count
    private static readonly System.Text.RegularExpressions.Regex Shortcut =
        new(@"\((?:[^()]*\b(?:Ctrl|Alt|Shift|Win|Cmd)\b[^()]*|F\d{1,2})\)|\b(?:Ctrl|Alt|Shift|Win)\+\S+|\bF\d{1,2}\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex Digits = new(@"\d+");

    /// <summary>
    /// A name or text with numbers in it ("Ln 36, Col 1", "3 unread", "10:54 PM", "42%") usually shows a state that
    /// changes — the cursor moves, a counter ticks — and then the anchor stops matching mid-lesson. Returns a note with
    /// the steadiest part to anchor on instead (the longest run without digits), or null when the name has no such
    /// numbers. Shortcut keys in a name do not count.
    /// </summary>
    internal static string? VolatileNote(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var bare = Shortcut.Replace(name, " ");
        if (!Digits.IsMatch(bare)) return null;
        var steady = Digits.Split(bare).Select(p => p.Trim(' ', ',', ':', '.', '-', '/', '%', '(', ')'))
            .OrderByDescending(p => p.Length).FirstOrDefault() ?? "";
        string how = steady.Length >= 3 ? $"anchor on a part that stays, e.g. name: \"{steady}\" (names match by containing it)" : "anchor on something steadier (automationId, a neighbour, or the element's role and position)";
        return $"\"{name}\" has numbers in it: if they change (a position, a counter, a time), the anchor stops matching — {how}";
    }

    private static string? VolatileNote(AnchorSpec a)
    {
        foreach (var p in a.Path) if (p.Name != null && VolatileNote(p.Name) is string n) return n;
        if (a.Find != null) foreach (var t in a.Find.Texts) if (VolatileNote(t) is string n) return n;
        return null;
    }

    /// <summary>
    /// A text the author did not pin (no index / nth) that is on screen more than once: the mark goes to the first
    /// (Finder.Rank), which may not be the one the step means ("Save" in the menu and on a dialog button). Null when
    /// it occurs once, or the author chose the occurrence. <paramref name="places"/>: each one's centre, the marked one first.
    /// </summary>
    internal static string? RepeatNote(FindSpec f, IReadOnlyList<PointF>? places)
    {
        if (f.Text == null || f.NthGiven || places == null || places.Count < 2) return null;
        var at = string.Join(", ", places.Take(6).Select((p, i) => $"{i}: ({p.X:0},{p.Y:0})")) + (places.Count > 6 ? ", …" : "");
        return $"text \"{f.TextShown}\" is on screen {places.Count} times ({at}); the mark goes to 0 (a whole caption before part of a longer one, then the same letter case, then reading order). " +
               "If that is not the one, pin it: find.within (the part of the element it is in), find.index, match:'exact', or an element anchor";
    }

    private static JsonObject ValidateCore(JsonNode args)
    {
        var spec = J.Unwrap(J.Get(args, "spec")) ?? (J.Has(args, "steps") ? args : null);
        var steps = new List<(int index, Step? step, string? error, JsonNode? raw)>();
        List<string> lint = new();
        string? scaleNote = null;
        if (spec != null)
        {
            spec = spec.DeepClone();
            lint = SpecLint.CheckSpec(spec);
            TemplateStore.LoadFrom(spec);
            var defaults = DefaultsOf(spec);
            if (spec is JsonObject so0) scaleNote = Rescale(so0, defaults);
            int i = 0;
            foreach (var s in J.Items(J.Get(spec, "steps")))
            {
                try { steps.Add((i, Step.Parse(s, defaults, i), null, s)); }
                catch (Exception e) { steps.Add((i, null, e.Message, s)); }
                i++;
            }
        }
        else
        {
            Tutorial? t;
            lock (Sync) t = _tut;
            if (t == null) throw new InvalidOperationException("pass spec (same as start_tutorial) or start a tutorial first");
            for (int i = 0; i < t.Steps.Count; i++) steps.Add((i, t.Steps[i], null, null));
        }

        int problems = 0, warnings = 0;
        var outSteps = new JsonArray();
        int? firstIndex = null;
        int curIndex = 0;
        foreach (var (index, step, error, raw) in steps)
        {
            firstIndex ??= index;
            curIndex = index;
            var so = new JsonObject { ["index"] = index };
            if (step == null)
            {
                so["id"] = J.Str(raw, "id");
                so["error"] = "step not understood: " + error;
                problems++;
                outSteps.Add(so);
                continue;
            }
            so["id"] = step.Id;
            so["title"] = step.Title;
            var notes = new JsonArray();
            if (step.Targets.Count == 0 && step.Until.Count == 0) notes.Add((JsonNode)"no targets and no 'until': the step waits for Done / complete_step");
            if (step.Title.Length == 0 && step.Text.Length == 0) { notes.Add((JsonNode)"no title or text: the card will be empty"); warnings++; }

            var targets = new JsonArray();
            foreach (var m in step.Targets)
            {
                var r = CheckAnchor(m.Anchor);
                r["name"] = m.Label ?? m.Id;
                if (m.Check?.Anchor is AnchorSpec ca) r["checkAnchor"] = CheckAnchor(ca);
                if (ShapeMisfit(m, r) is string misfit) { r["shapeNote"] = misfit; warnings++; }
                Count(r);
                targets.Add(r);
            }
            so["targets"] = targets;

            var conds = new JsonArray();
            foreach (var c in step.Until)
            {
                var co = new JsonObject { ["type"] = c.Type };
                var a = c.Anchor ?? c.Pixel?.Anchor;
                if (a != null)
                {
                    var r = CheckAnchor(a);
                    // "appear" waits for something that is not there yet; "disappear" for something that is
                    if (c.Type == "appear")
                    {
                        co["anchor"] = a.Describe();
                        co["now"] = J.Bool(r, "ok") == true ? "already present — the step may complete at once" : "not present yet (expected)";
                    }
                    else { co["check"] = r; Count(r); }
                }
                if (c.From != null) { var r = CheckAnchor(c.From.Anchor); co["from"] = r; Count(r); }
                if (c.To != null) { var r = CheckAnchor(c.To.Anchor); co["to"] = r; Count(r); }
                conds.Add(co);
            }
            if (conds.Count > 0) so["until"] = conds;
            if (notes.Count > 0) so["notes"] = notes;
            outSteps.Add(so);
        }

        void Count(JsonObject r)
        {
            if (J.Has(r, "repeated")) warnings++;
            if (J.Has(r, "volatile")) warnings++;
            if (J.Bool(r, "ok") == true) return;
            // a program a later step works in is usually started by an earlier one (Start menu, a link, "opens")
            if (J.Str(r, "reason") == "process not running" && curIndex != firstIndex)
            {
                r["reason"] = "process not running yet — fine if an earlier step starts it";
                r["severity"] = "warning";
            }
            if (J.Str(r, "severity") == "error") problems++; else warnings++;
        }

        return new JsonObject
        {
            ["ok"] = problems == 0,
            ["errors"] = problems,
            ["warnings"] = warnings,
            ["unknownFields"] = lint.Count > 0 ? Warnings(lint) : null,
            ["scale"] = scaleNote,
            ["steps"] = outSteps,
            ["advice"] = "errors: fix before starting (unknown template, unreadable step, window/element missing on the first step). " +
                         "warnings on later steps are often things an earlier step opens; check them when you get there " +
                         "(validate again, or watch anchor_found events). 'occluded'/'inactive' only mean another window is in front now. " +
                         "'repeated': the text is on screen more than once — make sure the mark is on the right one. " +
                         "'volatile': the name has numbers that may change (cursor position, counters, time) — anchor on the part that stays.",
        };
    }

    /// <summary>Resolves an anchor synchronously and says whether its point is visible now, and why not.</summary>
    private static JsonObject CheckAnchor(AnchorSpec a)
    {
        var o = new JsonObject { ["anchor"] = a.Describe() };
        if (VolatileNote(a) is string vol) o["volatile"] = vol;
        JsonObject Bad(string reason, string severity = "warning")
        {
            o["ok"] = false;
            o["reason"] = reason;
            o["severity"] = severity;
            return o;
        }
        if (a.Find is { Text: null } f && !TemplateStore.Exists(f.Template))
            return Bad($"template '{f.Template}' was never captured — call template(id:'{f.Template}', …) first", "error");
        if (a.IsScreen)
        {
            var sp = new PointF(a.Dx, a.Dy);
            o["point"] = J.PtJson(sp);
            if (!OnAnyMonitor(sp)) return Bad("offscreen", "error");
            o["ok"] = true;
            return o;
        }
        AnchorResult r;
        try
        {
            var wf = a.WithoutFind();
            if (_resolved == null || !_resolved.TryGetValue(wf.Key, out r!)) { r = Resolver.ResolveWithin(wf, 3000); if (_resolved != null) _resolved[wf.Key] = r; }
        }
        catch (Exception e) when (Resolver.IsGone(e) || e is InvalidOperationException) { return Bad("element vanished while reading it: " + e.Message); }
        if (!r.Found) return Bad(r.Reason == "process not running" ? "process not running" : "not found: " + r.Reason, r.Reason == "process not running" ? "error" : "warning");
        if (Native.IsAboveUs(Native.ProcessId(r.Root)))
            return Bad("the app runs as administrator and TutorialOverlay does not: its clicks cannot be seen — start TutorialOverlay as administrator too", "error");
        if (Native.IsIconic(r.Root)) return Bad("window minimized");
        if (!Native.IsWindowVisible(r.Root) || Native.IsCloaked(r.Root)) return Bad("window hidden");

        var frame = Native.FrameRect(r.Root);
        RectangleF bounds = r.Rel is RectangleF rel ? new RectangleF(frame.X + rel.X, frame.Y + rel.Y, rel.Width, rel.Height)
            : a.Client ? Native.ClientScreenRect(r.Root) : frame;
        var origin = a.OriginOf(bounds);
        var ag = new AnchorGeo
        {
            Bounds = bounds, P = new PointF(origin.X + a.Dx, origin.Y + a.Dy), Root = r.Root, Pids = r.Pids,
            Scale = Native.ScaleAt(new PointF(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2)),
        };
        o["bounds"] = J.RectJson(bounds);
        if (a.Find is FindSpec fs)
        {
            FindResult fr;
            try
            {
                if (_found == null || !_found.TryGetValue(a.Key, out fr!)) { fr = Finder.Search(a, null); if (_found != null) _found[a.Key] = fr; }
            }
            catch (Exception e) { return Bad((fs.Text != null ? "text search failed: " : "template search failed: ") + e.Message, "error"); }
            if (!fr.Found && fr.Closed is string closedIn)
                return Bad(closedIn.Length > 0 ? $"text \"{fs.TextShown}\" is in the window \"{closedIn}\", not open now (fine if an earlier step opens it)"
                                               : $"text \"{fs.TextShown}\" is in a {fs.InClass} window (a menu), not open now (fine if an earlier step opens it)");
            if (!fr.Found)
                return Bad(fs.Text != null ? $"text \"{fs.TextShown}\" not visible in the element (scrolled away? different wording or language?)"
                                           : $"template '{fs.Template}' not visible in the element (scrolled away?)");
            if (fr.Root != IntPtr.Zero && Native.IsWindow(fr.Root))
            {
                // found in a menu / dialog of the app: its window is the one that must show the point
                frame = Native.FrameRect(fr.Root);
                ag.Root = fr.Root;
                o["window"] = $"0x{fr.Root.ToInt64():X} \"{Native.WindowTitle(fr.Root)}\"";
            }
            var hit = new PointF(frame.X + fr.Rel.X, frame.Y + fr.Rel.Y);
            float px = fs.Axis == "y" ? origin.X : hit.X, py = fs.Axis == "x" ? origin.Y : hit.Y;
            ag.P = new PointF(px + a.Dx, py + a.Dy);
            o["matchScore"] = Math.Round(fr.Score, 3);
            var vf = fr.Root != IntPtr.Zero && Native.IsWindow(fr.Root) ? Native.FrameRect(fr.Root) : frame;
            if (fr.Visible is RectangleF fv) o["visible"] = J.RectJson(new RectangleF(vf.X + fv.X, vf.Y + fv.Y, fv.Width, fv.Height));
            else if (fr.Box is RectangleF fb) o["visible"] = J.RectJson(new RectangleF(vf.X + fr.Rel.X + fb.X, vf.Y + fr.Rel.Y + fb.Y, fb.Width, fb.Height));
            else if (fr.Size.Width > 0) o["visible"] = J.RectJson(new RectangleF(hit.X - fr.Size.Width / 2, hit.Y - fr.Size.Height / 2, fr.Size.Width, fr.Size.Height));
            if (RepeatNote(fs, fr.Places?.Select(p => new PointF(vf.X + p.X, vf.Y + p.Y)).ToList()) is string rep) o["repeated"] = rep;
        }
        o["point"] = J.PtJson(ag.P);
        if (a.Find == null && !a.HasPoint) o["visible"] = J.RectJson(bounds);
        else if (a.Find == null && a.HasPoint && Finder.MeasureSpot(a) is RectangleF spot)
            o["visible"] = J.RectJson(new RectangleF(frame.X + spot.X, frame.Y + spot.Y, spot.Width, spot.Height));
        if (!OnAnyMonitor(ag.P)) return Bad("point is off screen (offset too large, or the window is partly off screen)");
        if (a.Path.Count > 0 && a.HasPoint && a.Find == null && !RectangleF.Inflate(bounds, 2, 2).Contains(ag.P))
            o["note"] = "the point lies outside its element — check the offset";
        if (!ShowsAnchorWindow(ag.P, ag)) return Bad("occluded: another window is on top of this point right now");
        if (!OnShellSurface(a, ag.Root) && !AppActive(ag.Pids, a.Process, ForegroundPid())) o["note"] = "the app is not in front now: marks stay hidden until it is (hideWhenInactive)";
        o["ok"] = true;
        return o;
    }
}
