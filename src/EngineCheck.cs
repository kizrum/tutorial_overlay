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
    public static JsonObject Validate(JsonNode args)
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
                         "(validate again, or watch anchor_found events). 'occluded'/'inactive' only mean another window is in front now.",
        };
    }

    /// <summary>Resolves an anchor synchronously and says whether its point is visible now, and why not.</summary>
    private static JsonObject CheckAnchor(AnchorSpec a)
    {
        var o = new JsonObject { ["anchor"] = a.Describe() };
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
        try { r = Resolver.ResolveNow(a.WithoutFind()); }
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
            try { fr = Finder.Search(a, null); }
            catch (Exception e) { return Bad((fs.Text != null ? "text search failed: " : "template search failed: ") + e.Message, "error"); }
            if (!fr.Found)
                return Bad(fs.Text != null ? $"text \"{fs.Text}\" not visible in the element (scrolled away? different wording or language?)"
                                           : $"template '{fs.Template}' not visible in the element (scrolled away?)");
            var hit = new PointF(frame.X + fr.Rel.X, frame.Y + fr.Rel.Y);
            float px = fs.Axis == "y" ? origin.X : hit.X, py = fs.Axis == "x" ? origin.Y : hit.Y;
            ag.P = new PointF(px + a.Dx, py + a.Dy);
            o["matchScore"] = Math.Round(fr.Score, 3);
        }
        o["point"] = J.PtJson(ag.P);
        if (!OnAnyMonitor(ag.P)) return Bad("point is off screen (offset too large, or the window is partly off screen)");
        if (a.Path.Count > 0 && a.HasPoint && a.Find == null && !RectangleF.Inflate(bounds, 2, 2).Contains(ag.P))
            o["note"] = "the point lies outside its element — check the offset";
        if (!ShowsAnchorWindow(ag.P, ag)) return Bad("occluded: another window is on top of this point right now");
        if (!OnShellSurface(a, ag.Root) && !AppActive(ag.Pids, a.Process, ForegroundPid())) o["note"] = "the app is not in front now: marks stay hidden until it is (hideWhenInactive)";
        o["ok"] = true;
        return o;
    }
}
