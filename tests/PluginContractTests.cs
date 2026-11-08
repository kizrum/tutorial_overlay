using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace TutorialOverlay.Tests;

/// <summary>
/// The plugin (plugin/) and the program must speak the same language. The skill tells the agent which tools to call
/// with which arguments and which fields, events and answers to expect; the Stop hook reads lesson.json. A tool,
/// argument or field renamed in the program but not in the plugin builds and passes every other test, and breaks
/// only in a real lesson. These tests compare the plugin's words with what the program declares.
/// </summary>
public static class PluginContractTests
{
    /// <summary>
    /// Words the skill may name although this program no longer has them: what it tells agents to do with an older
    /// TutorialOverlay. Each with the reason. Empty while every fallback the skill describes still exists.
    /// </summary>
    private static readonly Dictionary<string, string> Legacy = new(StringComparer.Ordinal);

    private static string Root()
    {
        for (int up = 0; up < 8; up++)
        {
            var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, string.Concat(Enumerable.Repeat("../", up))));
            if (File.Exists(Path.Combine(dir, "TutorialOverlay.csproj")) && Directory.Exists(Path.Combine(dir, "plugin"))) return dir;
        }
        throw new Exception("repository root (TutorialOverlay.csproj + plugin/) not found above " + AppContext.BaseDirectory);
    }

    private static string Skill() => File.ReadAllText(Path.Combine(Root(), "plugin", "skills", "tutorial-overlay", "SKILL.md"));

    /// <summary>Tool name → its argument names, from the tools/list the server answers.</summary>
    private static Dictionary<string, HashSet<string>> ToolArgs() =>
        Tools.List().Select(t => t!.AsObject()).ToDictionary(
            t => (string)t["name"]!,
            t => (t["inputSchema"]?["properties"]?.AsObject().Select(p => p.Key) ?? Enumerable.Empty<string>()).ToHashSet(StringComparer.Ordinal));

    /// <summary>SpecLint's field sets by name (Anchor, Find, Step, CondTypes, …): the fields the spec parser reads.</summary>
    private static Dictionary<string, HashSet<string>> SpecSets() =>
        typeof(SpecLint).GetFields(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(HashSet<string>))
            .ToDictionary(f => f.Name, f => (HashSet<string>)f.GetValue(null)!);

    /// <summary>
    /// Every word the program knows: tool and argument names, spec fields, and every string literal in its source
    /// (event names, event and answer fields such as "heard" or "volatile").
    /// </summary>
    private static HashSet<string> Vocabulary()
    {
        var v = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (tool, args) in ToolArgs()) { v.Add(tool); v.UnionWith(args); }
        foreach (var set in SpecSets().Values) v.UnionWith(set);
        foreach (var f in Directory.GetFiles(Path.Combine(Root(), "src"), "*.cs"))
            foreach (Match m in Regex.Matches(File.ReadAllText(f), "\"([A-Za-z_][A-Za-z0-9_]*)\""))
                v.Add(m.Groups[1].Value);
        return v;
    }

    /// <summary>The `code` spans of a Markdown text.</summary>
    private static IEnumerable<string> CodeSpans(string md) =>
        Regex.Matches(md, "`([^`\n]+)`").Select(m => m.Groups[1].Value.Trim());

    /// <summary>Top-level argument names of "a, b: [1, 2], c: {x: 1}" → a, b, c.</summary>
    private static IEnumerable<string> ArgNames(string args)
    {
        int depth = 0, start = 0;
        var parts = new List<string>();
        for (int i = 0; i <= args.Length; i++)
        {
            char c = i < args.Length ? args[i] : ',';
            if (c is '[' or '{' or '(') depth++;
            else if (c is ']' or '}' or ')') depth--;
            else if (c == ',' && depth == 0) { parts.Add(args[start..i]); start = i + 1; }
        }
        foreach (var p in parts)
        {
            var m = Regex.Match(p, @"^\s*([A-Za-z_]\w*)\s*(:|$)");
            if (m.Success) yield return m.Groups[1].Value;
        }
    }

    /// <summary>`name(args)` in the skill: a tool the server has, with arguments it takes.</summary>
    public static void TestSkillCallsRealTools()
    {
        var tools = ToolArgs();
        var bad = new List<string>();
        int calls = 0;
        foreach (var span in CodeSpans(Skill()))
            foreach (Match m in Regex.Matches(span, @"\b([a-z][a-z_]*)\(([^()]*)\)"))
            {
                var name = m.Groups[1].Value;
                calls++;
                if (Legacy.ContainsKey(name)) continue;
                if (!tools.TryGetValue(name, out var args)) { bad.Add($"`{span}`: no tool \"{name}\""); continue; }
                foreach (var a in ArgNames(m.Groups[2].Value))
                    if (!args.Contains(a) && !Legacy.ContainsKey(a))
                        bad.Add($"`{span}`: {name} has no argument \"{a}\" (it takes {string.Join(", ", args.OrderBy(x => x))})");
            }
        A.Check(calls >= 5, $"only {calls} tool calls found in SKILL.md: the span parser no longer matches its style");
        A.Check(bad.Count == 0, string.Join("\n    ", bad.Prepend("")));
    }

    /// <summary>
    /// Every name in a `code` span of the skill — a tool, a spec field, an event, a field of an answer — is a word the
    /// program uses. `find.X` must be a field of find, and `type: 'x'` a condition or shape type.
    /// </summary>
    public static void TestSkillNamesWhatTheProgramHas()
    {
        var vocab = Vocabulary();
        var sets = SpecSets();
        var types = sets["CondTypes"].Concat(sets["ShapeTypes"]).Append("auto").ToHashSet(StringComparer.Ordinal);
        var bad = new List<string>();
        int names = 0;
        void Need(string word, string span, ISet<string> known, string what)
        {
            names++;
            if (!known.Contains(word) && !Legacy.ContainsKey(word)) bad.Add($"`{span}`: \"{word}\" is not {what}");
        }
        foreach (var span in CodeSpans(Skill()))
        {
            if (span.Contains(' ') && !span.Contains(':') && !span.Contains('(')) continue;   // a command line, a caption
            // drop quoted values ('General settings', "main") but keep the type values checked below
            foreach (Match m in Regex.Matches(span, @"\btype\s*:\s*'([^']*)'"))
                Need(m.Groups[1].Value, span, types, "a condition or shape type");
            foreach (Match m in Regex.Matches(span, @"\bfind\.([A-Za-z]+)"))
                Need(m.Groups[1].Value, span, sets["Find"], "a field of find");
            var code = Regex.Replace(span, @"'[^']*'|""[^""]*""", "''");
            // a hyphenated name (`tutorial-overlay`) is the product's, not a word of the protocol
            foreach (Match m in Regex.Matches(code, @"(?<![\w.-])([A-Za-z_][A-Za-z0-9_]*)(?![\w-])"))
            {
                var w = m.Groups[1].Value;
                if (w is "true" or "false" or "null") continue;
                if (char.IsUpper(w[0]) && !w.Contains('_')) continue;   // a UI caption ("Shade Smooth"), not a name
                Need(w, span, vocab, "a tool, argument, spec field, event or answer field of the program");
            }
        }
        A.Check(names >= 30, $"only {names} names found in SKILL.md: the span parser no longer matches its style");
        A.Check(bad.Count == 0, string.Join("\n    ", bad.Prepend("")));
    }

    /// <summary>
    /// The Stop hook decides from lesson.json and the transcript: the fields and states it reads are the ones the
    /// program writes, and the tools it looks for in the transcript exist.
    /// </summary>
    public static void TestStopHookReadsWhatTheProgramWrites()
    {
        var root = Root();
        var hook = File.ReadAllText(Path.Combine(root, "plugin", "hooks", "stop-guard.ps1"));
        var bad = new List<string>();

        // the fields Engine.LessonInfo() sets: ["x"] = … in its body (the method up to the next one)
        var body = Regex.Match(File.ReadAllText(Path.Combine(root, "src", "Engine.cs")),
            @"JsonObject LessonInfo\(\)(.*?)\n    (?:public|private|internal)\b", RegexOptions.Singleline);
        A.Check(body.Success, "Engine.LessonInfo() not found in src/Engine.cs");
        var written = Regex.Matches(body.Groups[1].Value, @"\[""(\w+)""\]\s*=")
            .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        A.Check(written.Contains("state") && written.Contains("pid"), "LessonInfo's fields (pid, state, …) not found in its body");
        var read = Regex.Matches(hook, @"\$lesson\.(\w+)").Select(m => m.Groups[1].Value).Distinct().ToList();
        A.Check(read.Count >= 5, $"only {read.Count} lesson.json fields found in the hook");
        bad.AddRange(read.Where(f => !written.Contains(f)).Select(f => $"the hook reads lesson.json \"{f}\", which the program does not write"));

        var states = Enum.GetNames(typeof(TutorialState)).Select(s => s.ToLowerInvariant()).Append("outro").Append("none").ToHashSet();
        foreach (Match m in Regex.Matches(hook, @"\$lesson\.state\s+-(?:eq|ne)\s+'(\w+)'"))
            if (!states.Contains(m.Groups[1].Value)) bad.Add($"the hook compares the state with '{m.Groups[1].Value}', which is never written");
        foreach (Match m in Regex.Matches(hook, @"\$lesson\.agent\s+-(?:eq|ne)\s+'(\w+)'"))
            if (m.Groups[1].Value is not ("gone" or "connected")) bad.Add($"the hook compares the agent with '{m.Groups[1].Value}', which is never written");

        var tools = ToolArgs();
        var group = Regex.Match(hook, @"tutorial-overlay__\(([a-z_|]+)\)");
        A.Check(group.Success, "the hook's transcript pattern (tutorial-overlay__(a|b|…)) not found");
        foreach (var t in group.Groups[1].Value.Split('|'))
            if (!tools.ContainsKey(t)) bad.Add($"the hook looks for tool \"{t}\" in the transcript, which the server does not have");

        A.Check(bad.Count == 0, string.Join("\n    ", bad.Prepend("")));
    }
}
