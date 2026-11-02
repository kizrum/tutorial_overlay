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

    public bool IsScreen => Process == null && Window == null && WindowClass == null && Title == null && Hwnd == null;
    public bool HasBounds => !IsScreen;

    /// <summary>Resolution identity (ignores origin/offset, which only matter for layout).</summary>
    public string Key => string.Join("|", Process?.ToLowerInvariant(), Window, WindowClass, Title, Hwnd, Client,
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
    public string Type = "circle";   // circle | rect | arrow | badge | label | info | check | checkbox | button | dim
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
    public float Radius = 8;
    public bool? Target;
    public float Opacity = 0.5f;

    public bool IsTargetDefault => Type is "circle" or "rect" or "checkbox";
    public bool IsTarget => Target ?? IsTargetDefault;

    public static ShapeSpec Parse(JsonNode n)
    {
        var t = (J.Str(n, "type") ?? J.Str(n, "shape") ?? "circle").ToLowerInvariant();
        t = t switch
        {
            "frame" or "box" or "rectangle" or "border" or "outline" => "rect",
            "number" or "step" or "num" => "badge",
            "text" or "caption" or "hint" or "tooltip" => "label",
            "darken" or "shade" or "spotlight" => "dim",
            "callout" or "note" or "infotext" or "info_text" or "panel" => "info",
            "tick" or "checkmark" or "done" => "check",
            "toggle" or "cell" => "checkbox",
            "btn" => "button",
            "ring" or "ellipse" => "circle",
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
        if (J.Num(n, "pad") is double pad) s.Pad = (float)pad;
        if (J.Num(n, "radius") is double rad) { if (t == "circle" || t == "badge") s.R = (float)rad; else s.Radius = (float)rad; }
        if (J.Num(n, "opacity") is double op) s.Opacity = (float)Math.Clamp(op, 0, 1);
        var from = J.Get(n, "from");
        if (J.AsPt(from) is PointF fp) s.From = fp;
        else if (J.AsStr(from) is string dir) s.FromDir = dir.ToLowerInvariant();
        if (J.Str(n, "dir") is string d2) s.FromDir = d2.ToLowerInvariant();
        return s;
    }

    private static float? F(double? d) => d is double v ? (float)v : null;
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
    public string? Label;                 // human name for events / checklist
    public string DoneStyle = "hide";     // hide | check (leave a green tick when satisfied)
    public bool? HideInactive;            // hide while the anchor's app is not in the foreground

    // runtime
    public bool Dismissed;
    public int Hits;
    public bool? CheckOk;
    public DateTime CheckAt;
    public MarkGeo? Geo;
    public string Visibility = "resolving";
    public DateTime? ShownAt;

    public bool HasHitArea => Shapes.Any(s => s.IsTarget);
    public bool HasButtons => Shapes.Any(s => s.Type == "button");

    public static Mark Parse(JsonNode n, AnchorSpec? defaults, string id)
    {
        n = J.Unwrap(n)!;
        var m = new Mark
        {
            Id = J.Str(n, "id") ?? id,
            Anchor = AnchorSpec.Parse(J.Get(n, "anchor"), defaults),
            DismissOnClick = J.Bool(n, "dismissOnClick") ?? true,
            Toggle = J.Bool(n, "toggle"),
            Button = (J.Str(n, "button") ?? "left").ToLowerInvariant(),
            Optional = J.Bool(n, "optional") ?? false,
            Label = J.Str(n, "name") ?? J.Str(n, "label") ?? J.Str(n, "text"),
            DoneStyle = (J.Str(n, "doneStyle") ?? "hide").ToLowerInvariant(),
            HideInactive = J.Bool(n, "hideWhenInactive"),
        };
        m.Check = PixelCheck.Parse(J.Get(n, "check"), defaults);
        foreach (var s in J.Items(J.Get(n, "shapes"))) m.Shapes.Add(ShapeSpec.Parse(s));
        if (m.Shapes.Count == 0)
        {
            // sensible default: frame around a UI element, ring around a bare point
            bool hasBounds = m.Anchor.Path.Count > 0 || (m.Anchor.HasBounds && m.Anchor.Dx == 0 && m.Anchor.Dy == 0 && m.Anchor.Origin == "topleft");
            m.Shapes.Add(new ShapeSpec { Type = hasBounds ? "rect" : "circle" });
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
    }
}

internal sealed class Condition
{
    public string Type = "all_clicked";  // all_clicked | appear | disappear | pixel | count
    public AnchorSpec? Anchor;
    public PixelCheck? Pixel;
    public int Count;

    // runtime
    public bool SeenPresent;
    public bool Met;

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
        return c;
    }

    private static string Norm(string t) => t.ToLowerInvariant() switch
    {
        "all_clicked" or "targets" or "clicked" or "all" => "all_clicked",
        "appear" or "appeared" or "window_open" or "window_opened" or "open" or "opened" or "exists" => "appear",
        "disappear" or "disappeared" or "window_closed" or "closed" or "close" or "gone" => "disappear",
        "pixel" or "color" => "pixel",
        "hits" or "count" or "clicks" => "count",
        "manual" or "button" or "none" => "manual",
        var x => x,
    };

    public void Reset() { SeenPresent = false; Met = false; }

    public JsonObject ToJson() => new()
    {
        ["type"] = Type, ["met"] = Met, ["seenPresent"] = SeenPresent,
        ["anchor"] = Anchor?.Describe() ?? Pixel?.Anchor?.Describe(),
    };
}

internal sealed class Step
{
    public string? Id;
    public string Title = "";
    public string Text = "";
    public List<Mark> Targets = new();
    public List<Condition> Until = new();
    public bool UntilAll;                // false: any condition completes the step
    public bool? Toggle;
    public float? Dim;
    public int? AdvanceDelayMs;
    public bool Numbered;
    public bool? Checklist;
    public List<CardButton> Buttons = new();     // extra card buttons → "button" events
    public HashSet<string>? Controls;            // which standard card controls to show

    // runtime
    public readonly Stack<(Mark mark, bool prevDismissed)> History = new();

    public static Step Parse(JsonNode n, AnchorSpec? defaults, int ordinal)
    {
        n = J.Unwrap(n)!;
        var s = new Step
        {
            Id = J.Str(n, "id"),
            Title = J.Str(n, "title") ?? "",
            Text = J.Str(n, "text") ?? J.Str(n, "description") ?? J.Str(n, "body") ?? "",
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
            s.Targets.Add(Mark.Parse(t, stepDefaults, $"s{ordinal}t{i++}"));
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

    public void ResetRuntime()
    {
        foreach (var t in Targets) t.ResetRuntime();
        foreach (var c in Until) c.Reset();
        History.Clear();
    }

    public IEnumerable<Mark> Required => Targets.Where(t => !t.Optional && (t.HasHitArea || t.Check != null || t.HasButtons));
}

internal sealed class Tutorial
{
    public string Title = "";
    public AnchorSpec Defaults = new();
    public List<Step> Steps = new();
    public int Index = -1;
    public string State = "running";     // running | waiting | finished | stopped
    public bool KeepOpen;
    public bool Toggle;
    public float? Dim;
    public int AdvanceDelayMs = 450;
    public bool HideWhenInactive = true;

    public DateTime? AdvanceAt;          // step completed, waiting for the short "✓" moment
    public string? CompletedHow;

    public Step? Current => Index >= 0 && Index < Steps.Count ? Steps[Index] : null;
    public bool Active => State is "running" or "waiting";
}
