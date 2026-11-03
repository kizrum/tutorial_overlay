using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace TutorialOverlay;

/// <summary>
/// The user's tutorial library: specs saved as JSON files in %LOCALAPPDATA%\TutorialOverlay\tutorials.
/// A saved tutorial can be replayed from the tray menu (no agent needed) or by name: start_tutorial({name}).
/// </summary>
internal static class Tutorials
{
    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TutorialOverlay", "tutorials");

    /// <summary>File-safe name from a title: letters and digits of any script, the rest becomes '-'.</summary>
    public static string Slug(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s.Trim().ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '-');
        var slug = string.Join("-", sb.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries));
        slug = slug.Length > 60 ? slug[..60].TrimEnd('-') : slug;
        // names Windows keeps for devices: "con.json" cannot be created
        if (Reserved.Contains(slug)) slug += "-1";
        return slug;
    }

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "con", "prn", "aux", "nul", "conin$", "conout$",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };

    public sealed record SaveResult(string Name, string Path, bool Replaced, List<string> MissingTemplates);

    private static string PathOf(string name) => Path.Combine(Dir, name + ".json");

    /// <summary>
    /// Saves (or overwrites — then Replaced is true) a spec, with the PNGs of the templates it uses inside.
    /// </summary>
    public static SaveResult Save(JsonObject spec, string? name = null)
    {
        var n = Slug(name ?? J.Str(spec, "title") ?? "");
        if (n.Length == 0) n = $"tutorial-{DateTime.Now:yyyyMMdd-HHmmss}";
        var copy = (JsonObject)spec.DeepClone();
        copy.Remove("startAt");          // a saved tutorial always starts from the beginning
        copy["savedAt"] = DateTime.Now.ToString("s");
        var missing = TemplateStore.EmbedInto(copy);
        if (missing.Count > 0) Log.Warn($"tutorial '{n}': template(s) {string.Join(", ", missing)} not found, saved without them");
        var path = PathOf(n);
        bool replaced = File.Exists(path);
        Files.WriteAtomic(path, copy.ToJsonString(J.Pretty));
        return new SaveResult(n, path, replaced, missing);
    }

    /// <summary>All saved tutorials, newest first.</summary>
    public static List<JsonObject> List()
    {
        var res = new List<JsonObject>();
        if (!Directory.Exists(Dir)) return res;
        foreach (var f in Directory.GetFiles(Dir, "*.json"))
        {
            try
            {
                var n = JsonNode.Parse(File.ReadAllText(f));
                res.Add(new JsonObject
                {
                    ["name"] = Path.GetFileNameWithoutExtension(f),
                    ["title"] = J.Str(n, "title") ?? Path.GetFileNameWithoutExtension(f),
                    ["app"] = J.Str(n, "app") ?? J.Str(n, "process") ?? J.Str(J.Get(n, "anchor"), "process"),
                    ["steps"] = J.Items(J.Get(n, "steps")).Count(),
                    ["templates"] = J.Get(n, "templates") is JsonObject tp && tp.Count > 0 ? tp.Count : null,
                    ["savedAt"] = J.Str(n, "savedAt") ?? File.GetLastWriteTime(f).ToString("s"),
                    ["path"] = f,
                });
            }
            catch (Exception e) { Log.Warn($"tutorial file skipped ({Path.GetFileName(f)}): {e.Message}"); }
        }
        return res.OrderByDescending(o => J.Str(o, "savedAt")).ToList();
    }

    /// <summary>A saved spec by name (file name), or by title when no file has that name.</summary>
    public static JsonObject Load(string name)
    {
        var path = PathOf(Slug(name));
        if (!File.Exists(path))
        {
            var byTitle = List().FirstOrDefault(o => string.Equals(J.Str(o, "title"), name, StringComparison.OrdinalIgnoreCase));
            if (byTitle == null) throw new FileNotFoundException($"no saved tutorial '{name}' (list_tutorials shows what there is)");
            path = J.Str(byTitle, "path")!;
        }
        return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidDataException($"{path} is not a tutorial spec");
    }

    public static bool Delete(string name)
    {
        var path = PathOf(Slug(name));
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }
}
