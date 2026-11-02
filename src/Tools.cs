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

    private const string AnchorDesc =
        "Anchor: {process:'FL64'|'notepad', window:'title substring', title:'top-level title OR UIA Window/Pane name (substring)', " +
        "element:{name,role,automationId,className,match:'contains'|'exact',pick:'largest'|'first'|'smallest',index} or an array of those (nested path), " +
        "origin:'topleft'|'center'|'topright'|'bottomleft'|'bottomright'|'top'|'bottom'|'left'|'right', offset:[dx,dy], relativeTo:'frame'|'client', hwnd, screen:true, " +
        "find:{template, within, axis} (visual anchor from the template tool: offset is measured from the match's top-left, so marks follow scrolling; " +
        "or find:{text:'Piano roll', match:'contains'|'exact'|'regex', index, lang, within} — found by OCR, offset measured from the text's centre, " +
        "for apps that draw their own UI and expose nothing to UIA; check the wording first with find_text; " +
        "targets that scroll out of the pane get an edge arrow 'scroll up/down/left/right')}. " +
        "Omit for screen-absolute coordinates (offset = absolute point). Shapes are positioned relative to origin+offset.";

    private const string ShapesDesc =
        "Shapes (all coords relative to the anchor point, physical px): " +
        "circle{x,y,r} (no x/y → ring around the anchor element), rect{x,y,w,h,pad,radius} (no w/h → frame around the element), " +
        "arrow{from:[x,y]|'nw'|'n'|..., to:[x,y], length} (auto-aims at the target), badge{text|n} (step number), " +
        "label{text} (caption, auto-placed without overlapping), info{title,text} (info box with icon), " +
        "check{} (green tick), checkbox{x,y,w,h} (shows ✓ once the target is done — good for toggle cells), " +
        "button{text,id,action:'done'|'next'|'back'|'skip'|'stop'|'undo'|'dismiss',style:'primary'|'secondary'|'success'|'danger'} " +
        "(a REAL clickable button window; emits a 'button' event), dim{opacity} (darken screen except targets). " +
        "Common: color ('#RRGGBB' or accent/red/orange/yellow/green/teal/blue/purple/white), thickness, size (font px @100%), target:false.";

    public static JsonArray List() => new()
    {
        Tool("draw", "Draw marks over any window (outside of a tutorial). Each mark = shapes sharing one anchor; clicking its hit area dismisses it (and still reaches the app). Returns mark ids. " + ShapesDesc,
            Obj(("shapes", T("array", ShapesDesc)), ("anchor", T("object", AnchorDesc)), ("dismissOnClick", T("boolean", "default true")),
                ("id", T("string", "mark id (re-using an id replaces the mark)")), ("toggle", T("boolean", "click on a dismissed mark re-lights it")),
                ("check", T("object", "pixel check {at:[x,y], expr:'r>140', radius} — mark counts as done while it holds")),
                ("button", T("string", "left|right|middle|any (default left)")), ("hideWhenInactive", T("boolean", "hide while the app is not in the foreground (default true)")),
                ("marks", T("array", "several marks at once: [{id, anchor, shapes, ...}]")), ("replace", T("boolean", "remove all other free marks")))),
        Tool("clear", "Remove free marks drawn with draw (all, or one id). Does not touch the tutorial.",
            Obj(("id", T("string", "mark id; omit for all")))),
        Tool("start_tutorial", "Start a step-by-step tutorial. Only the current step's targets are shown; steps advance by themselves. " +
            "spec = {title, app:'process name' (default for every anchor), window, dim:0..1, toggle, keepOpen (wait for append_steps after the last step), advanceDelayMs, hideWhenInactive, startAt, " +
            "outro (default true: after the last step the card offers Save / Go through again / Keep learning), " +
            "or {name:'saved-name', startAt} to replay a tutorial from the library (save_tutorial / list_tutorials), " +
            "lang:'ru'|'en', accent:'#RRGGBB', hotkeys:{done,back,skip,undo,stop:'Ctrl+Alt+Right'|'none'}, " +
            "steps:[{id, title, text, why (short 'why it works this way' shown on the card — TEACH the idea, not just the click; use it on every step where there is something to understand), anchor (default for targets), targets:[{id, name (checklist label; with grid may use {row} {col} {n}), anchor, shapes, toggle, check, button, optional, doneStyle:'hide'|'check', hint, number, arrow, " +
            "opens:{title|window|process|element, anyProcess, timeoutMs} (the click counts only when this NEW window/pane appears — inside the app or in another program; otherwise event no_effect), grid:{from:[x,y] centre of first cell rel. to anchor point, step:[dx,dy], rows, cols, cells:[[row,col],...] or [col,...] (1-based), w, h} → one target per cell (default shape: checkbox)}], " +
            "until:[ 'all_clicked' | {type:'appear'|'disappear', title|window|element|anchor} | {type:'pixel', anchor, at, expr} | {type:'count', count} " +
            "| {type:'key', keys:'Ctrl+S' or ['F6','Ctrl+F6']} (only while the app is in front; other keys are never recorded) " +
            "| {type:'drag', from:{anchor|element|title, rect:[x,y,w,h] rel. to anchor point, radius}, to:{...}, button, show} (drawn as source → drop zone arrow) " +
            "| {type:'uia', anchor|element|title, property:'exists'|'name'|'value'|'toggle'|'selected'|'expanded'|'enabled'|'focused'|'text', equals|contains|matches, not} ], untilMode:'any'|'all', " +
            "dim, numbered, checklist, sequence (targets one at a time in order, e.g. right-click → item of the popup menu anchored by {windowClass:'TQuickPopupMenuWindow'}; if that menu closes the chain restarts: event sequence_reset), buttons:[{id,text,action,style}] (extra card buttons), controls:['back','undo','skip','done','stop']}]}. " +
            "Without 'until', a step with targets completes when all targets are clicked; without targets it waits for Done / complete_step. " +
            "'disappear' only fires after the thing was first seen present. Clicks are matched by the PROCESS of the window under the cursor.",
            Obj(("spec", T("object", "tutorial spec (fields may also be passed at top level)")))),
        Tool("append_steps", "Append steps to the running tutorial (resumes it if it was waiting after the last step).",
            Obj(("steps", T("array", "steps, same format as in start_tutorial")), ("insertAt", T("integer", "optional index to insert at (after the current step)")))),
        Tool("goto_step", "Jump to a step (targets shown again, conditions reset).",
            Obj(("index", T("integer", "0-based")), ("id", T("string", "step id")), ("relative", T("string", "'next' or 'back'")))),
        Tool("complete_step", "Mark the current step done (as if the user pressed Done) and advance.",
            Obj(("expectIndex", T("integer", "only if the current step is this one (avoids races)")), ("immediate", T("boolean", "skip the short ✓ pause")))),
        Tool("stop_tutorial", "Stop the tutorial and hide its card.", new JsonObject()),
        Tool("status", "Current tutorial state, step targets with resolved screen rectangles and visibility reasons (visible/occluded/minimized/inactive/...), free marks, foreground window.", new JsonObject()),
        Tool("describe", "List windows and the UI Automation tree of an app with physical rectangles (and rel = offset from the window frame). Without app: lists top-level windows of all apps.",
            Obj(("app", T("string", "process name, e.g. FL64, notepad, chrome")), ("window", T("string", "title substring")),
                ("element", T("object", "only the subtree of this element (same format as anchor.element)")), ("depth", T("integer", "default 8")),
                ("maxNodes", T("integer", "default 600")), ("namedOnly", T("boolean", "skip nodes without name/automationId")))),
        Tool("screenshot", "Screenshot of an anchor's rectangle (window / element) or a screen area, WITHOUT the overlay's own drawings. Returns the mapping from image pixels to screen and to the anchor.",
            Obj(("anchor", T("object", AnchorDesc)), ("rect", T("array", "[x,y,w,h] absolute screen rect instead of an anchor")),
                ("pad", T("integer", "extra pixels around the anchor rect")), ("maxEdge", T("integer", "downscale so the long edge is ≤ this (default 1568, 0 = full size)")),
                ("includeOverlay", T("boolean", "also capture the overlay's drawings and the card (to verify placement; implies source=screen)")),
                ("source", T("string", "'window' (default with an anchor: the app's own pixels even when other windows cover it) or 'screen'")))),
        Tool("template", "Remember a small piece of an app's UI as a visual anchor (e.g. the 'C5' label on a piano roll keyboard, a bar number on a ruler). " +
            "Then any anchor can use find:{template:id, within:[x,y,w,h], axis:'y'|'x'|'xy'} to measure its offset from wherever that piece currently is — " +
            "marks follow scrolling content. Captured from the window's own pixels (works while covered). Returns the captured image.",
            Obj(("id", T("string", "name of the template")), ("anchor", T("object", AnchorDesc)),
                ("rect", T("array", "[x,y,w,h] relative to the anchor element's top-left (keep it small and distinctive, ~20-60 px)")))),
        Tool("hint", "Show your answer inside the step card (after a 'help' event, or proactively). Cleared when the step changes. highlight:true also dims the screen and pings the targets.",
            Obj(("text", T("string", "hint text")), ("title", T("string", "optional heading")), ("highlight", T("boolean", "spotlight the targets")),
                ("seconds", T("integer", "highlight duration (default 6)")), ("expectIndex", T("integer", "only if this is still the current step")))),
        Tool("pick", "Calibration: asks the user to click 1..count points and returns, for each, absolute coords, rel (offset from the anchor point), relToBounds, " +
            "the window, pixel colour, the UIA element under the point with ancestors, and a suggestedAnchor. The click is swallowed (does not reach the app) unless passThrough. " +
            "With count ≥ 2 on consecutive cells it also returns gridHint {from, step}. Esc cancels.",
            Obj(("anchor", T("object", "anchor to measure rel offsets against")), ("count", T("integer", "number of points (default 1)")),
                ("prompt", T("string", "text shown next to the cursor")), ("passThrough", T("boolean", "let the click reach the app (default false)")),
                ("timeoutMs", T("integer", "default 60000")))),
        Tool("find_text", "Read text on screen with the OCR built into Windows (works on apps that draw their own UI, e.g. FL Studio). " +
            "With text: where it is — screen rect, centre, rel (centre − anchor point) and a suggestedAnchor with find:{text} that keeps following it. " +
            "Without text: every recognised line with its rect (to see what is there and how the OCR spells it). Overlay drawings are excluded.",
            Obj(("text", T("string", "text to look for (case-insensitive; small OCR slips tolerated)")), ("anchor", T("object", AnchorDesc)),
                ("rect", T("array", "[x,y,w,h] absolute screen rect instead of an anchor")),
                ("within", T("array", "[x,y,w,h] part of the anchor element (relative to its top-left)")),
                ("match", T("string", "contains (default) | exact (whole line) | regex")),
                ("lang", T("string", "OCR language tag, e.g. 'en-US', 'ru' (default: the user's languages)")))),
        Tool("validate", "Check a tutorial before starting it (or the running one): resolves every target and condition anchor NOW and reports per step " +
            "ok / reason (process not running, not found, minimized, occluded, off screen, template not captured, text not visible). " +
            "Later steps often target things earlier steps open — those warnings are expected.",
            Obj(("spec", T("object", "same as start_tutorial's spec; omit to check the running tutorial")))),
        Tool("ask", "Ask the user a question in a panel above the step card (or in the screen corner, with no tutorial) and WAIT for the answer. " +
            "options → answer buttons ({choice, label}); input:true → a text field ({text}). Returns {cancelled} if closed, {timedOut} after timeoutMs.",
            Obj(("question", T("string", "the question")), ("title", T("string", "small heading (default: 'The assistant asks')")),
                ("options", T("array", "buttons: ['Yes','No'] or [{id, text, style:'primary'|'secondary'}]")),
                ("input", T("boolean", "add a text field (default: true when there are no options)")), ("placeholder", T("string", "text field hint")),
                ("timeoutMs", T("integer", "default 300000")))),
        Tool("ask_area", "Ask the user to circle something on screen (frozen screenshot + pencil) and WAIT. Returns bounds/strokes in screen px, " +
            "the UIA element at the centre with a suggestedAnchor and rel to the current step's first target, plus the image of the area with the circling.",
            Obj(("prompt", T("string", "what to circle, e.g. 'Circle the channel you want to edit'")), ("timeoutMs", T("integer", "default 180000")))),
        Tool("save_tutorial", "Save a tutorial to the user's library (%LOCALAPPDATA%\\TutorialOverlay\\tutorials): the current/last one (with appended steps) or the given spec. " +
            "Saved tutorials can be replayed from the tray menu without an agent, or with start_tutorial({name}). " +
            "At the end of every tutorial the card itself offers Save / Go through again / Keep learning (events: outro, tutorial_saved, continue_requested).",
            Obj(("name", T("string", "file name (default: from the title)")), ("spec", T("object", "spec to save instead of the current tutorial")))),
        Tool("list_tutorials", "Saved tutorials: name, title, app, number of steps, when saved.", new JsonObject()),
        Tool("delete_tutorial", "Remove a saved tutorial from the library.", Obj(("name", T("string", "its name from list_tutorials")))),
        Tool("events", "Events since a sequence number (hit, miss, unhit, undo, step_started, step_completed, step_back, step_skipped, tutorial_*, window_opened/closed, anchor_found/lost, check_changed, uia_changed, key_matched, drag, opened, no_effect, blocked (target clicked while a modal dialog of the app was open), button, pick, help (user pressed '?': reason cant_find|dont_understand|other — other carries the user's own text and, if they circled something with the pencil, sketch {bounds, strokes, atCenter, image: PNG path} — with target visibility and recent misses; answer with hint()), hook_reinstalled). With timeoutMs it long-polls until something happens.",
            Obj(("since", T("integer", "last seq you have seen (default 0)")), ("timeoutMs", T("integer", "wait up to this long for new events (max 60000)")))),
    };

    private static JsonObject Tool(string name, string desc, JsonObject props) => new()
    {
        ["name"] = name,
        ["description"] = desc,
        ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = props, ["additionalProperties"] = true },
    };

    public static async Task<JsonObject> Call(string name, JsonNode args, CancellationToken ct)
    {
        try
        {
            return name switch
            {
                "draw" => Text(new JsonObject { ["ids"] = new JsonArray(Engine.Draw(args).Select(i => (JsonNode)i).ToArray()) }),
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

    private static JsonObject Text(JsonNode o) => TextRaw(o.ToJsonString(J.Pretty));

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
        var content = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = r.info.ToJsonString(J.Pretty) });
        if (r.png != null) content.Add(new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(r.png), ["mimeType"] = "image/png" });
        return new JsonObject { ["content"] = content };
    }

    private static JsonObject FindText(JsonNode args)
    {
        var query = J.Str(args, "text");
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
        Bitmap? shot = window != IntPtr.Zero ? ScreenCapture.CaptureWindow(window, rect) : null;
        if (shot == null)
        {
            bool flip = !Settings.Global.HideFromCapture;
            if (flip) { Program.SetCapturable(false); Thread.Sleep(80); }
            try { shot = ScreenCapture.Capture(rect); }
            finally { if (flip) Program.SetCapturable(true); }
        }
        List<OcrLine> lines;
        using (shot) lines = Ocr.Read(shot, lang);

        RectangleF Screen(RectangleF r) => new(rect.X + r.X, rect.Y + r.Y, r.Width, r.Height);
        var info = new JsonObject { ["area"] = J.RectJson(rect) };
        if (query == null)
        {
            info["lines"] = new JsonArray(lines.Select(l => (JsonNode)new JsonObject { ["text"] = l.Text, ["rect"] = J.RectJson(Screen(l.Bounds)) }).ToArray());
            if (lines.Count == 0) info["note"] = "no text recognised (tiny or low-contrast text? try a smaller area, or lang)";
            return Text(info);
        }
        var found = Ocr.Find(lines, query, match).OrderBy(m => Math.Round(m.Rect.Y / 8)).ThenBy(m => m.Rect.X).ToList();
        var arr = new JsonArray();
        for (int i = 0; i < found.Count; i++)
        {
            var m = found[i];
            var sr = Screen(m.Rect);
            var c = new PointF(sr.X + sr.Width / 2, sr.Y + sr.Height / 2);
            var o = new JsonObject { ["index"] = i, ["text"] = m.Text, ["rect"] = J.RectJson(sr), ["center"] = J.PtJson(c), ["score"] = Math.Round(m.Score, 3) };
            if (anchorPoint is PointF ap) o["rel"] = J.PtJson(new PointF(c.X - ap.X, c.Y - ap.Y));
            if (anchorJson is JsonObject aj)
            {
                var sug = (JsonObject)aj.DeepClone();
                var find = new JsonObject { ["text"] = query };
                if (match != "contains") find["match"] = match;
                if (i > 0) find["index"] = i;
                if (lang != null) find["lang"] = lang;
                if (J.Get(args, "within") is JsonNode wn) find["within"] = wn.DeepClone();
                sug["find"] = find;
                sug.Remove("offset");
                o["suggestedAnchor"] = sug;
            }
            arr.Add(o);
        }
        info["matches"] = arr;
        if (element is RectangleF el) info["elementBounds"] = J.RectJson(el);
        if (found.Count == 0)
            info["near"] = new JsonArray(lines.Take(40).Select(l => (JsonNode)new JsonObject { ["text"] = l.Text, ["rect"] = J.RectJson(Screen(l.Bounds)) }).ToArray());
        info["note"] = found.Count == 0 ? "not found; 'near' lists what the OCR read in this area"
            : "suggestedAnchor marks the text's centre and follows it; add offset:[dx,dy] to point beside it";
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
        using var shot = ScreenCapture.CaptureWindow(r.Root, rect);
        if (shot == null) return Fail("could not capture the window (minimized?)");
        Finder.Templates[id] = Template.From(shot);
        var png = ScreenCapture.Png(shot, 0, out _);
        return new JsonObject
        {
            ["content"] = new JsonArray(
                new JsonObject { ["type"] = "text", ["text"] = $"template '{id}' saved ({rect.Width}x{rect.Height}) at {J.RectJson(rect).ToJsonString()}; use anchor.find:{{template:'{id}'}}" },
                new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(png), ["mimeType"] = "image/png" }),
        };
    }

    private static async Task<JsonObject> EventsTool(JsonNode args, CancellationToken ct)
    {
        long since = (long)(J.Num(args, "since") ?? 0);
        int timeout = Math.Clamp(J.Int(args, "timeoutMs") ?? 0, 0, 60000);
        var list = timeout > 0 ? await Events.WaitSince(since, timeout, ct) : Events.Since(since);
        return Text(new JsonObject
        {
            ["events"] = new JsonArray(list.Select(e => e.DeepClone()).ToArray()),
            ["lastSeq"] = list.Count > 0 ? (long)list[^1]["seq"]! : Math.Max(since, 0),
        });
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
        Bitmap? winShot = source == "window" && window != IntPtr.Zero ? ScreenCapture.CaptureWindow(window, rect) : null;
        // flip exclusion only when the wanted result differs from the normal state, then restore it
        bool flip = withOverlay == Settings.Global.HideFromCapture;
        if (winShot != null) flip = false;
        if (flip) { Program.SetCapturable(withOverlay); Thread.Sleep(80); }
        Bitmap bmp;
        try { bmp = winShot ?? ScreenCapture.Capture(rect); }
        finally { if (flip) Program.SetCapturable(!withOverlay); }
        using var _bmp = bmp;
        int maxEdge = J.Int(args, "maxEdge") ?? 1568;
        var png = ScreenCapture.Png(bmp, maxEdge, out float scale);
        var info = new JsonObject
        {
            ["captured"] = what,
            ["source"] = winShot != null ? "window (its own pixels, even if covered)" : "screen",
            ["screenRect"] = J.RectJson(rect),
            ["imageScale"] = Math.Round(scale, 4),
            ["mapping"] = $"screenX = {rect.X} + imgX / {scale:0.####}; screenY = {rect.Y} + imgY / {scale:0.####}",
        };
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
                new JsonObject { ["type"] = "text", ["text"] = info.ToJsonString(J.Pretty) },
                // the image itself follows
                new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(png), ["mimeType"] = "image/png" }),
        };
    }
}
