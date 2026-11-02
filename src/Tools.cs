using System;
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
        "Use keepOpen + append_steps to feed steps incrementally. Marks auto-hide when the app is minimised, covered or " +
        "not in the foreground and come back on the same step.";

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
        "origin:'topleft'|'center'|'topright'|'bottomleft'|'bottomright'|'top'|'bottom'|'left'|'right', offset:[dx,dy], relativeTo:'frame'|'client', hwnd, screen:true}. " +
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
            "steps:[{id, title, text, anchor (default for targets), targets:[{id, name (checklist label), anchor, shapes, toggle, check, button, optional, doneStyle:'hide'|'check', hint, number, arrow}], " +
            "until:[ 'all_clicked' | {type:'appear'|'disappear', title|window|element|anchor} | {type:'pixel', anchor, at, expr} | {type:'count', count} ], untilMode:'any'|'all', " +
            "dim, numbered, checklist, buttons:[{id,text,action,style}] (extra card buttons), controls:['back','undo','skip','done','stop']}]}. " +
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
                ("includeOverlay", T("boolean", "also capture the overlay's drawings and the card (to verify placement)")))),
        Tool("events", "Events since a sequence number (hit, miss, unhit, undo, step_started, step_completed, step_back, step_skipped, tutorial_*, window_opened/closed, anchor_found/lost, check_changed, button). With timeoutMs it long-polls until something happens.",
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
        if (withOverlay) { Program.SetCapturable(true); Thread.Sleep(80); }
        Bitmap bmp;
        try { bmp = ScreenCapture.Capture(rect); }
        finally { if (withOverlay) Program.SetCapturable(false); }
        using var _bmp = bmp;
        int maxEdge = J.Int(args, "maxEdge") ?? 1568;
        var png = ScreenCapture.Png(bmp, maxEdge, out float scale);
        var info = new JsonObject
        {
            ["captured"] = what,
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
