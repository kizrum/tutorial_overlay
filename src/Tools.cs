using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TutorialOverlay;

internal static class Tools
{
    public const string Instructions =
        "Screen overlay for step-by-step tutorials in any Windows app. All coordinates are PHYSICAL pixels. " +
        "Typical flow: describe(app) → pick UIA anchors (or a panel + pixel offset for apps that draw their own UI, " +
        "use screenshot(anchor) to measure offsets) → start_tutorial(spec). The user's clicks, misses, completed steps " +
        "and window open/close arrive as events (stdout JSON lines of the overlay process, MCP notifications, or the " +
        "events tool with long-poll) — react to them instead of asking the user whether they are done. " +
        "Write steps that teach: put the reason in each step's 'why' (what the tool does, why this way, the common mistake). " +
        "When a problem is found, explain its cause in hint() so the user learns, not only what to click. " +
        "One server per user and ONE agent at a time: the first agent to call a tool holds it; any other gets 'busy' and must call " +
        "wait_for_turn (never start a second TutorialOverlay). Call release when you are done. " +
        "Use keepOpen + append_steps to feed steps incrementally. Marks auto-hide when the app is minimised, covered or " +
        "not in the foreground and come back on the same step. For apps that draw their own UI, call pick(anchor) and ask " +
        "the user to click the spot: you get the exact offset (and with count=2 on neighbouring cells, a grid step).";

    private static JsonObject Obj(params (string k, JsonNode? v)[] props)
    {
        var o = new JsonObject();
        foreach (var (k, v) in props) o[k] = v;
        return o;
    }

    private static JsonObject T(string type, string desc) => new() { ["type"] = type, ["description"] = desc };

    // The full anchor and shape formats live in draw's parameters only; other tools point there.
    // Tool definitions sit in the agent's context for the whole session — every word here costs tokens.
    private const string AnchorDesc =
        "{process:'FL64', window:'title substr', title:'window OR UIA pane name substr', " +
        "element:{name,role,automationId,className,match:'contains'|'exact',pick:'largest'|'first'|'smallest',index} or array (nested path), " +
        "origin:'topleft'|'center'|'topright'|'bottomleft'|'bottomright'|'top'|'bottom'|'left'|'right', offset:[dx,dy], relativeTo:'frame'|'client', hwnd, screen:true, " +
        "find:{text, match:'contains'|'exact'|'regex', index, lang, within:[x,y,w,h]} (OCR: point = text centre; for self-drawn UIs, check wording with find_text) " +
        "or find:{template, within, axis:'xy'|'x'|'y'} (point = match top-left). Marks follow found text/templates when content scrolls. " +
        "No anchor = absolute screen point (offset).";

    private const string SameAnchor = "anchor, same format as draw.anchor";

    private const string ShapesDesc =
        "coords rel. to anchor point, physical px: circle{x,y,r} (no x/y: ring around element), rect{x,y,w,h,pad,radius} (no w/h: frame element), " +
        "arrow{from:[x,y]|'nw'|'n'…, to, length}, badge{text}, label{text} (auto-placed), info{title,text}, check{}, " +
        "checkbox{x,y,w,h} (✓ when target done), button{text,id,action:'done'|'next'|'back'|'skip'|'stop'|'undo'|'dismiss',style} (real clickable window → 'button' event), " +
        "path{points:[[x,y],…], smooth} (trajectory to follow: dashed, direction arrows, a running light), dim{opacity}. Common: color ('#RRGGBB'|accent|red|orange|yellow|green|teal|blue|purple|white), thickness, size, target:false.";

    public static JsonArray List() => new()
    {
        Tool("draw", "Draw marks over any window outside a tutorial; a mark = shapes on one anchor. Clicking it dismisses it (click still reaches the app). Returns ids.",
            Obj(("shapes", T("array", ShapesDesc)), ("anchor", T("object", AnchorDesc)), ("dismissOnClick", T("boolean", "default true")),
                ("id", T("string", "same id replaces")), ("toggle", T("boolean", "click re-lights a dismissed mark")),
                ("check", T("object", "{at:[x,y], expr:'r>140', radius}: done while the pixel test holds")),
                ("button", T("string", "left|right|middle|any")), ("hideWhenInactive", T("boolean", "default true")),
                ("marks", T("array", "several: [{id, anchor, shapes, …}]")), ("replace", T("boolean", "remove other free marks")))),
        Tool("clear", "Remove free marks (all, or one id).", Obj(("id", T("string", "omit for all")))),
        Tool("start_tutorial", "Start a tutorial; only the current step's targets show, steps advance by themselves. " +
            "spec={title, app (default process for anchors), window, dim, toggle, keepOpen (wait for append_steps after last step), advanceDelayMs, hideWhenInactive, startAt, " +
            "outro (default true: end card offers Save/Repeat/Keep learning), lang:'ru'|'en', accent, hotkeys:{done,back,skip,undo,stop:'Ctrl+Alt+Right'|'none'}, " +
            "mascot (default true: a character flies to each target, points and says the step's 'say' line), " +
            "steps:[{id, title, text, why (shown on the card: teach the idea, not just the click), say (mascot's short line, ≤60 chars; default title), anchor, " +
            "targets:[{id, name, anchor, shapes, toggle, check, button, optional, doneStyle:'hide'|'check', hint, number, arrow, " +
            "opens:{title|window|process|element, timeoutMs} (counts only if this NEW window appears, else event no_effect), " +
            "grid:{from:[x,y] first cell centre, step:[dx,dy], rows, cols, cells:[[r,c]…]|[c…], w, h} (target per cell; name may use {row} {col} {n})}], " +
            "until:['all_clicked' | {type:'appear'|'disappear', title|window|element|anchor} | {type:'pixel', anchor, at, expr} | {type:'count', count} | " +
            "{type:'key', keys:'Ctrl+S'|[…]} | {type:'drag', from:{anchor|element|title, rect, radius}, to, button, hold:['Shift']} | " +
            "{type:'trace', anchor, path:[[x,y],…] rel. to anchor point, smooth, tolerance (px, 24), coverage (0.85), ordered, button, hold:['Alt']} (drag along the path, keys held throughout; events trace_done / trace_off{reason}) | " +
            "{type:'scroll', in:{anchor|element, rect}, direction:'down'|'up'|'left'|'right', notches (3) | until:'visible'} | " +
            "{type:'uia', anchor|element|title, property:'exists'|'name'|'value'|'toggle'|'selected'|'expanded'|'enabled'|'focused'|'text', equals|contains|matches, not}], " +
            "untilMode:'any'|'all', numbered, checklist, sequence (targets one by one, e.g. right-click → popup item; chain restarts if the popup closes), " +
            "buttons:[{id,text,action,style}], controls:['back','undo','skip','done','stop']}]} " +
            "Or {name, startAt} to replay a saved one. No 'until': done when all targets clicked; no targets: waits for Done. " +
            "'disappear' needs the thing seen first. Clicks match by the PROCESS under the cursor. Anchors: see draw.anchor.",
            Obj(("spec", T("object", "tutorial spec (fields may be top level)")))),
        Tool("append_steps", "Add steps to the running tutorial (resumes one waiting after its last step).",
            Obj(("steps", T("array", "as in start_tutorial")), ("insertAt", T("integer", "index after the current step")))),
        Tool("goto_step", "Jump to a step (conditions reset).",
            Obj(("index", T("integer", "0-based")), ("id", T("string", "step id")), ("relative", T("string", "'next'|'back'")))),
        Tool("complete_step", "Complete the current step as if Done was pressed.",
            Obj(("expectIndex", T("integer", "only if still this step")), ("immediate", T("boolean", "skip the ✓ pause")))),
        Tool("stop_tutorial", "Stop the tutorial and hide its card.", new JsonObject()),
        Tool("status", "Tutorial state, step targets with screen rects and visibility (visible/occluded/minimized/inactive/dismissed…), free marks, foreground window.", new JsonObject()),
        Tool("describe", "Windows and UI Automation tree of an app (physical rects; rel = offset from window frame). No app: all top-level windows.",
            Obj(("app", T("string", "process name")), ("window", T("string", "title substr")), ("element", T("object", "subtree root, as anchor.element")),
                ("depth", T("integer", "default 8")), ("maxNodes", T("integer", "default 600")), ("namedOnly", T("boolean", "skip unnamed nodes")))),
        Tool("screenshot", "Image of an anchor's rect or a screen area, without the overlay's drawings, plus image→screen mapping. " +
            "Prefer an element anchor over the whole window: image tokens grow with size.",
            Obj(("anchor", T("object", SameAnchor)), ("rect", T("array", "[x,y,w,h] screen rect")), ("pad", T("integer", "px around the anchor rect")),
                ("maxEdge", T("integer", $"long edge ≤ this (default {DefaultMaxEdge}; 0 = full size, for tiny details)")),
                ("includeOverlay", T("boolean", "with drawings and card, to verify placement")),
                ("source", T("string", "'window' (default with anchor: own pixels even if covered) | 'screen'")))),
        Tool("template", "Remember a small UI piece (~20-60 px, distinctive) as a visual anchor for find:{template}. Captured from the window's own pixels. Returns the image.",
            Obj(("id", T("string", "template name")), ("anchor", T("object", SameAnchor)), ("rect", T("array", "[x,y,w,h] rel. to the element's top-left")))),
        Tool("hint", "Your answer in the step card (after a 'help' event, or proactively); cleared on step change.",
            Obj(("text", T("string", "hint")), ("title", T("string", "heading")), ("highlight", T("boolean", "dim screen and ping targets")),
                ("seconds", T("integer", "highlight time, default 6")), ("expectIndex", T("integer", "only if still this step")))),
        Tool("pick", "User clicks 1..count points; returns coords, rel to the anchor point, colour, UIA element, suggestedAnchor (count≥2 on neighbour cells: gridHint). Click is swallowed unless passThrough; Esc cancels.",
            Obj(("anchor", T("object", SameAnchor)), ("count", T("integer", "default 1")), ("prompt", T("string", "text by the cursor")),
                ("passThrough", T("boolean", "let the click reach the app")), ("timeoutMs", T("integer", "default 60000")))),
        Tool("find_text", "OCR (built into Windows) for self-drawn UIs. With text: matches (rect, centre, rel) and one suggestedAnchor with find:{text}. Without text: all recognised lines.",
            Obj(("text", T("string", "case-insensitive, small OCR slips tolerated")), ("anchor", T("object", SameAnchor)), ("rect", T("array", "[x,y,w,h] screen rect")),
                ("within", T("array", "[x,y,w,h] part of the element")), ("match", T("string", "contains|exact|regex")), ("lang", T("string", "e.g. 'en-US', 'ru'")),
                ("max", T("integer", $"most matches returned (default {DefaultMaxMatches})")))),
        Tool("validate", "Resolve every anchor of a spec (or the running tutorial) now; per step: ok or why not. Later steps often wait for windows earlier steps open.",
            Obj(("spec", T("object", "as start_tutorial; omit for the running one")))),
        Tool("ask", "Ask the user (panel above the card) and WAIT: options → {choice,label}; input → {text}; else {cancelled}|{timedOut}.",
            Obj(("question", T("string", "")), ("title", T("string", "heading")), ("options", T("array", "['Yes','No'] or [{id,text,style}]")),
                ("input", T("boolean", "text field (default when no options)")), ("placeholder", T("string", "")), ("timeoutMs", T("integer", "default 300000")))),
        Tool("ask_area", "User circles an area with a pencil; WAIT. Returns bounds, strokes, UIA element at the centre with suggestedAnchor, and the image.",
            Obj(("prompt", T("string", "what to circle")), ("timeoutMs", T("integer", "default 180000")))),
        Tool("save_tutorial", "Save the current/last tutorial (with appended steps) or a spec to the user's library; replay from the tray or start_tutorial({name}). The end card also offers Save.",
            Obj(("name", T("string", "default: from title")), ("spec", T("object", "instead of the current one")))),
        Tool("list_tutorials", "Saved tutorials.", new JsonObject()),
        Tool("delete_tutorial", "Remove a saved tutorial.", Obj(("name", T("string", "from list_tutorials")))),
        Tool("wait_for_turn", "The overlay serves ONE agent at a time. If a call answered 'busy', wait here until the other agent is done; you then hold it. Never start another TutorialOverlay instead.",
            Obj(("timeoutMs", T("integer", "default 600000, max 1800000")))),
        Tool("release", "Done with the overlay: let another agent use it (a running tutorial keeps running for the user).", new JsonObject()),
        Tool("events", "Events after seq: hit, miss, step_*, tutorial_*, window_opened/closed, anchor_found/lost, opened, no_effect, blocked, prepare_started/done (step 0: a dialog to answer before step 1), button, pick, " +
            "help (reason cant_find|dont_understand|other; other: user text + optional pencil sketch with image path → answer with hint), " +
            "continue_requested (append the next lesson), outro, …. timeoutMs long-polls.",
            Obj(("since", T("integer", "last seen seq")), ("timeoutMs", T("integer", "max 60000")))),
    };

    /// <summary>Screenshots: the long edge an image is scaled to unless the agent asks for more.</summary>
    private const int DefaultMaxEdge = 1024;
    private const int DefaultMaxMatches = 5;

    private static JsonObject Tool(string name, string desc, JsonObject props) => new()
    {
        ["name"] = name,
        ["description"] = desc,
        ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = props, ["additionalProperties"] = true },
    };

    public static async Task<JsonObject> Call(string name, JsonNode args, CancellationToken ct, string? session = null)
    {
        // whose call this is: a tutorial belongs to the agent (session) that started it
        Engine.Caller.Value = session;
        try
        {
            return name switch
            {
                "draw" => DrawTool(args),
                "clear" => Text(new JsonObject { ["removed"] = Engine.Clear(J.Str(args, "id")) }),
                "start_tutorial" => Text(Engine.StartTutorial(args)),
                "append_steps" => Text(Engine.AppendSteps(args)),
                "goto_step" => Text(Engine.GotoStep(args)),
                "complete_step" => Text(Engine.CompleteStep(args)),
                "stop_tutorial" => Text(Engine.StopTutorial()),
                "status" => Text(Engine.Status()),
                "describe" => TextRaw(await Task.Run(() => Describe.Run(args, ct), ct)),
                "screenshot" => await Task.Run(() => Screenshot(args), ct),
                "events" => await EventsTool(args, ct),
                "pick" => Text(await Engine.Pick(args, ct)),
                "hint" => Text(Engine.SetHint(args)),
                "template" => await Task.Run(() => TemplateTool(args), ct),
                "find_text" => await Task.Run(() => FindText(args), ct),
                "save_tutorial" => Text(Engine.SaveTutorial(args)),
                "list_tutorials" => Text(new JsonObject { ["tutorials"] = new JsonArray(Tutorials.List().Select(o => (JsonNode)o).ToArray()), ["dir"] = Tutorials.Dir }),
                "delete_tutorial" => Text(new JsonObject { ["deleted"] = Tutorials.Delete(J.Str(args, "name") ?? throw new ArgumentException("name is required")) }),
                "validate" => Text(await Task.Run(() => Engine.Validate(args), ct)),
                "ask" => Text(await Engine.Ask(args, ct)),
                "ask_area" => AreaResult(await Engine.AskArea(args, ct)),
                _ => Fail("unknown tool: " + name),
            };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Fail(e.Message);
        }
    }

    /// <summary>Tool results go into the agent's context: compact JSON, no null fields (~40% fewer tokens than indented).</summary>
    private static string Json(JsonNode o) => Prune(o.DeepClone())!.ToJsonString(J.Compact);

    private static JsonNode? Prune(JsonNode? n)
    {
        if (n is JsonObject o)
            foreach (var k in o.Select(p => p.Key).ToList())
            {
                if (o[k] is null) o.Remove(k);
                else Prune(o[k]);
            }
        else if (n is JsonArray a)
            foreach (var x in a) Prune(x);
        return n;
    }

    private static JsonObject Text(JsonNode o) => TextRaw(Json(o));

    private static JsonObject TextRaw(string s) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = s }),
    };

    private static JsonObject Fail(string msg) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "error: " + msg }),
        ["isError"] = true,
    };

    private static JsonObject AreaResult((JsonObject info, byte[]? png) r)
    {
        var content = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = Json(r.info) });
        if (r.png != null) content.Add(new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(r.png), ["mimeType"] = "image/png" });
        return new JsonObject { ["content"] = content };
    }

    private static JsonObject FindText(JsonNode args)
    {
        // empty or blank text = read everything
        var query = string.IsNullOrWhiteSpace(J.Str(args, "text")) ? null : J.Str(args, "text");
        string match = (J.Str(args, "match") ?? "contains").ToLowerInvariant();
        string? lang = J.Str(args, "lang");
        Rectangle rect;
        IntPtr window = IntPtr.Zero;
        PointF? anchorPoint = null;
        RectangleF? element = null;
        JsonNode? anchorJson = J.Get(args, "anchor");
        // like screenshot: during a tutorial the default target is its app
        if (anchorJson == null && !J.Has(args, "rect") && Engine.Tutorial is { Active: true, Defaults.Process: string proc })
            anchorJson = new JsonObject { ["process"] = proc };
        if (J.Get(args, "rect") is JsonArray ra && ra.Count >= 4)
            rect = new Rectangle((int)(J.AsNum(ra[0]) ?? 0), (int)(J.AsNum(ra[1]) ?? 0), (int)(J.AsNum(ra[2]) ?? 100), (int)(J.AsNum(ra[3]) ?? 100));
        else if (anchorJson != null)
        {
            var a = AnchorSpec.Parse(anchorJson, Engine.Tutorial?.Defaults).WithoutFind();
            if (a.IsScreen) rect = Native.MonitorRectAt(new PointF(a.Dx, a.Dy));
            else
            {
                var r = Resolver.ResolveNow(a);
                if (!r.Found) return Fail($"anchor not found: {r.Reason} ({a.Describe()})");
                if (Native.IsIconic(r.Root)) return Fail("the anchor's window is minimized");
                var frame = Native.FrameRect(r.Root);
                RectangleF b = r.Rel is RectangleF rel ? new RectangleF(frame.X + rel.X, frame.Y + rel.Y, rel.Width, rel.Height)
                    : a.Client ? Native.ClientScreenRect(r.Root) : frame;
                element = b;
                window = r.Root;
                var o = a.OriginOf(b);
                anchorPoint = new PointF(o.X + a.Dx, o.Y + a.Dy);
                rect = Rectangle.Round(b);
                if (J.Arr(args, "within") is JsonArray wa && wa.Count >= 4)
                    rect = Rectangle.Intersect(rect, Rectangle.Round(new RectangleF(b.X + (float)(J.AsNum(wa[0]) ?? 0), b.Y + (float)(J.AsNum(wa[1]) ?? 0),
                                                                                    (float)(J.AsNum(wa[2]) ?? 0), (float)(J.AsNum(wa[3]) ?? 0))));
            }
        }
        else
        {
            Native.GetCursorPos(out var cp);
            rect = Native.MonitorRectAt(new PointF(cp.X, cp.Y));
        }
        rect = Rectangle.Intersect(rect, Native.VirtualScreen());
        if (rect.Width < 4 || rect.Height < 4) return Fail("area is empty or off screen");

        // the window's own pixels when possible (no overlay drawings, works while covered); else the screen without our windows
        Bitmap? shot = window != IntPtr.Zero ? ScreenCapture.CaptureWindow(window, rect, maxAgeMs: 0) : null;
        if (shot == null)
        {
            bool flip = !Settings.Global.HideFromCapture;
            if (flip) { Program.SetCapturable(false); Thread.Sleep(80); }
            try { shot = ScreenCapture.Capture(rect); }
            finally { if (flip) Program.SetCapturable(true); }
        }
        List<OcrLine> lines;
        using (shot) lines = Ocr.Read(shot, lang, query);

        RectangleF Screen(RectangleF r) => new(rect.X + r.X, rect.Y + r.Y, r.Width, r.Height);
        var info = new JsonObject { ["area"] = J.RectJson(rect) };
        if (query == null)
        {
            info["lines"] = new JsonArray(lines.Select(l => (JsonNode)new JsonObject { ["text"] = l.Text, ["rect"] = J.RectJson(Screen(l.Bounds)) }).ToArray());
            if (lines.Count == 0) info["note"] = "no text recognised (tiny or low-contrast text? try a smaller area, or lang)";
            return Text(info);
        }
        var found = Ocr.Find(lines, query, match).OrderBy(m => Math.Round(m.Rect.Y / 8)).ThenBy(m => m.Rect.X).ToList();
        int max = Math.Max(1, J.Int(args, "max") ?? DefaultMaxMatches);
        var arr = new JsonArray();
        for (int i = 0; i < found.Count && i < max; i++)
        {
            var m = found[i];
            var sr = Screen(m.Rect);
            var c = new PointF(sr.X + sr.Width / 2, sr.Y + sr.Height / 2);
            // score only when it is not an exact read
            var o = new JsonObject { ["index"] = i, ["text"] = m.Text, ["rect"] = J.RectJson(sr), ["center"] = J.PtJson(c), ["score"] = m.Score > 0 ? Math.Round(m.Score, 2) : null };
            if (anchorPoint is PointF ap) o["rel"] = J.PtJson(new PointF(c.X - ap.X, c.Y - ap.Y));
            arr.Add(o);
        }
        info["matches"] = arr;
        if (found.Count > max) info["more"] = found.Count - max;
        // one ready anchor for match 0; any other match is the same with find.index = its index
        if (found.Count > 0 && anchorJson is JsonObject aj)
        {
            var sug = (JsonObject)aj.DeepClone();
            var find = new JsonObject { ["text"] = query };
            if (match != "contains") find["match"] = match;
            if (lang != null) find["lang"] = lang;
            if (J.Get(args, "within") is JsonNode wn) find["within"] = wn.DeepClone();
            sug["find"] = find;
            sug.Remove("offset");
            info["suggestedAnchor"] = sug;
        }
        if (element is RectangleF el) info["elementBounds"] = J.RectJson(el);
        if (found.Count == 0)
        {
            info["near"] = new JsonArray(lines.Take(25).Select(l => (JsonNode)new JsonObject { ["text"] = l.Text, ["rect"] = J.RectJson(Screen(l.Bounds)) }).ToArray());
            info["note"] = "not found; 'near' is what the OCR read here";
        }
        else info["note"] = "suggestedAnchor = match 0 (add find.index for others); point = text centre, offset moves it";
        return Text(info);
    }

    private static JsonObject TemplateTool(JsonNode args)
    {
        var id = J.Str(args, "id") ?? throw new ArgumentException("id is required");
        var a = AnchorSpec.Parse(J.Get(args, "anchor"), Engine.Tutorial?.Defaults);
        if (J.Arr(args, "rect") is not JsonArray ra || ra.Count < 4) throw new ArgumentException("rect [x,y,w,h] is required");
        var r = Resolver.ResolveNow(a.WithoutFind());
        if (!r.Found) return Fail($"anchor not found: {r.Reason}");
        var frame = Native.FrameRect(r.Root);
        RectangleF b = r.Rel is RectangleF rel ? new RectangleF(frame.X + rel.X, frame.Y + rel.Y, rel.Width, rel.Height) : frame;
        var rect = Rectangle.Round(new RectangleF(b.X + (float)(J.AsNum(ra[0]) ?? 0), b.Y + (float)(J.AsNum(ra[1]) ?? 0),
                                                  (float)(J.AsNum(ra[2]) ?? 0), (float)(J.AsNum(ra[3]) ?? 0)));
        if (rect.Width < 6 || rect.Height < 6) return Fail("template too small (at least 6x6 px)");
        using var shot = ScreenCapture.CaptureWindow(r.Root, rect, maxAgeMs: 0);
        if (shot == null) return Fail("could not capture the window (minimized?)");
        var png = ScreenCapture.Png(shot, 0, out _);
        // kept on disk too, and carried inside saved tutorials that use it
        TemplateStore.Put(id, Template.From(shot), png);
        return new JsonObject
        {
            ["content"] = new JsonArray(
                new JsonObject { ["type"] = "text", ["text"] = $"template '{id}' saved ({rect.Width}x{rect.Height}) at {J.RectJson(rect).ToJsonString()}; use anchor.find:{{template:'{id}'}}" },
                new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(png), ["mimeType"] = "image/png" }),
        };
    }

    private static JsonObject DrawTool(JsonNode args)
    {
        var res = new JsonObject { ["ids"] = new JsonArray(Engine.Draw(args).Select(i => (JsonNode)i).ToArray()) };
        var lint = SpecLint.CheckDraw(args);
        if (lint.Count > 0) res["warnings"] = new JsonArray(lint.Take(30).Select(x => (JsonNode)x).ToArray());
        return Text(res);
    }

    private static async Task<JsonObject> EventsTool(JsonNode args, CancellationToken ct)
    {
        long since = (long)(J.Num(args, "since") ?? 0);
        int timeout = Math.Clamp(J.Int(args, "timeoutMs") ?? 0, 0, 60000);
        List<JsonObject> list;
        bool gap;
        if (timeout > 0) (list, gap) = await Events.WaitSince(since, timeout, ct);
        else list = Events.Since(since, out gap);
        var res = new JsonObject
        {
            ["events"] = new JsonArray(list.Select(e => e.DeepClone()).ToArray()),
            ["lastSeq"] = list.Count > 0 ? (long)list[^1]["seq"]! : Math.Max(since, 0),
        };
        // older events than these were not returned (too many at once, or older than the buffer of 1000)
        if (gap) res["gap"] = "some events after 'since' were dropped, or 'since' is from before a server restart; call status() to resync the current state";
        return Text(res);
    }

    private static JsonObject Screenshot(JsonNode args)
    {
        Rectangle rect;
        string what;
        PointF? anchorPoint = null;
        RectangleF? anchorBounds = null;
        IntPtr window = IntPtr.Zero;
        // no target given during a tutorial: shoot the tutorial's app, not whatever monitor the cursor is on
        if (!J.Has(args, "rect") && !J.Has(args, "anchor") && Engine.Tutorial is { Active: true, Defaults.Process: not null } tut)
            args = new JsonObject { ["anchor"] = new JsonObject { ["process"] = tut.Defaults.Process }, ["includeOverlay"] = J.Bool(args, "includeOverlay"),
                                    ["maxEdge"] = J.Int(args, "maxEdge"), ["source"] = J.Str(args, "source") };
        if (J.Get(args, "rect") is JsonArray ra && ra.Count >= 4)
        {
            rect = new Rectangle((int)(J.AsNum(ra[0]) ?? 0), (int)(J.AsNum(ra[1]) ?? 0), (int)(J.AsNum(ra[2]) ?? 100), (int)(J.AsNum(ra[3]) ?? 100));
            what = "rect";
        }
        else if (J.Has(args, "anchor"))
        {
            var a = AnchorSpec.Parse(J.Get(args, "anchor"), Engine.Tutorial?.Defaults);
            if (a.IsScreen) { rect = Native.MonitorRectAt(new PointF(a.Dx, a.Dy)); what = "monitor"; }
            else
            {
                var r = Resolver.ResolveNow(a);
                if (!r.Found) return Fail($"anchor not found: {r.Reason} ({a.Describe()})");
                if (Native.IsIconic(r.Root)) return Fail("the anchor's window is minimized");
                var frame = Native.FrameRect(r.Root);
                RectangleF b = r.Rel is RectangleF rel ? new RectangleF(frame.X + rel.X, frame.Y + rel.Y, rel.Width, rel.Height)
                    : a.Client ? Native.ClientScreenRect(r.Root) : frame;
                anchorBounds = b;
                window = r.Root;
                var o = a.OriginOf(b);
                anchorPoint = new PointF(o.X + a.Dx, o.Y + a.Dy);
                int pad = J.Int(args, "pad") ?? 0;
                rect = Rectangle.Round(b);
                rect.Inflate(pad, pad);
                what = a.Describe();
            }
        }
        else
        {
            Native.GetCursorPos(out var cp);
            rect = Native.MonitorRectAt(new PointF(cp.X, cp.Y));
            what = "monitor under cursor";
        }
        rect = Rectangle.Intersect(rect, Native.VirtualScreen());
        if (rect.Width <= 0 || rect.Height <= 0) return Fail("rectangle is off screen");

        bool withOverlay = J.Bool(args, "includeOverlay") ?? false;
        // the app's own window (works while it is covered) unless the screen is asked for / the overlay must be in it
        string source = (J.Str(args, "source") ?? (window != IntPtr.Zero && !withOverlay ? "window" : "screen")).ToLowerInvariant();
        Bitmap? winShot = source == "window" && window != IntPtr.Zero ? ScreenCapture.CaptureWindow(window, rect, maxAgeMs: 0) : null;
        // flip exclusion only when the wanted result differs from the normal state, then restore it
        bool flip = withOverlay == Settings.Global.HideFromCapture;
        if (winShot != null) flip = false;
        if (flip) { Program.SetCapturable(withOverlay); Thread.Sleep(80); }
        Bitmap bmp;
        try { bmp = winShot ?? ScreenCapture.Capture(rect); }
        finally { if (flip) Program.SetCapturable(!withOverlay); }
        using var _bmp = bmp;
        int maxEdge = J.Int(args, "maxEdge") ?? DefaultMaxEdge;
        var png = ScreenCapture.Png(bmp, maxEdge, out float scale);
        var info = new JsonObject
        {
            ["captured"] = what,
            ["source"] = winShot != null ? "window (its own pixels, even if covered)" : "screen",
            ["screenRect"] = J.RectJson(rect),
            ["imageScale"] = Math.Round(scale, 4),
            ["mapping"] = $"screenX = {rect.X} + imgX / {scale:0.####}; screenY = {rect.Y} + imgY / {scale:0.####}",
        };
        // a strongly reduced image is fine for an overview, not for measuring or reading small text
        if (scale < 0.6f)
            info["precision"] = $"reduced ×{scale:0.##}: 1 image px = {1 / scale:0.#} screen px, small text may be unreadable. " +
                                "For exact positions use find_text / pick / an element anchor; to read details screenshot a smaller anchor or maxEdge:0.";
        if (anchorPoint is PointF ap)
        {
            info["anchorPoint"] = J.PtJson(ap);
            info["anchorPointInImage"] = J.PtJson(new PointF((ap.X - rect.X) * scale, (ap.Y - rect.Y) * scale));
            info["offsetHint"] = "shape x,y (or anchor offset) = (imgX - anchorPointInImage.x) / imageScale, likewise for y";
        }
        if (anchorBounds is RectangleF ab) info["anchorBounds"] = J.RectJson(ab);
        return new JsonObject
        {
            ["content"] = new JsonArray(
                new JsonObject { ["type"] = "text", ["text"] = Json(info) },
                // the image itself follows
                new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(png), ["mimeType"] = "image/png" }),
        };
    }
}
