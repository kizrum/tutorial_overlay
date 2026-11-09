using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace TutorialOverlay;

/// <summary>
/// A step written ahead from what the agent knows ("Options → General settings") names targets in windows that do
/// not exist yet. Most of the time the target shows up when its menu / dialog opens and nobody needs to step in.
/// When a window of the app opens and the target is still not found in it, the agent hears about it at once, with
/// the window's numbered contents (the inspect list) attached, and fixes the target with one retarget call.
/// </summary>
internal static partial class Engine
{
    /// <summary>The target has been missing this long while the new window stood still: not just the menu's fade-in or the first OCR read.</summary>
    private const int MissingAfterMs = 1500;
    /// <summary>A window counts as opened for this step if it appeared at most this long before the step started (the click that completed the previous step opened it).</summary>
    /// <summary>
    /// No window opened, but the one the target is looked for in is there (a tab of an open settings window, the
    /// browser list): missing this long into the step, the target is reported too — longer than for a new window,
    /// since the user may still be scrolling or switching to it.
    /// </summary>
    private const int ExistingAfterMs = 4000;
    /// <summary>A step that scrolls until its target shows: the target may stay off screen this long before it is reported.</summary>
    private const int ScrollFindMs = 20000;

    /// <summary>When each top-level window was first seen during this tutorial (MinValue: it was there before it began).</summary>
    private static readonly Dictionary<IntPtr, DateTime> WindowSince = new();
    private static Tutorial? _windowsOf;
    private static readonly Dictionary<string, DateTime> MissingSince = new();
    private static readonly HashSet<string> MissingReported = new();
    private static Step? _missingStep;

    /// <summary>Runs every tick under <see cref="Sync"/>: notices a target that its newly opened window does not contain.</summary>
    private static void WatchMissing(Tutorial? t, Step? step, DateTime now)
    {
        // every window that appears during the tutorial counts as opened, whatever its size: a dialog can be bigger
        // than the app's main window, or the only window it shows
        var windows = Resolver.RecentWindows(400).Where(w => w.Frame.Width > 1 && w.Frame.Height > 1 && !Native.IsIconic(w.Hwnd)).ToList();
        bool baseline = !ReferenceEquals(t, _windowsOf);
        if (baseline) { _windowsOf = t; WindowSince.Clear(); }
        foreach (var w in windows) if (!WindowSince.ContainsKey(w.Hwnd)) WindowSince[w.Hwnd] = baseline ? DateTime.MinValue : now;
        foreach (var h in WindowSince.Keys.Where(h => !windows.Any(w => w.Hwnd == h)).ToList()) WindowSince.Remove(h);

        if (!ReferenceEquals(step, _missingStep)) { _missingStep = step; MissingSince.Clear(); MissingReported.Clear(); }
        // nobody to tell: a tutorial replayed from the tray, or its agent has gone (released the server, its client
        // closed). "Checking…" would then wait forever: the card goes back to saying what is wrong
        bool listening = t?.Owner != null && McpServer.Lease.Holder == t.Owner;
        if (step is { Checking: true } && !listening) { step.Checking = false; _stepVersion++; }
        if (t == null || step == null || !listening || t.AdvanceAt != null) return;
        // a step that asks to scroll until its target shows expects it off screen: that is the step, not a miss.
        // Only a target still missing long after (the list ended without it) is worth a word
        if (step.Until.Any(c => c.Type == "scroll" && c.UntilVisible && !c.Met)
            && (now - step.EnteredAt).TotalMilliseconds < ScrollFindMs) { MissingSince.Clear(); return; }

        foreach (var m in step.Targets)
        {
            if (m.Optional || m.Satisfied || m.Visibility == "later") { MissingSince.Remove(m.Id); continue; }
            if (!IsMissing(m.Visibility))
            {
                MissingSince.Remove(m.Id);
                // found after all (the app was just slow): nothing to check any more
                if (step.Checking && m.Geo != null && !step.Targets.Any(x => x != m && MissingSince.ContainsKey(x.Id))) { step.Checking = false; _stepVersion++; }
                continue;
            }
            var since = MissingSince.TryGetValue(m.Id, out var s) ? s : MissingSince[m.Id] = now;
            if ((now - since).TotalMilliseconds < MissingAfterMs) continue;

            var process = m.Anchor.Process ?? t.Defaults.Process;
            if (process == null) continue;
            var pids = Resolver.Pids(process);
            // only while the user is in that app: elsewhere its text is not even searched for (and nobody looks)
            if (!pids.Contains(ForegroundPid())) { MissingSince.Remove(m.Id); continue; }
            // the newest window of the app that opened around this step and has been there long enough to be searched
            var win = windows.Where(w => pids.Contains(w.Pid) && WindowSince.TryGetValue(w.Hwnd, out var at)
                                         && at >= step.EnteredAt.AddMilliseconds(-Finder.GraceFor(step.Review))
                                         && (now - at).TotalMilliseconds >= MissingAfterMs)
                             .OrderByDescending(w => WindowSince[w.Hwnd]).FirstOrDefault();
            // a "not found" given before that window opened says nothing about it: an app with a big, slow UIA tree
            // (Steam's library) keeps the resolver busy for seconds, and its menu opened in the middle of a search
            if (win != null && VerdictAt(m.Anchor) is DateTime verdict && verdict < WindowSince[win.Hwnd]) continue;
            RectangleF? scope = null;
            string where = "new window";
            if (win == null)
            {
                // no window opened: the target is missing where it is looked for — its panel (a list, a tab page) or,
                // when even its element is missing, its app's main window. No window at all is a wait, not a miss
                if (!(m.Visibility.Contains("not visible") || m.Visibility == "element not found")) continue;
                // a text named only by its app is written ahead for a menu or dialog not open yet: its absence from
                // the open windows is a wait, not a miss (it is reported when a window opens without it)
                var an = m.Anchor;
                if (an.Find != null && an.Path.Count == 0 && an.Window == null && an.Title == null && an.WindowClass == null && an.Hwnd == null) continue;
                if ((now - since).TotalMilliseconds < ExistingAfterMs || (now - step.EnteredAt).TotalMilliseconds < ExistingAfterMs) continue;
                var r = Resolver.Get(m.Anchor.WithoutFind().Key);
                if (r is { Found: true } && Native.IsWindow(r.Root))
                {
                    win = windows.FirstOrDefault(w => w.Hwnd == r.Root);
                    if (win != null && r.Rel is RectangleF rel) scope = new RectangleF(win.Frame.X + rel.X, win.Frame.Y + rel.Y, rel.Width, rel.Height);
                }
                else
                {
                    // the element itself is missing: the window the anchor names (title / class), else the app's biggest
                    var named = Resolver.MatchingWindows(m.Anchor.WithoutFind(), windows);
                    win = windows.Where(w => named.Contains(w.Hwnd)).OrderByDescending(w => (long)w.Frame.Width * w.Frame.Height).FirstOrDefault();
                }
                if (win == null) continue;
                where = "open window";
            }
            var key = $"{m.Id}|{where}|{win.Hwnd}";
            if (!MissingReported.Add(key)) continue;
            step.Checking = true;     // the card says so, instead of leaving the user before a step without a mark
            _stepVersion++;
            ReportMissing(t.Index, step, m, win, process, scope, where, where == "open window" && ScrollsToFind(m.Anchor));
        }
    }

    /// <summary>When the anchor's latest verdict was given: its text search, else its element search.</summary>
    private static DateTime? VerdictAt(AnchorSpec a) =>
        a.Find != null ? Finder.Get(a.Key)?.At : a.Path.Count > 0 ? Resolver.Get(a.Key)?.At : null;

    /// <summary>The anchor does not resolve (as opposed to: resolved, but covered / inactive / scrolled out of view).</summary>
    internal static bool IsMissing(string visibility) =>
        visibility.Contains("not found") || visibility.Contains("not visible") || visibility is "no visible window";

    /// <summary>
    /// Inspects the window (or the panel <paramref name="scope"/> in it) off the UI thread and emits target_not_found
    /// with its numbered contents.
    /// </summary>
    private static void ReportMissing(int index, Step step, Mark m, TopWindow win, string process, RectangleF? scope = null,
                                      string where = "new window", bool scrollable = false)
    {
        var target = m.Label ?? m.Id;
        var wanted = m.Anchor.Describe();
        var reason = m.Visibility;
        Task.Run(() =>
        {
            var ev = new JsonObject
            {
                ["step"] = index, ["target"] = m.Id, ["label"] = m.Label, ["anchor"] = wanted, ["reason"] = reason, ["where"] = where,
                ["window"] = new JsonObject
                {
                    ["hwnd"] = $"0x{win.Hwnd.ToInt64():X}", ["title"] = win.Title, ["class"] = win.Class, ["rect"] = J.RectJson(win.Frame),
                },
            };
            try
            {
                if (!Native.IsWindow(win.Hwnd)) return;   // closed meanwhile: nothing to look into
                var args = new JsonObject
                {
                    ["anchor"] = new JsonObject { ["process"] = process, ["hwnd"] = $"0x{win.Hwnd.ToInt64():X}" },
                    ["maxItems"] = 80, ["maxEdge"] = 512,
                };
                // only the panel the target is looked for in (the browser, not all of FL): a shorter, truer list
                if (scope is RectangleF sc)
                {
                    var fr = Native.FrameRect(win.Hwnd);
                    args["within"] = new JsonArray(sc.X - fr.X, sc.Y - fr.Y, sc.Width, sc.Height);
                }
                var (info, _) = Inspect.Run(args);
                // the handle is new every time a menu opens: the anchors name the window by its class (and title) —
                // a title only when the class alone is ambiguous: titles change ("Settings - Files & folders" →
                // "Settings - MIDI" with the tab), and an anchor on the old one would stop resolving
                bool classShared = Resolver.RecentWindows(500).Count(w => w.Pid == win.Pid && w.Class == win.Class) > 1;
                void NameWindow(JsonObject a)
                {
                    a.Remove("hwnd");
                    a["windowClass"] = win.Class;
                    if (classShared && win.Title.Length > 0) a["window"] = win.Title;
                }
                if (info["anchor"] is JsonObject a0) NameWindow(a0);
                if (info["items"] is JsonArray its)
                    foreach (var it in its) if (it?["anchor"] is JsonObject ai) NameWindow(ai);
                if (scrollable) ev["note"] = "the target is looked for in a list: it may only be scrolled out of view — check the picture before retargeting";
                ev["candidates"] = new JsonObject
                {
                    ["anchor"] = info["anchor"]?.DeepClone(), ["items"] = info["items"]?.DeepClone(), ["note"] = info["note"]?.DeepClone(),
                };
            }
            catch (Exception e) { ev["candidates"] = new JsonObject { ["error"] = e.Message }; }
            ev["fix"] = $"retarget({{step: {index}, target: '{m.Id}', anchor, label, text}}) with the right item's anchor and its real name in the card text — or hint() if the path itself is wrong";
            Events.Emit("target_not_found", ev);
        });
    }

    /// <summary>retarget: a step's target gets another anchor (the rest of the target — shapes, label, checks — stays).</summary>
    public static JsonObject Retarget(JsonNode args)
    {
        var res = RetargetLocked(args, out var anchor);
        // checked now, outside the lock (UIA / OCR take a moment): the background resolver has not seen the new anchor yet
        string? why = null;
        try
        {
            var r = Resolver.ResolveNow(anchor.WithoutFind());
            if (!r.Found) why = r.Reason;
            else if (anchor.Find != null && !Finder.Search(anchor, null).Found) why = anchor.Find.Text != null ? "text not visible" : "template not visible";
        }
        catch (Exception e) { why = "error: " + e.Message; }
        res["resolvesNow"] = why == null;
        res["reason"] = why;
        return res;
    }

    private static JsonObject RetargetLocked(JsonNode args, out AnchorSpec anchor)
    {
        lock (Sync)
        {
            var t = RequireOwned();
            int si = J.Int(args, "step") ?? t.Index;
            if (si < 0 || si >= t.Steps.Count) throw new ArgumentException($"no such step (have {t.Steps.Count})");
            var s = t.Steps[si];
            var tn = J.Get(args, "target");
            int k = tn is JsonValue v && J.AsNum(v) is double d ? (int)d
                  : J.AsStr(tn) is string id ? s.Targets.FindIndex(x => x.Id == id || x.Label == id) : (s.Targets.Count == 1 ? 0 : -1);
            if (k < 0 || k >= s.Targets.Count)
                throw new ArgumentException("target: its id, label or index (" + string.Join(", ", s.Targets.Select(x => x.Label ?? x.Id)) + ")");
            var anchorJson = J.Unwrap(J.Get(args, "anchor")) ?? throw new ArgumentException("anchor is required");
            var m = s.Targets[k];
            // the card says what the user looks for: a corrected item needs its corrected name there too
            if (J.Str(args, "label") is string label) m.Label = label;
            if (J.Str(args, "title") is string title) s.Title = title;
            if (J.Str(args, "text") is string text) s.Text = text;
            s.Checking = false;
            m.Anchor = AnchorSpec.Parse(anchorJson, t.Defaults);
            m.Geo = null;
            m.Visibility = "resolving";
            m.ScrollHint = null;
            MissingSince.Remove(m.Id);
            MissingReported.RemoveWhere(x => x.StartsWith(m.Id + "|"));

            // what "save" writes follows; a target grown out of a grid has no single entry of its own to change
            if (t.Spec["steps"] is JsonArray specSteps && specSteps.Count == t.Steps.Count
                && specSteps[si] is JsonObject rawStep && J.Get(rawStep, "targets") is JsonArray rawTargets
                && rawTargets.Count == s.Targets.Count && rawTargets[k] is JsonObject rawTarget)
            {
                rawTarget["anchor"] = anchorJson.DeepClone();
                if (J.Str(args, "label") is string l) rawTarget["label"] = l;
                if (J.Str(args, "title") is string ti) rawStep["title"] = ti;
                if (J.Str(args, "text") is string te) rawStep["text"] = te;
            }

            anchor = m.Anchor;
            Events.Emit("target_changed", new() { ["step"] = si, ["target"] = m.Id, ["anchor"] = m.Anchor.Describe() });
            _stepVersion++;
            return new JsonObject { ["step"] = si, ["target"] = m.Id, ["anchor"] = m.Anchor.Describe() };
        }
    }
}
