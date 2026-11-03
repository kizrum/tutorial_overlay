using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace TutorialOverlay;

/// <summary>
/// Visual anchors (find:{template}) outlive the process: every captured template is kept as a PNG in
/// %LOCALAPPDATA%\TutorialOverlay\templates and loaded on first use, and a saved tutorial carries the templates it
/// uses inside its own file ("templates": {id: base64 PNG}) — so it replays after a restart, from the tray menu
/// and on another computer.
/// </summary>
internal static class TemplateStore
{
    public static string Dir => Path.Combine(Files.AppDir, "templates");

    /// <summary>Exact id → file name: readable part plus a hash (ids differing only in case or punctuation stay apart).</summary>
    public static string FileOf(string id)
    {
        var slug = Tutorials.Slug(id);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..8].ToLowerInvariant();
        return Path.Combine(Dir, (slug.Length > 0 ? slug + "-" : "") + hash + ".png");
    }

    /// <summary>A template by id: memory first, then the disk.</summary>
    public static bool TryGet(string id, out Template? tpl)
    {
        if (Finder.Templates.TryGetValue(id, out tpl)) return true;
        var path = FileOf(id);
        if (!File.Exists(path)) { tpl = null; return false; }
        try
        {
            tpl = FromPng(File.ReadAllBytes(path));
            Finder.Templates[id] = tpl;
            return true;
        }
        catch (Exception e)
        {
            Log.Warn($"template '{id}' unreadable ({path}): {e.Message}");
            tpl = null;
            return false;
        }
    }

    public static bool Exists(string id) => TryGet(id, out _);

    /// <summary>Remembers a template in memory and on disk.</summary>
    public static void Put(string id, Template tpl, byte[] png)
    {
        Finder.Templates[id] = tpl;
        try { Files.WriteAtomic(FileOf(id), png); }
        catch (Exception e) { Log.Warn($"template '{id}' not written to disk: {e.Message}"); }
    }

    public static Template FromPng(byte[] png)
    {
        using var ms = new MemoryStream(png);
        using var bmp = new Bitmap(ms);
        return Template.From(bmp);
    }

    /// <summary>Template ids a spec refers to (find: "id" | {template} | {id} without text).</summary>
    public static HashSet<string> UsedBy(JsonNode? spec)
    {
        var ids = new HashSet<string>();
        void Walk(JsonNode? n)
        {
            switch (n)
            {
                case JsonObject o:
                    foreach (var (k, v) in o)
                    {
                        if (k == "templates") continue;
                        if (k == "find")
                        {
                            var f = J.Unwrap(v);
                            if (f is JsonValue && J.AsStr(f) is string s && s.Length > 0) ids.Add(s);
                            else if (f is JsonObject fo && J.Str(fo, "text") == null && (J.Str(fo, "template") ?? J.Str(fo, "id")) is string t && t.Length > 0) ids.Add(t);
                        }
                        Walk(v);
                    }
                    break;
                case JsonArray a:
                    foreach (var x in a) Walk(x);
                    break;
            }
        }
        Walk(spec);
        return ids;
    }

    /// <summary>Puts the PNGs of the templates the spec uses into spec.templates. Returns ids that could not be found.</summary>
    public static List<string> EmbedInto(JsonObject spec)
    {
        var missing = new List<string>();
        var embedded = J.Get(spec, "templates") as JsonObject;
        var res = new JsonObject();
        foreach (var id in UsedBy(spec).OrderBy(x => x, StringComparer.Ordinal))
        {
            var path = FileOf(id);
            if (File.Exists(path)) res[id] = Convert.ToBase64String(File.ReadAllBytes(path));
            else if (J.Str(embedded, id) is string keep) res[id] = keep;
            else missing.Add(id);
        }
        if (res.Count > 0) spec["templates"] = res; else spec.Remove("templates");
        return missing;
    }

    /// <summary>Registers templates carried by a spec ("templates": {id: base64 PNG}). Returns how many.</summary>
    public static int LoadFrom(JsonNode? spec)
    {
        if (J.Get(spec, "templates") is not JsonObject o) return 0;
        int n = 0;
        foreach (var (id, v) in o)
        {
            if (J.AsStr(v) is not string b64) continue;
            try
            {
                var png = Convert.FromBase64String(b64);
                Put(id, FromPng(png), png);
                n++;
            }
            catch (Exception e) { Log.Warn($"template '{id}' in the spec is not a PNG: {e.Message}"); }
        }
        return n;
    }
}
