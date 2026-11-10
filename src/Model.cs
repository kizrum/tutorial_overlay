using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace TutorialOverlay;

/// <summary>One segment of a UI Automation path: name / role / automationId / className.</summary>
internal sealed class ElementSpec
{
    public string? Name;
    public bool NameExact;
    public string? Role;
    public string? AutomationId;
    public string? ClassName;
    public string Pick = "largest";   // largest | smallest | first | last
    public int? Index;

    public static ElementSpec Parse(JsonNode n)
    {
        if (n is JsonValue) return new ElementSpec { Name = J.AsStr(n) };
        return new ElementSpec
        {
            Name = J.Str(n, "name"),
            NameExact = (J.Str(n, "match") ?? "contains").Equals("exact", StringComparison.OrdinalIgnoreCase),
            Role = J.Str(n, "role") ?? J.Str(n, "controlType") ?? J.Str(n, "type"),
            AutomationId = J.Str(n, "automationId") ?? J.Str(n, "id"),
            ClassName = J.Str(n, "className") ?? J.Str(n, "class"),
            Pick = (J.Str(n, "pick") ?? "largest").ToLowerInvariant(),
            Index = J.Int(n, "index"),
        };
    }

    public string Describe()
    {
        var parts = new List<string>();
        if (Role != null) parts.Add(Role);
        if (Name != null) parts.Add(NameExact ? $"\"{Name}\"" : $"~\"{Name}\"");
        if (AutomationId != null) parts.Add("#" + AutomationId);
        if (ClassName != null) parts.Add("." + ClassName);
        if (Index != null) parts.Add($"[{Index}]");
        else if (Pick != "largest") parts.Add(Pick);
        return string.Join(" ", parts);
    }
}

/// <summary>
/// Visual anchor: find a remembered template (or a piece of text, via OCR) inside the element; the point is measured
/// from the match — the template's top-left, or the centre of the text.
/// </summary>
internal sealed class FindSpec
{
    public string Template = "";
    public string? Text;              // OCR: text to look for instead of a template (the first of Texts)
    public List<string> Texts = new(); // every wording that counts ("General settings", "General preferences"), tried in order
    public string TextMatch = "contains";   // contains | exact | regex
    public string? Lang;              // OCR language tag (default: the user's languages)
    public int Nth;                   // which occurrence, top-to-bottom then left-to-right (0 = first)
    public bool NthGiven;             // the author chose the occurrence (index / nth), so several of them are expected
    public RectangleF? Within;        // search only this part of the element (relative to its top-left)
    // in: which of the app's windows the text lives in — "main", or a window by its title's start and class
    // ("Settings" of "Settings - Look and feel", TMIDIForm); a menu without a title by its class. Written by hand, or
    // learned from the window the user's click found it in, so going back or replaying never takes the same words in
    // another window (the "Theme settings" item of a menu for the Theme tab of the settings).
    public bool InMain;
    public string? InTitle, InClass;
    public bool HasIn => InMain || InTitle != null || InClass != null;
    public string Axis = "xy";        // xy | x | y — which coordinates come from the match (y: keyboard rows, x: bar ruler)
    public float MaxScore = 0.08f;    // mean abs difference allowed (0 identical … 1 opposite)

    public static FindSpec? Parse(JsonNode? n)
    {
        n = J.Unwrap(n);
        if (n == null) return null;
        if (n is JsonValue) return new FindSpec { Template = J.AsStr(n) ?? "" };
        var f = new FindSpec
        {
            Template = J.Str(n, "template") ?? J.Str(n, "id") ?? "",
            Axis = (J.Str(n, "axis") ?? "xy").ToLowerInvariant(),
            MaxScore = (float)(J.Num(n, "maxScore") ?? 0.08),
            Text = J.Get(n, "text") is JsonArray ? null : J.Str(n, "text"),
            TextMatch = (J.Str(n, "match") ?? "contains").ToLowerInvariant(),
            Lang = J.Str(n, "lang"),
            Nth = J.Int(n, "index") ?? J.Int(n, "nth") ?? 0,
            NthGiven = J.Has(n, "index") || J.Has(n, "nth"),
        };
        // text: one wording, or several the app may use (versions, languages) — the first one found wins
        if (J.Arr(n, "text") is JsonArray variants)
            f.Texts = variants.Select(J.AsStr).OfType<string>().Where(s => s.Trim().Length > 0).ToList();
        else if (f.Text != null) f.Texts = new List<string> { f.Text };
        f.Text = f.Texts.FirstOrDefault();
        var inn = J.Unwrap(J.Get(n, "in"));
        if (inn is JsonValue && J.AsStr(inn) is string iv) { if (iv.Equals("main", StringComparison.OrdinalIgnoreCase)) f.InMain = true; else f.InTitle = iv; }
        else if (inn is JsonObject io) { f.InTitle = J.Str(io, "title"); f.InClass = J.Str(io, "class"); }
        if (J.Arr(n, "within") is JsonArray a && a.Count >= 4)
            f.Within = new RectangleF((float)(J.AsNum(a[0]) ?? 0), (float)(J.AsNum(a[1]) ?? 0), (float)(J.AsNum(a[2]) ?? 0), (float)(J.AsNum(a[3]) ?? 0));
        return f;
    }

    /// <summary>The wordings for messages: "A" or "A / B".</summary>
    public string TextShown => string.Join(" / ", Texts);

    public string Key => Text != null ? $"text:{string.Join("|", Texts)}/{TextMatch}/{Lang}/{Nth}/{Within}/{Axis}/{InShown}" : $"{Template}/{Within}/{Axis}/{MaxScore}";

    /// <summary>The window it lives in, for messages: "main", "Settings", "the TQuickPopupMenuWindow window"; null: any.</summary>
    public string? InShown => InMain ? "main" : InTitle ?? (InClass != null ? InClass : null);

    /// <summary>The window as JSON for find.in: "main" or {title, class}.</summary>
    public JsonNode? InJson() => InMain ? JsonValue.Create("main")
        : HasIn ? new JsonObject { ["title"] = InTitle, ["class"] = InClass } : null;

    /// <summary>A window of the app is the one find.in names (main is told apart by the caller).</summary>
    public bool InMatches(string title, string cls) =>
        !InMain && (InClass != null || InTitle != null)
        && (InClass == null || cls.Equals(InClass, StringComparison.OrdinalIgnoreCase))
        && (InTitle == null || title.TrimStart().StartsWith(InTitle, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Where a mark lives: process → top-level window (title substring) → optional UIA path, plus a point
/// (origin of the resolved rectangle + offset in physical pixels).
/// </summary>
internal sealed class AnchorSpec
{
    public string? Process;          // "FL64", "notepad.exe", ...
    public string? Window;           // top-level title substring
    public string? WindowClass;
    public string? Title;            // "window or pane" title: top-level title OR UIA Window/Pane name (substring)
    public long? Hwnd;
    public List<ElementSpec> Path = new();
    public string Origin = "topleft";
    public float Dx, Dy;
    public bool Client;              // window-only anchors: client area instead of visible frame
    public string? Read;             // UIA property the resolver should read (uia conditions)
    public FindSpec? Find;           // visual anchor inside the element (follows scrolling content)

    /// <summary>An offset or origin was given: the anchor names a point, not just an element.</summary>
    public bool HasPoint => Dx != 0 || Dy != 0 || Origin != "topleft";

    public bool IsScreen => Process == null && Window == null && WindowClass == null && Title == null && Hwnd == null;
    public bool HasBounds => !IsScreen;

    /// <summary>Resolution identity (ignores origin/offset, which only matter for layout).</summary>
    public string Key => string.Join("|", Process?.ToLowerInvariant(), Window, WindowClass, Title, Hwnd, Client, Read, Find?.Key,
        string.Join(">", Path.Select(p => $"{p.Role}/{p.Name}/{p.NameExact}/{p.AutomationId}/{p.ClassName}/{p.Pick}/{p.Index}")));

    public static AnchorSpec Parse(JsonNode? n, AnchorSpec? defaults)
    {
        n = J.Unwrap(n);
        var a = new AnchorSpec();
        if (n is JsonValue)
        {
            // bare string = window title
            a.Window = J.AsStr(n);
        }
        else if (n is JsonObject)
        {
            a.Process = NormalizeProcess(J.Str(n, "process") ?? J.Str(n, "app"));
            a.Window = J.Str(n, "window");
            a.WindowClass = J.Str(n, "windowClass");
            a.Title = J.Str(n, "title");
            a.Hwnd = J.Num(n, "hwnd") is double h ? (long)h : ParseHex(J.Str(n, "hwnd"));
            a.Client = (J.Str(n, "relativeTo") ?? "").Equals("client", StringComparison.OrdinalIgnoreCase) || J.Bool(n, "client") == true;
            a.Origin = (J.Str(n, "origin") ?? "topleft").ToLowerInvariant().Replace("-", "").Replace("_", "");
            var el = J.Unwrap(J.Get(n, "element") ?? J.Get(n, "path"));
            foreach (var seg in J.Items(el)) a.Path.Add(ElementSpec.Parse(seg));
            a.Find = FindSpec.Parse(J.Get(n, "find"));
            var off = J.Pt(n, "offset");
            if (off is PointF o) { a.Dx = o.X; a.Dy = o.Y; }
            if (J.Num(n, "dx") is double dx) a.Dx = (float)dx;
            if (J.Num(n, "dy") is double dy) a.Dy = (float)dy;
        }
        if (defaults != null && a.Process == null && a.Hwnd == null && J.Bool(n, "screen") != true)
        {
            // inherit the tutorial's app unless the anchor is explicitly screen-absolute ("screen": true)
            a.Process = defaults.Process;
            if (a.Window == null && a.Title == null && a.Path.Count == 0 && defaults.Window != null) a.Window = defaults.Window;
        }
        return a;
    }

    /// <summary>The same anchor without its visual search — what the resolver resolves.</summary>
    /// <summary>The same anchor in one particular top-level window.</summary>
    public AnchorSpec WithHwnd(IntPtr hwnd)
    {
        var c = (AnchorSpec)MemberwiseClone();
        c.Hwnd = hwnd.ToInt64();
        return c;
    }

    public AnchorSpec WithoutFind()
    {
        var c = (AnchorSpec)MemberwiseClone();
        c.Find = null;
        return c;
    }

    public AnchorSpec With(float dx = 0, float dy = 0, string? read = null)
    {
        var c = (AnchorSpec)MemberwiseClone();
        c.Dx += dx; c.Dy += dy;
        if (read != null) c.Read = read;
        return c;
    }

    private static long? ParseHex(string? s)
    {
        if (s == null) return null;
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && long.TryParse(s[2..], NumberStyles.HexNumber, null, out var v)) return v;
        return long.TryParse(s, out v) ? v : null;
    }

    public static string? NormalizeProcess(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return null;
        p = p.Trim();
        if (p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) p = p[..^4];
        return p;
    }

    public PointF OriginOf(RectangleF r) => Origin switch
    {
        "center" or "centre" => new PointF(r.X + r.Width / 2, r.Y + r.Height / 2),
        "topright" => new PointF(r.Right, r.Y),
        "bottomleft" => new PointF(r.X, r.Bottom),
        "bottomright" => new PointF(r.Right, r.Bottom),
        "top" or "topcenter" => new PointF(r.X + r.Width / 2, r.Y),
        "bottom" or "bottomcenter" => new PointF(r.X + r.Width / 2, r.Bottom),
        "left" => new PointF(r.X, r.Y + r.Height / 2),
        "right" => new PointF(r.Right, r.Y + r.Height / 2),
        _ => new PointF(r.X, r.Y),
    };

    public string Describe()
    {
        if (IsScreen) return $"screen+({Dx},{Dy})";
        var parts = new List<string>();
        if (Process != null) parts.Add(Process);
        if (Hwnd != null) parts.Add($"hwnd 0x{Hwnd:X}");
        if (Window != null) parts.Add($"window~\"{Window}\"");
        if (Title != null) parts.Add($"title~\"{Title}\"");
        if (WindowClass != null) parts.Add($"class {WindowClass}");
        foreach (var p in Path) parts.Add("> " + p.Describe());
        if (Find != null) parts.Add(Find.Text != null ? $"find text \"{Find.TextShown}\"" + (Find.Nth > 0 ? $" #{Find.Nth}" : "") + (Find.InShown is string inn ? $" in {inn}" : "") : $"find template '{Find.Template}'");
        return string.Join(" ", parts);
    }
}

/// <summary>"r>140 &amp;&amp; g&lt;100", "lum>=120 || b>200", "~#FF8800:40" (color within distance).</summary>
internal sealed class PixelCheck
{
    public AnchorSpec? Anchor;       // null = mark's anchor
    public PointF? At;               // relative to anchor point; null = mark center
    public string Expr = "lum>128";
    public int Radius;               // average over (2r+1)^2 pixels

    public static PixelCheck? Parse(JsonNode? n, AnchorSpec? defaults)
    {
        n = J.Unwrap(n);
        if (n == null) return null;
        if (n is JsonValue) return new PixelCheck { Expr = J.AsStr(n) ?? "lum>128" };
        return new PixelCheck
        {
            Anchor = J.Has(n, "anchor") ? AnchorSpec.Parse(J.Get(n, "anchor"), defaults) : null,
            At = J.Pt(n, "at"),
            Expr = J.Str(n, "expr") ?? J.Str(n, "when") ?? "lum>128",
            Radius = Math.Clamp(J.Int(n, "radius") ?? 1, 0, 6),
        };
    }

    public bool Evaluate(Color c)
    {
        foreach (var orPart in Expr.Split("||"))
        {
            bool all = true;
            foreach (var andPart in orPart.Split("&&"))
                if (!Term(andPart.Trim(), c)) { all = false; break; }
            if (all) return true;
        }
        return false;
    }

    private static bool Term(string t, Color c)
    {
        if (t.Length == 0) return true;
        if (t.StartsWith('~'))
        {
            var parts = t[1..].Split(':');
            var want = J.ParseColor(parts[0], Color.Black);
            double tol = parts.Length > 1 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 40;
            double dist = Math.Sqrt(Sq(c.R - want.R) + Sq(c.G - want.G) + Sq(c.B - want.B));
            return dist <= tol;
        }
        string[] ops = { ">=", "<=", "!=", "==", ">", "<", "=" };
        foreach (var op in ops)
        {
            int i = t.IndexOf(op, StringComparison.Ordinal);
            if (i <= 0) continue;
            var lhs = t[..i].Trim().ToLowerInvariant();
            if (!double.TryParse(t[(i + op.Length)..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var rhs)) return false;
            double v = Channel(lhs, c);
            return op switch
            {
                ">=" => v >= rhs, "<=" => v <= rhs, "!=" => Math.Abs(v - rhs) > 0.5,
                "==" or "=" => Math.Abs(v - rhs) <= 0.5, ">" => v > rhs, "<" => v < rhs, _ => false
            };
        }
        return false;
    }

    private static double Sq(double x) => x * x;

    private static double Channel(string name, Color c)
    {
        // simple arithmetic like "r-g" is handy for "is it reddish"
        int minus = name.IndexOf('-', 1);
        if (minus > 0) return Channel(name[..minus], c) - Channel(name[(minus + 1)..], c);
        return name switch
        {
            "r" or "red" => c.R,
            "g" or "green" => c.G,
            "b" or "blue" => c.B,
            "lum" or "bright" or "brightness" or "l" => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B,
            "max" => Math.Max(c.R, Math.Max(c.G, c.B)),
            "min" => Math.Min(c.R, Math.Min(c.G, c.B)),
            "sat" => Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B)),
            _ => double.NaN
        };
    }
}

internal sealed class ShapeSpec
{
    public string Type = "circle";   // circle | rect | auto | brackets | pill | marker | band | forbid | arrow | badge | label | info | check | checkbox | button | dim | path
    public float? X, Y, W, H, R;
    public PointF? From, To;
    public string? FromDir;
    public float? Length;
    public string? Text;
    public string? Title;            // info
    public string? ButtonId, Style, Action;  // button
    public Color? Color;
    public float? Width;
    public float? Size;
    public float Pad = 6;
    public bool PadGiven;            // pad was written in the spec (else each shape has its own)
    public float Radius = 8;
    public bool? Target;
    public float Opacity = 0.5f;
    public List<PointF>? Points;     // path: points relative to the anchor point
    public bool Smooth = true;       // path: a curve through the points (false: straight segments)
    public float? Curve;             // arrow: how far it bows, as a share of its length (the side is chosen by us)
    public string? End;              // arrow: what its tip ends in — arrow | dot | chevron | bar | ring | none
    public string? Taper;            // arrow: none | tail (thin at the tail, full at the tip) | both (thin–thick–thin)
    public string? Route;            // arrow: elbow — only across and down, a rounded corner between (dense grids)

    public bool IsTargetDefault => Type is "circle" or "rect" or "checkbox" or "auto" or "brackets" or "pill" or "marker" or "band";
    /// <summary>Shapes that take the target at its own size, measured live — the frame of <c>auto</c>, corners, a capsule, a highlighter band.</summary>
    public bool FitsTarget => Type is "auto" or "brackets" or "pill" or "marker";
    public bool IsTarget => Target ?? IsTargetDefault;

    public static ShapeSpec Parse(JsonNode n)
    {
        var t = (J.Str(n, "type") ?? J.Str(n, "shape") ?? "circle").ToLowerInvariant();
        t = t switch
        {
            "frame" or "box" or "rectangle" or "border" or "outline" => "rect",
            "fit" or "target" => "auto",
            "number" or "step" or "num" => "badge",
            "text" or "caption" or "hint" or "tooltip" => "label",
            "darken" or "shade" or "spotlight" => "dim",
            "callout" or "note" or "infotext" or "info_text" or "panel" => "info",
            "tick" or "checkmark" or "done" => "check",
            "toggle" or "cell" => "checkbox",
            "btn" => "button",
            "ring" or "ellipse" => "circle",
            "corners" or "bracket" or "viewfinder" => "brackets",
            "capsule" or "segment" or "lozenge" => "pill",
            "highlight" or "highlighter" => "marker",
            "row" or "stripe" or "strip" => "band",
            "forbidden" or "danger" or "avoid" or "no" or "dont" => "forbid",
            "trajectory" or "curve" or "polyline" or "route" => "path",
            _ => t
        };
        var s = new ShapeSpec
        {
            Type = t,
            X = F(J.Num(n, "x")), Y = F(J.Num(n, "y")),
            W = F(J.Num(n, "w") ?? J.Num(n, "width_px") ?? J.Num(n, "wide")),
            H = F(J.Num(n, "h") ?? J.Num(n, "height")),
            R = F(J.Num(n, "r") ?? J.Num(n, "radius_px")),
            To = J.Pt(n, "to"),
            Length = F(J.Num(n, "length")),
            Text = J.Str(n, "text") ?? J.Str(n, "label") ?? J.Str(n, "n"),
            Color = J.Has(n, "color") ? J.ParseColor(J.Str(n, "color"), Palette.Accent) : null,
            Width = F(J.Num(n, "thickness") ?? J.Num(n, "stroke") ?? J.Num(n, "lineWidth")),
            Size = F(J.Num(n, "size") ?? J.Num(n, "fontSize")),
            Target = J.Bool(n, "target"),
            Title = J.Str(n, "title"),
            ButtonId = J.Str(n, "id") ?? J.Str(n, "buttonId"),
            Style = J.Str(n, "style"),
            Action = J.Str(n, "action"),
        };
        if (J.Num(n, "w") == null && J.Num(n, "width") is double wd && t == "rect") s.W = (float)wd;
        else if (J.Num(n, "width") is double lw && s.Width == null) s.Width = (float)lw;
        if (J.Num(n, "pad") is double pad) { s.Pad = (float)pad; s.PadGiven = true; }
        if (t == "path")
        {
            s.Points = Condition.ParsePoints(J.Get(n, "points") ?? J.Get(n, "path"));
            s.Smooth = J.Bool(n, "smooth") ?? true;
        }
        if (J.Num(n, "curve") is double cv) s.Curve = (float)Math.Clamp(cv, 0, 0.5);
        else if (J.Bool(n, "curve") == true || J.Str(n, "curve")?.ToLowerInvariant() == "auto") s.Curve = DefaultCurve;
        s.End = EndOf(J.Str(n, "end"));
        s.Taper = TaperOf(J.Str(n, "taper"));
        if (J.Str(n, "route")?.Trim().ToLowerInvariant() is string route)
            s.Route = route is "elbow" or "orthogonal" or "ortho" or "manhattan" or "grid" ? "elbow" : route is "straight" or "line" or "direct" ? null : route;
        if (J.Num(n, "radius") is double rad) { if (t == "circle" || t == "badge") s.R = (float)rad; else s.Radius = (float)rad; }
        if (J.Num(n, "opacity") is double op) s.Opacity = (float)Math.Clamp(op, 0, 1);
        var from = J.Get(n, "from");
        if (J.AsPt(from) is PointF fp) s.From = fp;
        else if (J.AsStr(from) is string dir) s.FromDir = dir.ToLowerInvariant();
        if (J.Str(n, "dir") is string d2) s.FromDir = d2.ToLowerInvariant();
        return s;
    }

    private static float? F(double? d) => d is double v ? (float)v : null;

    /// <summary>The bow of <c>curve: true</c>: a fifth of the arrow's length.</summary>
    public const float DefaultCurve = 0.2f;
    public static readonly string[] Ends = { "arrow", "dot", "chevron", "bar", "ring", "none" };
    public static readonly string[] Tapers = { "none", "tail", "both" };

    /// <summary>An arrow's end by its name or a common other word for it; an unknown word is kept for the lint to name.</summary>
    public static string? EndOf(string? v) => v?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "head" or "triangle" or "arrowhead" => "arrow",
        "point" or "disc" or "dot" => "dot",
        "open" or "v" or "chevron" => "chevron",
        "line" or "tee" or "t" or "edge" or "bar" => "bar",
        "circle" or "o" or "click" or "ring" => "ring",
        "no" or "false" or "nothing" or "none" => "none",
        var x => x,
    };

    public static string? TaperOf(string? v) => v?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "no" or "false" or "flat" or "even" or "none" => "none",
        "true" or "yes" or "start" or "tail" => "tail",
        "brush" or "hand" or "both" => "both",
        var x => x,
    };
}

/// <summary>A clickable group of shapes sharing one anchor. Clicking its hit area dismisses all of it.</summary>
internal sealed class Mark
{
    public string Id = "";
    public AnchorSpec Anchor = new();
    public List<ShapeSpec> Shapes = new();
    public bool DismissOnClick = true;
    public bool? Toggle;
    public PixelCheck? Check;
    public string Button = "left";       // left | right | middle | any
    public bool Optional;
    public bool PinnedToOpened;           // learned to live in the window the previous step's click opened (a popup)
    public string? Label;                 // human name for events / checklist
    public string DoneStyle = "hide";     // hide | check (leave a green tick when satisfied)
    public bool? HideInactive;            // hide while the anchor's app is not in the foreground
    public AnchorSpec? Opens;             // the click must open this window / pane (inside the app or another process)
    public int OpensTimeoutMs = 6000;

    // runtime
    public bool Dismissed;
    public int Hits;
    public bool? CheckOk;
    public DateTime CheckAt;
    public bool CheckSampling;            // a screen read for the check is under way (off the UI thread)
    public int Epoch;                     // bumped on every reset: a reading that started before it is dropped
    public MarkGeo? Geo;
    public string Visibility = "resolving";
    public DateTime? ShownAt;
    public (string dir, RectangleF view, PointF toward, float beyond)? ScrollHint;   // target outside the visible part; beyond = px past the edge (-1 unknown)
    public Color? AutoColor;              // contrast colour picked against the app's pixels around the target
    public DateTime? OpensArmedAt;        // clicked, waiting for Opens to show up
    public HashSet<IntPtr>? OpensBefore;  // matching top-level windows at click time
    public bool OpensPresentBefore;
    // while UI Automation is slow to answer (Steam's big tree): the element found by its caption meanwhile
    public AnchorSpec? StandIn;           // built once: null when its caption cannot stand for it
    public bool StandInBuilt;
    public DateTime? PendingSince;        // since when its UIA search has not answered
    public bool StandInWanted;            // its caption is being looked for
    public bool ViaStandIn;               // the mark stands on the caption now, not on the element

    public string? Group;                 // targets of a step with the same group are outlined together, the group's name on the outline
    public PointF? OffscreenAt;           // where it is while off every monitor (a tab at the screen's edge points there)

    public bool HasHitArea => Shapes.Any(s => s.IsTarget);
    public bool HasButtons => Shapes.Any(s => s.Type == "button");

    public static Mark Parse(JsonNode n, AnchorSpec? defaults, string id)
    {
        n = J.Unwrap(n)!;
        var m = new Mark
        {
            Id = J.Str(n, "id") ?? id,
            // no own anchor: the whole step/draw anchor applies (element path included), not just its process
            Anchor = J.Has(n, "anchor") ? AnchorSpec.Parse(J.Get(n, "anchor"), defaults) : defaults?.With() ?? new AnchorSpec(),
            DismissOnClick = J.Bool(n, "dismissOnClick") ?? true,
            Toggle = J.Bool(n, "toggle"),
            Button = (J.Str(n, "button") ?? "left").ToLowerInvariant(),
            Optional = J.Bool(n, "optional") ?? false,
            Label = J.Str(n, "name") ?? J.Str(n, "label") ?? J.Str(n, "text"),
            DoneStyle = (J.Str(n, "doneStyle") ?? "hide").ToLowerInvariant(),
            HideInactive = J.Bool(n, "hideWhenInactive"),
            Group = J.Str(n, "group"),
        };
        if (J.Pt(n, "__cell") is PointF cell) m.Anchor = m.Anchor.With(cell.X, cell.Y);
        if (J.Unwrap(J.Get(n, "opens")) is JsonNode on)
        {
            m.Opens = AnchorSpec.Parse(J.Get(on, "anchor") ?? on, defaults);
            if (J.Bool(on, "anyProcess") == true) m.Opens.Process = null;
            m.OpensTimeoutMs = Math.Clamp(J.Int(on, "timeoutMs") ?? 6000, 500, 120000);
        }
        m.Check = PixelCheck.Parse(J.Get(n, "check"), defaults);
        foreach (var s in J.Items(J.Get(n, "shapes"))) m.Shapes.Add(ShapeSpec.Parse(s));
        if (m.Shapes.Count == 0)
        {
            // sensible default: frame around a UI element, ring around a bare point
            // a point (an offset into an element) gets a ring; an element or a found text / template, a frame round it
            bool hasBounds = !m.Anchor.HasPoint && (m.Anchor.Path.Count > 0 || m.Anchor.HasBounds);
            // no shapes: the target framed at its own size (an element, a found text, the visible thing at a spot)
            m.Shapes.Add(new ShapeSpec { Type = "auto", Pad = 3 });
            if (J.Str(n, "number") is string num) m.Shapes.Add(new ShapeSpec { Type = "badge", Text = num });
            if ((J.Str(n, "hint") ?? J.Str(n, "caption")) is string hint) m.Shapes.Add(new ShapeSpec { Type = "label", Text = hint });
            if (J.Bool(n, "arrow") == true) m.Shapes.Add(new ShapeSpec { Type = "arrow" });
        }
        return m;
    }

    public bool ToggleEffective(Step? step, Tutorial? tut) => Toggle ?? step?.Toggle ?? tut?.Toggle ?? false;

    public bool Satisfied => Check != null ? CheckOk == true : Dismissed;

    public void ResetRuntime()
    {
        Dismissed = false; Hits = 0; CheckOk = null; CheckAt = default; Geo = null; Visibility = "resolving"; ShownAt = null;
        CheckSampling = false; Epoch++;
        PendingSince = null; StandInWanted = false; ViaStandIn = false;
        OpensArmedAt = null; OpensBefore = null; OpensPresentBefore = false; AutoColor = null;
    }
}

/// <summary>A screen area tied to an anchor: its bounds, a rect relative to the anchor point, or a circle.</summary>
internal sealed class ScreenRegion
{
    public AnchorSpec Anchor = new();
    public RectangleF? Rect;
    public float? Radius;

    public static ScreenRegion Parse(JsonNode? n, AnchorSpec? defaults)
    {
        n = J.Unwrap(n);
        var r = new ScreenRegion { Anchor = AnchorSpec.Parse(J.Get(n, "anchor") ?? n, defaults) };
        if (J.Arr(n, "rect") is JsonArray a && a.Count >= 4)
            r.Rect = new RectangleF((float)(J.AsNum(a[0]) ?? 0), (float)(J.AsNum(a[1]) ?? 0), (float)(J.AsNum(a[2]) ?? 0), (float)(J.AsNum(a[3]) ?? 0));
        if (J.Num(n, "radius") is double rad) r.Radius = (float)rad;
        return r;
    }
}

internal sealed class Condition
{
    public string Type = "all_clicked";  // all_clicked | appear | disappear | pixel | count | key | drag | uia | trace | scroll
    public AnchorSpec? Anchor;
    public PixelCheck? Pixel;
    public int Count;
    public List<Chord> Keys = new();
    public ScreenRegion? From, To;
    public string Button = "left";
    public bool Show = true;
    public string Property = "exists";
    public string? EqualsText, ContainsText, Matches;
    public bool Not;

    // trace: drag along a path; drag/trace: keys held the whole time
    public List<PointF> Path = new();     // relative to the anchor point, physical px
    public bool Smooth = true, Ordered = true;
    public float Tolerance = 24, Coverage = 0.85f;
    public List<uint> Hold = new();
    public List<string> HoldNames = new();
    // scroll: wheel notches over an area (or until the step's targets come into view)
    public ScreenRegion? Area;
    public string Direction = "down";
    public int Notches = 3;
    public bool UntilVisible;

    // runtime
    public bool SeenPresent;
    public bool Met;
    public bool SeenUnmet;          // was false at least once during this visit of the step
    public bool TraceActive, TraceFailed, HoldBroken;
    public int TraceFrom, TraceMax;
    public string? FailReason;
    public DateTime FailedAt;
    public int ScrollCount;
    public int ScrollAccum;          // wheel delta so far: a notch is 120, a touchpad sends many small parts
    public bool SeenHidden;          // scroll until visible: the targets were off screen at some point
    public float ScrollPx;           // how far the area's content moved the asked way (seen in its pixels)
    public byte[]? ScrollPrev;       // the area's last look (half size, grey)
    public Size ScrollPrevSize;
    public DateTime ScrollSampledAt;
    public bool ScrollSampling;
    public DateTime OffAreaAt;       // the user scrolled the app, but not over the area: the mascot says where
    public bool PixelSampling;       // a screen read for a pixel condition is under way (off the UI thread)

    public static Condition Parse(JsonNode n, AnchorSpec? defaults)
    {
        n = J.Unwrap(n)!;
        if (n is JsonValue) return new Condition { Type = Norm(J.AsStr(n) ?? "all_clicked") };
        var c = new Condition { Type = Norm(J.Str(n, "type") ?? "all_clicked"), Count = J.Int(n, "count") ?? 0 };
        if (c.Type is "appear" or "disappear")
        {
            // either {anchor:{...}} or the anchor fields inline: {type, title|window|element|process}
            c.Anchor = AnchorSpec.Parse(J.Get(n, "anchor") ?? n, defaults);
        }
        else if (c.Type == "pixel")
        {
            c.Pixel = PixelCheck.Parse(n, defaults);
            c.Pixel!.Anchor = AnchorSpec.Parse(J.Get(n, "anchor"), defaults);
        }
        else if (c.Type == "key")
        {
            foreach (var k in J.Items(J.Get(n, "keys") ?? J.Get(n, "key")))
                if (TutorialOverlay.Keys.Parse(J.AsStr(k)) is Chord ch) c.Keys.Add(ch);
                else throw new ArgumentException($"key condition: cannot parse \"{J.AsStr(k)}\"");
            if (c.Keys.Count == 0) throw new ArgumentException("key condition needs keys, e.g. \"Ctrl+S\"");
            c.Anchor = AnchorSpec.Parse(J.Get(n, "anchor"), defaults);   // only counts while this app is in front
        }
        else if (c.Type == "drag")
        {
            c.From = ScreenRegion.Parse(J.Get(n, "from"), defaults);
            c.To = ScreenRegion.Parse(J.Get(n, "to"), defaults);
            c.Button = (J.Str(n, "button") ?? "left").ToLowerInvariant();
            c.Show = J.Bool(n, "show") ?? true;
            c.ParseHold(n);
        }
        else if (c.Type == "trace")
        {
            c.Anchor = AnchorSpec.Parse(J.Get(n, "anchor") ?? n, defaults);
            c.Path = ParsePoints(J.Get(n, "path") ?? J.Get(n, "points"));
            if (c.Path.Count < 2) throw new ArgumentException("trace condition needs a path of at least 2 points [[x,y],…] relative to the anchor point");
            c.Smooth = J.Bool(n, "smooth") ?? true;
            c.Ordered = J.Bool(n, "ordered") ?? J.Bool(n, "direction") ?? true;
            c.Tolerance = (float)(J.Num(n, "tolerance") ?? 24);
            c.Coverage = (float)Math.Clamp(J.Num(n, "coverage") ?? 0.85, 0.1, 1);
            c.Button = (J.Str(n, "button") ?? "left").ToLowerInvariant();
            c.Show = J.Bool(n, "show") ?? true;
            c.ParseHold(n);
        }
        else if (c.Type == "scroll")
        {
            if ((J.Get(n, "in") ?? J.Get(n, "area") ?? J.Get(n, "over")) is JsonNode area) c.Area = ScreenRegion.Parse(area, defaults);
            else if (J.Get(n, "anchor") is JsonNode an) c.Area = ScreenRegion.Parse(new JsonObject { ["anchor"] = an.DeepClone() }, defaults);
            c.Direction = (J.Str(n, "direction") ?? J.Str(n, "dir") ?? "down").ToLowerInvariant();
            c.Notches = Math.Max(1, J.Int(n, "notches") ?? J.Int(n, "count") ?? 3);
            c.UntilVisible = (J.Str(n, "until") ?? "").Equals("visible", StringComparison.OrdinalIgnoreCase) || J.Bool(n, "untilVisible") == true;
            c.Show = J.Bool(n, "show") ?? true;
            c.ParseHold(n);
        }
        else if (c.Type == "uia")
        {
            c.Property = (J.Str(n, "property") ?? J.Str(n, "prop") ?? "exists").ToLowerInvariant();
            c.Anchor = AnchorSpec.Parse(J.Get(n, "anchor") ?? n, defaults).With(read: c.Property == "exists" ? null : c.Property);
            c.EqualsText = J.Str(n, "equals") ?? J.Str(n, "is");
            c.ContainsText = J.Str(n, "contains");
            c.Matches = J.Str(n, "matches") ?? J.Str(n, "regex");
            c.Not = J.Bool(n, "not") ?? false;
        }
        return c;
    }

    internal static string Norm(string t) => t.ToLowerInvariant() switch
    {
        "all_clicked" or "targets" or "clicked" or "all" => "all_clicked",
        "appear" or "appeared" or "window_open" or "window_opened" or "open" or "opened" or "exists" => "appear",
        "disappear" or "disappeared" or "window_closed" or "closed" or "close" or "gone" => "disappear",
        "pixel" or "color" => "pixel",
        "hits" or "count" or "clicks" => "count",
        "manual" or "button" or "none" => "manual",
        "key" or "keys" or "hotkey" or "shortcut" or "press" => "key",
        "drag" or "drop" or "dragdrop" or "drag_drop" => "drag",
        "trace" or "path" or "trajectory" or "stroke" or "draw" => "trace",
        "scroll" or "wheel" => "scroll",
        "uia" or "property" or "element" or "state" => "uia",
        var x => x,
    };

    public void Reset()
    {
        SeenPresent = false; Met = false; SeenUnmet = false;
        TraceActive = TraceFailed = HoldBroken = false; FailReason = null; ScrollCount = 0; ScrollAccum = 0; SeenHidden = false; ScrollPx = 0; ScrollPrev = null;
        OffAreaAt = default;
    }

    public static List<PointF> ParsePoints(JsonNode? n)
    {
        var res = new List<PointF>();
        foreach (var p in J.Items(n))
            if (p is JsonArray a && a.Count >= 2) res.Add(new PointF((float)(J.AsNum(a[0]) ?? 0), (float)(J.AsNum(a[1]) ?? 0)));
            else if (J.Num(p, "x") is double x) res.Add(new PointF((float)x, (float)(J.Num(p, "y") ?? 0)));
        return res;
    }

    /// <summary>"hold": ["Shift"] / "Ctrl+Alt" — keys that must stay down for the whole drag.</summary>
    private void ParseHold(JsonNode n)
    {
        foreach (var k in J.Items(J.Get(n, "hold") ?? J.Get(n, "holding")))
            foreach (var part in (J.AsStr(k) ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                uint vk = part.ToLowerInvariant() switch
                {
                    "shift" => 0x10, "ctrl" or "control" => 0x11, "alt" => 0x12, "win" => 0x5B, "space" => 0x20,
                    _ => TutorialOverlay.Keys.Parse(part)?.Vk ?? 0,
                };
                if (vk == 0) throw new ArgumentException($"hold: cannot parse key '{part}'");
                if (!Hold.Contains(vk)) { Hold.Add(vk); HoldNames.Add(part.Length == 1 ? part.ToUpperInvariant() : char.ToUpperInvariant(part[0]) + part[1..]); }
            }
    }

    public JsonObject ToJson() => new()
    {
        ["type"] = Type, ["met"] = Met, ["seenPresent"] = SeenPresent,
        ["anchor"] = Anchor?.Describe() ?? Pixel?.Anchor?.Describe() ?? From?.Anchor.Describe(),
        ["keys"] = Keys.Count > 0 ? string.Join(" | ", Keys) : null,
        ["property"] = Type == "uia" ? Property : null,
        ["scrolled"] = Type == "scroll" ? ScrollCount : null,
    };
}

internal sealed class Step
{
    public string? Id;
    public string Title = "";
    public string Text = "";
    public string? Why;                          // "why it works this way" — the card teaches, not just instructs
    public string? Say;                          // the mascot's short line (default: the title)
    public DateTime EnteredAt;                   // when the step became current (mascot / highlight sync)
    public List<Mark> Targets = new();
    public List<Condition> Until = new();
    public bool UntilAll;                // false: any condition completes the step
    public bool? Toggle;
    public float? Dim;
    public int? AdvanceDelayMs;
    public bool Numbered;
    public bool Sequence;                        // targets one at a time, in order (click → menu item → ...)
    public bool? Checklist;
    public List<CardButton> Buttons = new();     // extra card buttons → "button" events
    public HashSet<string>? Controls;            // which standard card controls to show

    // runtime
    public readonly Stack<(Mark mark, bool prevDismissed)> History = new();
    public readonly List<JsonObject> Misses = new();   // recent misses, for help requests
    public string? Hint, HintTitle;                    // agent's answer shown on the card
    public readonly List<ChatLine> Chat = new();       // the conversation about this step ("Something else" and the answers)
    public DateTime AskedAt;                           // UTC: the user's last "help" on this step (kept when they move on)
    public string? AskedReason;                        // and its reason (cant_find | dont_understand | other)

    /// <summary>How long after a question an answer still reaches a step the user has left.</summary>
    public static readonly TimeSpan LateAnswer = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The user asked for help. "Something else" (their own words) starts or goes on with the step's conversation,
    /// with a note from the card when no agent would hear it; the other reasons stay a card hint (the caller's).
    /// Returns whether it went into the conversation.
    /// </summary>
    public bool Ask(string reason, string? text, string? image, int shapes, bool heard, string awayNote, DateTime now)
    {
        HelpPending = true;
        AskedAt = now;
        AskedReason = reason;
        if (reason != "other") return false;
        Chat.Add(new ChatLine("user", text ?? "", Image: image, Shapes: shapes));
        if (!heard) Chat.Add(new ChatLine("note", awayNote));
        return true;
    }

    /// <summary>The agent's answer on the current step: its next message in a conversation, else the card's hint.</summary>
    public void Answer(string? text, string? title)
    {
        if (Chat.Count > 0)
        {
            Chat.Add(new ChatLine("agent", text ?? "", title));
            Hint = HintTitle = null;
        }
        else
        {
            Hint = text;
            HintTitle = title;
        }
        HelpPending = false;
    }

    /// <summary>
    /// The agent's answer for this step after the user has moved on: it waits in the step's conversation (a question
    /// asked from the help menu becomes its first message). A hint nobody asked for, or asked long ago, is refused.
    /// </summary>
    public bool AnswerLate(string? text, string? title, DateTime now, string cantFind, string dontGet)
    {
        if (Chat.Count == 0 && now - AskedAt > LateAnswer) return false;
        if (Chat.Count == 0 && AskedReason is "cant_find" or "dont_understand")
            Chat.Add(new ChatLine("user", AskedReason == "cant_find" ? cantFind : dontGet));
        Chat.Add(new ChatLine("agent", text ?? "", title));
        return true;
    }
    public bool HelpPending;
    public bool Checking;                              // target_not_found sent: the agent is looking for the right item
    public List<PointF[]>? Sketch;                     // what the user circled with the help request (until the answer)
    public DateTime SketchAt;                          // when: the circling fades on its own after a few seconds
    public DateTime? HighlightUntil;
    public DateTime NextPing;
    public DateTime? MissPulseAt;        // a miss: the targets flash once the mascot points again
    public DateTime? SeqMissingSince;            // current sequence target's anchor gone (menu closed?)
    public bool Review;                          // entered with "Back": already-true conditions don't auto-complete it
    public Color? AutoColor;                     // one contrast colour for all targets of the step

    public static Step Parse(JsonNode n, AnchorSpec? defaults, int ordinal)
    {
        n = J.Unwrap(n)!;
        var s = new Step
        {
            Id = J.Str(n, "id"),
            Title = J.Str(n, "title") ?? "",
            Text = J.Str(n, "text") ?? J.Str(n, "description") ?? J.Str(n, "body") ?? "",
            Why = J.Str(n, "why") ?? J.Str(n, "explain") ?? J.Str(n, "because"),
            Say = J.Str(n, "say"),
            UntilAll = (J.Str(n, "untilMode") ?? "any").Equals("all", StringComparison.OrdinalIgnoreCase),
            Toggle = J.Bool(n, "toggle"),
            Dim = J.Num(n, "dim") is double d ? (float)d : (J.Bool(n, "dim") == true ? 0.45f : null),
            AdvanceDelayMs = J.Int(n, "advanceDelayMs"),
            Numbered = J.Bool(n, "numbered") ?? false,
        };
        // a step-level "anchor" is the default for all of its targets
        var stepDefaults = J.Has(n, "anchor") ? AnchorSpec.Parse(J.Get(n, "anchor"), defaults) : defaults;
        int i = 0;
        foreach (var t in J.Items(J.Get(n, "targets") ?? J.Get(n, "marks")))
            foreach (var cell in Grid.Expand(t))
                s.Targets.Add(Mark.Parse(cell, stepDefaults, $"s{ordinal}t{i++}"));
        if (J.Has(n, "shapes") && s.Targets.Count == 0)
            s.Targets.Add(Mark.Parse(n, stepDefaults, $"s{ordinal}t0"));
        foreach (var b in J.Items(J.Get(n, "buttons")))
        {
            if (b is JsonValue) { var txt = J.AsStr(b) ?? "?"; s.Buttons.Add(new CardButton(txt, txt, "secondary", null)); continue; }
            var text = J.Str(b, "text") ?? J.Str(b, "label") ?? J.Str(b, "id") ?? "?";
            s.Buttons.Add(new CardButton(J.Str(b, "id") ?? text, text, J.Str(b, "style") ?? "secondary", J.Str(b, "action")));
        }
        if (J.Arr(n, "controls") is JsonArray ctl)
            s.Controls = new HashSet<string>(ctl.Select(x => (J.AsStr(x) ?? "").ToLowerInvariant()));
        s.Checklist = J.Bool(n, "checklist");
        s.Sequence = J.Bool(n, "sequence") ?? false;
        s.InMenu = J.Bool(n, "inMenu") ?? false;
        if (s.Numbered)
            for (int k = 0; k < s.Targets.Count; k++)
                if (!s.Targets[k].Shapes.Any(x => x.Type == "badge"))
                    s.Targets[k].Shapes.Add(new ShapeSpec { Type = "badge", Text = (k + 1).ToString(), Target = false });
        foreach (var c in J.Items(J.Get(n, "until") ?? J.Get(n, "complete") ?? J.Get(n, "done")))
            s.Until.Add(Condition.Parse(c, stepDefaults));
        if (s.Until.Count == 0 && s.Targets.Any(t => !t.Optional && (t.HasHitArea || t.Check != null || t.HasButtons)))
            s.Until.Add(new Condition { Type = "all_clicked" });
        return s;
    }

    public bool InMenu;                              // its targets are in a menu / popup the previous step opens
    public string? DoneHow;                          // how it was last completed (targets, key, agent, …)
    public bool SeenThisVisit;                       // its target was on screen during this visit
    public DateTime? LostSince;                      // seen, then gone (a menu that closed): since when
    public bool CameBackAuto;                        // entered by going back on its own: its target's container is gone
    public int AutoBackDepth;                        // how many steps the lesson has gone back on its own to reach it
    public DateTime AutoBackAt;                      // when the lesson last went back from it on its own (kept across visits)
    public bool NextSeenAbsent;                      // the next step's menu items were seen not there during this visit
    public DateTime? NextAbsentSince;                // since when none of them is there

    public void ResetRuntime()
    {
        SeenThisVisit = false; LostSince = null; CameBackAuto = false; AutoBackDepth = 0; NextSeenAbsent = false; NextAbsentSince = null;
        foreach (var t in Targets) t.ResetRuntime();
        foreach (var c in Until) c.Reset();
        History.Clear();
        Misses.Clear();
        Hint = HintTitle = null;
        HelpPending = false;
        Checking = false;
        HighlightUntil = null;
        SeqMissingSince = null;
        AutoColor = null;
    }

    public IEnumerable<Mark> Required => Targets.Where(t => !t.Optional && (t.HasHitArea || t.Check != null || t.HasButtons));
}

/// <summary>
/// One message of a step's conversation: the user's question (with the picture they marked, if any), the agent's
/// answer, or a note from the card itself (the agent is away).
/// </summary>
internal sealed record ChatLine(string Who, string Text, string? Title = null, string? Image = null, int Shapes = 0)
{
    public bool FromUser => Who == "user";
}

/// <summary>running: on a step · waiting: after the last step for append_steps · finished · stopped.</summary>
internal enum TutorialState { Running, Waiting, Finished, Stopped }

internal sealed class Tutorial
{
    public string Title = "";
    public AnchorSpec Defaults = new();
    public List<Step> Steps = new();
    public int Index = -1;
    public TutorialState State = TutorialState.Running;
    public string StateName => State.ToString().ToLowerInvariant();   // as tools and events report it
    public bool KeepOpen;
    public readonly HashSet<IntPtr> MarkedWindows = new();   // windows a step's target has been shown in
    public bool Toggle;
    public float? Dim;
    public int AdvanceDelayMs = 450;
    public bool HideWhenInactive = true;
    public bool Outro = true;            // after the last step: offer save / repeat / continue on the card
    public bool Mascot = true;           // "Шажок" points at the targets (also needs the global setting)
    public JsonObject Spec = new();      // what it was started from (+ appended steps) — what "save" writes
    public string? SavedAs;              // name in the tutorial library, once saved
    public bool Changed;                 // repaired on the way (retarget, appended steps, a fixed version) since it was saved
    public bool ConfirmOverwrite;        // the end card asks before the saved version is replaced
    public string? Owner;                // MCP session that started it (null: the user, from the tray)

    public bool OutroOpen;               // finished, the end card is showing
    public DateTime? AdvanceAt;          // step completed, waiting for the short "✓" moment
    public DateTime WaitingSince;        // when the card started waiting for append_steps
    public bool WaitUnheard;             // "keep learning" pressed while no agent was following events
    public string? CompletedHow;

    public Step? Current => Index >= 0 && Index < Steps.Count ? Steps[Index] : null;
    public bool Active => State is TutorialState.Running or TutorialState.Waiting;
}

/// <summary>
/// "grid": one target description → a target per cell. Coordinates are relative to the anchor point:
/// from = centre of the first cell, step = distance between cell centres. Rows/cols/cells are 1-based.
/// </summary>
internal static class Grid
{
    public static IEnumerable<JsonNode> Expand(JsonNode t)
    {
        t = J.Unwrap(t)!;
        var g = J.Get(t, "grid");
        if (g == null) { yield return t; yield break; }
        var from = J.Pt(g, "from") ?? new PointF(0, 0);
        var step = J.Pt(g, "step") ?? new PointF((float)(J.Num(g, "dx") ?? 0), (float)(J.Num(g, "dy") ?? 0));
        int rows = Math.Max(1, J.Int(g, "rows") ?? 1), cols = Math.Max(1, J.Int(g, "cols") ?? 1);
        var cells = new List<(int r, int c)>();
        if (J.Arr(g, "cells") is JsonArray list)
        {
            foreach (var x in list)
            {
                if (x is JsonArray rc && rc.Count >= 2) cells.Add(((int)(J.AsNum(rc[0]) ?? 1), (int)(J.AsNum(rc[1]) ?? 1)));
                else if (J.AsNum(x) is double col) cells.Add((1, (int)col));   // bare numbers = columns of row 1
            }
        }
        else
        {
            for (int r = 1; r <= rows; r++)
                for (int c = 1; c <= cols; c++) cells.Add((r, c));
        }
        float w = (float)(J.Num(g, "w") ?? Math.Max(8, Math.Abs(step.X) * 0.85));
        float h = (float)(J.Num(g, "h") ?? Math.Max(8, Math.Min(Math.Abs(step.Y) > 0 ? Math.Abs(step.Y) * 0.7 : 1e9, w * 1.35)));
        string? baseId = J.Str(t, "id");
        string? nameTpl = J.Str(t, "name") ?? J.Str(t, "label");
        int n = 0;
        foreach (var (r, c) in cells)
        {
            n++;
            var o = (JsonObject)t.DeepClone();
            o.Remove("grid");
            o["__cell"] = new JsonArray(from.X + (c - 1) * step.X, from.Y + (r - 1) * step.Y);
            if (baseId != null) o["id"] = $"{baseId}-{r}-{c}";
            if (nameTpl != null)
            {
                o.Remove("label");
                o["name"] = nameTpl.Replace("{row}", r.ToString()).Replace("{col}", c.ToString()).Replace("{n}", n.ToString());
            }
            if (J.Get(o, "shapes") == null)
                o["shapes"] = new JsonArray(new JsonObject { ["type"] = "checkbox", ["x"] = -w / 2, ["y"] = -h / 2, ["w"] = w, ["h"] = h });
            yield return o;
        }
    }
}
