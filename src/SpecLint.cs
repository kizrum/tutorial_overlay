using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace TutorialOverlay;

/// <summary>
/// The spec parser is forgiving: an unknown field is simply ignored. That hides typos — "untill" or "tagrets" make a
/// step behave strangely with no error. This walks a spec (or draw arguments) and lists every field the parser does
/// not read, with a "did you mean" guess, plus unknown condition / shape types.
/// </summary>
internal static class SpecLint
{
    private static HashSet<string> Set(params string[] keys) => new(keys, StringComparer.Ordinal);
    private static HashSet<string> Union(params HashSet<string>[] sets) => new(sets.SelectMany(x => x), StringComparer.Ordinal);

    private static readonly HashSet<string> Anchor = Set("process", "app", "window", "windowClass", "title", "hwnd", "relativeTo", "client",
        "origin", "element", "path", "find", "offset", "dx", "dy", "screen");
    private static readonly HashSet<string> Element = Set("name", "match", "role", "controlType", "type", "automationId", "id", "className", "class", "pick", "index");
    private static readonly HashSet<string> Find = Set("template", "id", "axis", "maxScore", "text", "match", "lang", "index", "nth", "within", "in");
    private static readonly HashSet<string> Check = Set("anchor", "at", "expr", "when", "radius");
    private static readonly HashSet<string> Shape = Set("type", "shape", "x", "y", "w", "width_px", "wide", "h", "height", "r", "radius_px", "to", "length",
        "text", "label", "n", "color", "thickness", "stroke", "lineWidth", "size", "fontSize", "target", "title", "id", "buttonId", "style", "action",
        "width", "pad", "points", "path", "smooth", "radius", "opacity", "from", "dir");
    private static readonly HashSet<string> Mark = Set("id", "anchor", "dismissOnClick", "toggle", "button", "optional", "name", "label", "text",
        "doneStyle", "hideWhenInactive", "opens", "check", "shapes", "number", "hint", "caption", "arrow", "grid");
    private static readonly HashSet<string> Opens = Union(Anchor, Set("anchor", "anyProcess", "timeoutMs"));
    private static readonly HashSet<string> Grid = Set("from", "step", "dx", "dy", "rows", "cols", "cells", "w", "h");
    private static readonly HashSet<string> Step = Union(Mark, Set("id", "title", "text", "description", "body", "why", "explain", "because", "say",
        "untilMode", "toggle", "dim", "advanceDelayMs", "numbered", "anchor", "targets", "marks", "shapes", "buttons", "controls", "checklist",
        "sequence", "until", "complete", "done", "inMenu"));
    private static readonly HashSet<string> Cond = Union(Anchor, Set("type", "count", "anchor", "at", "expr", "when", "radius", "keys", "key", "from", "to",
        "button", "show", "hold", "holding", "path", "points", "smooth", "ordered", "direction", "tolerance", "coverage", "in", "area", "over", "dir",
        "notches", "until", "untilVisible", "property", "prop", "equals", "is", "contains", "matches", "regex", "not"));
    private static readonly HashSet<string> Region = Union(Anchor, Set("anchor", "rect", "radius"));
    private static readonly HashSet<string> Button = Set("id", "text", "label", "style", "action");
    private static readonly HashSet<string> Spec = Set("title", "app", "process", "window", "anchor", "dim", "toggle", "keepOpen", "advanceDelayMs",
        "hideWhenInactive", "startAt", "outro", "lang", "language", "accent", "hotkeys", "mascot", "steps", "name", "savedAt", "autoColor", "animate",
        "hideFromCapture", "scale", "templates", "description", "version", "replace");
    private static readonly HashSet<string> Draw = Union(Mark, Set("shapes", "anchor", "dismissOnClick", "id", "toggle", "check", "button",
        "hideWhenInactive", "marks", "replace"));
    private static readonly HashSet<string> ShapeTypes = Set("circle", "rect", "arrow", "badge", "label", "info", "check", "checkbox", "button", "dim", "path");
    private static readonly HashSet<string> CondTypes = Set("all_clicked", "appear", "disappear", "pixel", "count", "manual", "key", "drag", "trace", "scroll", "uia");
    private static readonly HashSet<string> Controls = Set("back", "undo", "skip", "done", "stop", "help");

    /// <summary>Problems in a tutorial spec (empty = nothing suspicious).</summary>
    public static List<string> CheckSpec(JsonNode? spec)
    {
        var res = new List<string>();
        spec = J.Unwrap(spec);
        if (spec is not JsonObject o) return res;
        Keys(o, Spec, "", res);
        if (J.Get(o, "hotkeys") is JsonObject hk) Keys(hk, new HashSet<string>(Settings.Actions), "hotkeys.", res);
        if (J.Get(o, "anchor") is JsonNode a) AnchorNode(a, "anchor.", res);
        int i = 0;
        foreach (var s in J.Items(J.Get(o, "steps"))) CheckStep(s, $"steps[{i++}].", res);
        return res;
    }

    /// <summary>Problems in steps given to append_steps.</summary>
    public static List<string> CheckSteps(JsonNode? steps, int firstIndex)
    {
        var res = new List<string>();
        int i = firstIndex;
        foreach (var s in J.Items(steps)) CheckStep(s, $"steps[{i++}].", res);
        return res;
    }

    /// <summary>Problems in draw arguments.</summary>
    public static List<string> CheckDraw(JsonNode? args)
    {
        var res = new List<string>();
        if (args is not JsonObject o) return res;
        Keys(o, Draw, "", res);
        MarkNode(o, "", res, skipKeys: true);
        int i = 0;
        foreach (var m in J.Items(J.Get(o, "marks"))) MarkNode(m, $"marks[{i++}].", res);
        return res;
    }

    private static void CheckStep(JsonNode s, string at, List<string> res)
    {
        s = J.Unwrap(s)!;
        if (s is not JsonObject so) return;
        Keys(so, Step, at, res);
        if (J.Get(so, "anchor") is JsonNode a) AnchorNode(a, at + "anchor.", res);
        int i = 0;
        foreach (var t in J.Items(J.Get(so, "targets") ?? J.Get(so, "marks"))) MarkNode(t, $"{at}targets[{i++}].", res);
        if (J.Has(so, "shapes")) MarkNode(so, at, res, skipKeys: true);
        i = 0;
        foreach (var b in J.Items(J.Get(so, "buttons")))
        {
            if (b is JsonObject bo) Keys(bo, Button, $"{at}buttons[{i}].", res);
            i++;
        }
        if (J.Arr(so, "controls") is JsonArray ctl)
            foreach (var c in ctl)
                if (J.AsStr(c) is string cs && !Controls.Contains(cs.ToLowerInvariant()))
                    res.Add($"{at}controls: unknown control '{cs}'{Guess(cs, Controls)} (back, undo, skip, done, stop, help)");
        i = 0;
        foreach (var c in J.Items(J.Get(so, "until") ?? J.Get(so, "complete") ?? J.Get(so, "done"))) CondNode(c, $"{at}until[{i++}].", res);
    }

    private static void MarkNode(JsonNode m, string at, List<string> res, bool skipKeys = false)
    {
        m = J.Unwrap(m)!;
        if (m is not JsonObject mo) return;
        if (!skipKeys) Keys(mo, Mark, at, res);
        if (J.Get(mo, "anchor") is JsonNode a) AnchorNode(a, at + "anchor.", res);
        if (J.Unwrap(J.Get(mo, "opens")) is JsonObject op)
        {
            Keys(op, Opens, at + "opens.", res);
            AnchorFields(op, at + "opens.", res);
            if (J.Get(op, "anchor") is JsonNode oa) AnchorNode(oa, at + "opens.anchor.", res);
        }
        if (J.Unwrap(J.Get(mo, "check")) is JsonObject ck)
        {
            Keys(ck, Check, at + "check.", res);
            if (J.Get(ck, "anchor") is JsonNode ca) AnchorNode(ca, at + "check.anchor.", res);
        }
        if (J.Get(mo, "grid") is JsonObject g) Keys(g, Grid, at + "grid.", res);
        int i = 0;
        foreach (var sh in J.Items(J.Get(mo, "shapes")))
        {
            if (sh is JsonObject sho)
            {
                Keys(sho, Shape, $"{at}shapes[{i}].", res);
                var type = ShapeSpec.Parse(sho).Type;
                if (!ShapeTypes.Contains(type))
                    res.Add($"{at}shapes[{i}].type: unknown shape '{J.Str(sho, "type") ?? J.Str(sho, "shape")}'{Guess(type, ShapeTypes)} — nothing is drawn for it");
            }
            i++;
        }
    }

    private static void CondNode(JsonNode c, string at, List<string> res)
    {
        c = J.Unwrap(c)!;
        string type;
        if (c is JsonValue) type = Condition.Norm(J.AsStr(c) ?? "all_clicked");
        else if (c is JsonObject co)
        {
            Keys(co, Cond, at, res);
            type = Condition.Norm(J.Str(co, "type") ?? "all_clicked");
            AnchorFields(co, at, res);
            if (J.Get(co, "anchor") is JsonNode a) AnchorNode(a, at + "anchor.", res);
            foreach (var k in new[] { "from", "to", "in", "area", "over" })
                if (J.Unwrap(J.Get(co, k)) is JsonObject ro)
                {
                    Keys(ro, Region, $"{at}{k}.", res);
                    AnchorFields(ro, $"{at}{k}.", res);
                    if (J.Get(ro, "anchor") is JsonNode ra) AnchorNode(ra, $"{at}{k}.anchor.", res);
                }
        }
        else return;
        if (!CondTypes.Contains(type))
            res.Add($"{at}type: unknown condition '{type}'{Guess(type, CondTypes)} — the step would never complete by it");
    }

    private static void AnchorNode(JsonNode a, string at, List<string> res)
    {
        a = J.Unwrap(a)!;
        if (a is not JsonObject ao) return;
        Keys(ao, Anchor, at, res);
        AnchorFields(ao, at, res);
    }

    /// <summary>The nested parts of an anchor (also of objects with inline anchor fields): element path and find.</summary>
    private static void AnchorFields(JsonObject o, string at, List<string> res)
    {
        int i = 0;
        var el = J.Unwrap(J.Get(o, "element") ?? (J.Get(o, "path") is JsonArray p && p.All(x => x is not JsonArray && J.Get(x, "x") == null) ? p : null));
        foreach (var e in J.Items(el))
        {
            if (e is JsonObject eo) Keys(eo, Element, el is JsonArray ? $"{at}element[{i}]." : $"{at}element.", res);
            i++;
        }
        if (J.Unwrap(J.Get(o, "find")) is JsonObject fo) Keys(fo, Find, at + "find.", res);
    }

    private static void Keys(JsonObject o, HashSet<string> known, string at, List<string> res)
    {
        foreach (var (k, _) in o)
        {
            if (known.Contains(k) || k.StartsWith("__") || k.StartsWith("$") || k == "comment" || k == "_comment") continue;
            res.Add($"{at}{k}: unknown field{Guess(k, known)} — ignored");
        }
    }

    /// <summary>" — did you mean 'x'?" for a near miss (case, or up to two edits), else "".</summary>
    private static string Guess(string k, IEnumerable<string> known)
    {
        string? best = null;
        int bestD = int.MaxValue;
        foreach (var c in known)
        {
            int d = string.Equals(c, k, StringComparison.OrdinalIgnoreCase) ? 0 : Distance(k.ToLowerInvariant(), c.ToLowerInvariant());
            if (d < bestD) { bestD = d; best = c; }
        }
        int limit = k.Length <= 4 ? 1 : 2;
        return best != null && bestD <= limit ? $" (did you mean '{best}'?)" : "";
    }

    internal static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                // swapped neighbours ("tagrets") count as one edit
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        return d[a.Length, b.Length];
    }
}

/// <summary>
/// Pixel offsets in a spec are physical pixels, so they only fit the display scale they were measured at. A spec
/// can say which one ("scale": 1.65 — saved tutorials get it automatically); started at another scale, its pixel
/// fields are converted. Anchors by text, template or UIA element move by themselves; offsets from them are scaled.
/// </summary>
internal static class SpecScale
{
    /// <summary>Fields holding physical pixels: numbers or arrays of numbers (or arrays of points).</summary>
    private static readonly HashSet<string> PixelKeys = new(StringComparer.Ordinal)
    {
        "offset", "dx", "dy", "x", "y", "w", "h", "r", "from", "to", "step", "at", "path", "points", "within", "rect",
    };

    /// <summary>Multiplies every pixel field under <paramref name="n"/> by <paramref name="k"/>. Returns how many values changed.</summary>
    public static int Apply(JsonNode? n, double k)
    {
        int changed = 0;
        void Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var key in o.Select(p => p.Key).ToList())
                    {
                        var v = o[key];
                        if (key == "templates" || key == "hotkeys") continue;
                        if (PixelKeys.Contains(key) && Numeric(v)) { o[key] = Scaled(v!, k, ref changed); continue; }
                        Walk(v);
                    }
                    break;
                case JsonArray a:
                    foreach (var x in a) Walk(x);
                    break;
            }
        }
        Walk(n);
        return changed;
    }

    /// <summary>A number, or an array (of arrays) of numbers — not an object such as an anchor.</summary>
    private static bool Numeric(JsonNode? v) => v switch
    {
        JsonValue jv => J.AsNum(jv) != null && J.AsStr(jv) is string s && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _),
        JsonArray a => a.Count > 0 && a.All(Numeric),
        _ => false,
    };

    private static JsonNode Scaled(JsonNode v, double k, ref int changed)
    {
        if (v is JsonArray a)
        {
            var res = new JsonArray();
            foreach (var x in a) res.Add(Scaled(x!, k, ref changed));
            return res;
        }
        changed++;
        return JsonValue.Create(Math.Round(J.AsNum(v)!.Value * k, 1));
    }
}
