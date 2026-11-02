using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TutorialOverlay;

/// <summary>Tolerant accessors for loosely-typed JSON coming from the agent.</summary>
internal static class J
{
    // relaxed escaping: Cyrillic titles stay readable in events and tool results
    public static readonly JsonSerializerOptions Compact = new() { WriteIndented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static JsonNode? Get(JsonNode? n, string key) => n is JsonObject o && o.TryGetPropertyValue(key, out var v) ? v : null;

    public static bool Has(JsonNode? n, string key) => Get(n, key) != null;

    public static string? Str(JsonNode? n, string key) => AsStr(Get(n, key));

    public static string? AsStr(JsonNode? v)
    {
        if (v is not JsonValue jv) return null;
        if (jv.TryGetValue<string>(out var s)) return s;
        if (jv.TryGetValue<JsonElement>(out var e))
            return e.ValueKind switch
            {
                JsonValueKind.String => e.GetString(),
                JsonValueKind.Number => e.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };
        return jv.ToJsonString().Trim('"');
    }

    public static double? Num(JsonNode? n, string key) => AsNum(Get(n, key));

    public static double? AsNum(JsonNode? v)
    {
        if (v is not JsonValue jv) return null;
        if (jv.TryGetValue<JsonElement>(out var e))
        {
            if (e.ValueKind == JsonValueKind.Number) return e.GetDouble();
            if (e.ValueKind == JsonValueKind.String && double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d1)) return d1;
            return null;
        }
        if (jv.TryGetValue<double>(out var d)) return d;
        if (jv.TryGetValue<int>(out var i)) return i;
        if (jv.TryGetValue<long>(out var l)) return l;
        if (jv.TryGetValue<float>(out var f)) return f;
        if (jv.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
        return null;
    }

    public static int? Int(JsonNode? n, string key) => Num(n, key) is double d ? (int)Math.Round(d) : null;

    public static bool? Bool(JsonNode? n, string key)
    {
        var v = Get(n, key);
        if (v is not JsonValue jv) return null;
        if (jv.TryGetValue<JsonElement>(out var e))
            return e.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => e.GetString()?.ToLowerInvariant() is "true" or "1" or "yes",
                JsonValueKind.Number => e.GetDouble() != 0,
                _ => null
            };
        if (jv.TryGetValue<bool>(out var b)) return b;
        return null;
    }

    public static JsonArray? Arr(JsonNode? n, string key) => Get(n, key) as JsonArray;

    /// <summary>[x,y] or {"x":..,"y":..}.</summary>
    public static PointF? Pt(JsonNode? n, string key) => AsPt(Get(n, key));

    public static PointF? AsPt(JsonNode? v)
    {
        if (v is JsonArray a && a.Count >= 2 && AsNum(a[0]) is double x && AsNum(a[1]) is double y) return new PointF((float)x, (float)y);
        if (v is JsonObject o && Num(o, "x") is double ox && Num(o, "y") is double oy) return new PointF((float)ox, (float)oy);
        return null;
    }

    /// <summary>Accepts an object, an array, or a JSON string containing either (agents sometimes double-encode).</summary>
    public static JsonNode? Unwrap(JsonNode? v)
    {
        if (v is JsonValue jv && AsStr(jv) is string s && (s.TrimStart().StartsWith('{') || s.TrimStart().StartsWith('[')))
        {
            try { return JsonNode.Parse(s); } catch { }
        }
        return v;
    }

    public static IEnumerable<JsonNode> Items(JsonNode? v)
    {
        v = Unwrap(v);
        if (v is JsonArray a) { foreach (var x in a) if (x != null) yield return x; }
        else if (v != null) yield return v;
    }

    public static JsonArray RectJson(RectangleF r) => new(Round(r.X), Round(r.Y), Round(r.Width), Round(r.Height));
    public static JsonArray PtJson(PointF p) => new(Round(p.X), Round(p.Y));
    public static int Round(float f) => (int)Math.Round(f);

    public static Color ParseColor(string? s, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(s)) return fallback;
        s = s.Trim();
        try
        {
            if (s.StartsWith('#'))
            {
                var hex = s[1..];
                if (hex.Length == 3) hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
                uint v = uint.Parse(hex, NumberStyles.HexNumber);
                if (hex.Length == 6) return Color.FromArgb(255, (int)(v >> 16) & 255, (int)(v >> 8) & 255, (int)v & 255);
                if (hex.Length == 8) return Color.FromArgb((int)(v >> 24) & 255, (int)(v >> 16) & 255, (int)(v >> 8) & 255, (int)v & 255);
            }
            if (Palette.Named.TryGetValue(s.ToLowerInvariant(), out var named)) return named;
            var c = Color.FromName(s);
            if (c.IsKnownColor) return c;
        }
        catch { }
        return fallback;
    }
}
