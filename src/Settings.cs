using System;
using System.Text;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace TutorialOverlay;

/// <summary>A key chord such as Ctrl+Alt+Right.</summary>
internal readonly record struct Chord(uint Mods, uint Vk)
{
    public override string ToString() => Keys.Format(this);
}

/// <summary>Key names ⇄ virtual-key codes. Used for card hotkeys and for "key" step conditions.</summary>
internal static class Keys
{
    private static readonly Dictionary<string, uint> Named = BuildNames();

    private static Dictionary<string, uint> BuildNames()
    {
        var d = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            ["left"] = 0x25, ["up"] = 0x26, ["right"] = 0x27, ["down"] = 0x28,
            ["enter"] = 0x0D, ["return"] = 0x0D, ["space"] = 0x20, ["tab"] = 0x09, ["esc"] = 0x1B, ["escape"] = 0x1B,
            ["backspace"] = 0x08, ["delete"] = 0x2E, ["del"] = 0x2E, ["insert"] = 0x2D, ["ins"] = 0x2D,
            ["home"] = 0x24, ["end"] = 0x23, ["pageup"] = 0x21, ["pgup"] = 0x21, ["pagedown"] = 0x22, ["pgdn"] = 0x22,
            ["plus"] = 0xBB, ["minus"] = 0xBD, ["comma"] = 0xBC, ["period"] = 0xBE, ["slash"] = 0xBF,
            ["numpad+"] = 0x6B, ["numpad-"] = 0x6D, ["numpad*"] = 0x6A, ["numpad/"] = 0x6F,
            ["capslock"] = 0x14, ["printscreen"] = 0x2C, ["pause"] = 0x13,
            ["→"] = 0x27, ["←"] = 0x25, ["↑"] = 0x26, ["↓"] = 0x28,
        };
        for (int i = 1; i <= 24; i++) d["f" + i] = (uint)(0x70 + i - 1);
        for (int i = 0; i <= 9; i++) d["numpad" + i] = (uint)(0x60 + i);
        return d;
    }

    private static readonly Dictionary<uint, string> Display = new()
    {
        [0x25] = "←", [0x26] = "↑", [0x27] = "→", [0x28] = "↓", [0x0D] = "Enter", [0x20] = "Space", [0x09] = "Tab",
        [0x1B] = "Esc", [0x08] = "Backspace", [0x2E] = "Del", [0x2D] = "Ins", [0x24] = "Home", [0x23] = "End",
        [0x21] = "PgUp", [0x22] = "PgDn", [0xBB] = "+", [0xBD] = "-",
    };

    public static Chord? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        uint mods = 0, vk = 0;
        // "Ctrl++" style: a trailing '+' is the key itself
        var parts = s.Trim().EndsWith("++") ? s.Trim()[..^2].Split('+').Append("plus").ToArray() : s.Split('+');
        foreach (var raw in parts)
        {
            var p = raw.Trim();
            if (p.Length == 0) continue;
            switch (p.ToLowerInvariant())
            {
                case "ctrl" or "control" or "ctl": mods |= Native.MOD_CONTROL; break;
                case "alt": mods |= Native.MOD_ALT; break;
                case "shift": mods |= Native.MOD_SHIFT; break;
                case "win" or "meta" or "cmd": mods |= Native.MOD_WIN; break;
                default:
                    if (Named.TryGetValue(p, out var v)) vk = v;
                    else if (p.Length == 1 && char.IsLetterOrDigit(p[0])) vk = char.ToUpperInvariant(p[0]);
                    else return null;
                    break;
            }
        }
        return vk == 0 ? null : new Chord(mods, vk);
    }

    public static string Format(Chord c)
    {
        var mods = FormatMods(c.Mods);
        return mods.Length > 0 ? mods + "+" + KeyName(c.Vk) : KeyName(c.Vk);
    }

    public static string FormatMods(uint mods)
    {
        var parts = new List<string>();
        if ((mods & Native.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((mods & Native.MOD_ALT) != 0) parts.Add("Alt");
        if ((mods & Native.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((mods & Native.MOD_WIN) != 0) parts.Add("Win");
        return string.Join("+", parts);
    }

    public static string KeyName(uint vk)
    {
        if (Display.TryGetValue(vk, out var d)) return d;
        if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x70 + 1);
        if ((vk >= 'A' && vk <= 'Z') || (vk >= '0' && vk <= '9')) return ((char)vk).ToString();
        return $"0x{vk:X2}";
    }
}

/// <summary>
/// TutorialOverlay's own words (the card, the tray, the settings) in one language. They live in language files, not
/// in the code: <c>src/lang/&lt;code&gt;.json</c> built into the exe, and the user's own in
/// <c>%LOCALAPPDATA%\TutorialOverlay\lang\&lt;code&gt;.json</c> (a new language, or changes to a built-in one). Each
/// field is filled from English first, then from the language's file, so a key a file lacks shows in English.
/// </summary>
internal sealed class Loc
{
    public string Step = "", Waiting = "", Tutorial = "", Preparing = "";
    public string Back = "", Skip = "", Done = "", Got = "", Undo = "", Help = "";
    public string Marked = "", Target = "";
    public string HintDone = "", HintBack = "", HintSkip = "", HintUndo = "", HintStop = "";
    public string PickTitle = "", PickText = "", PickCount = "";
    public string Review = "";
    public string Why = "";
    public string HeadStep = "", HeadOf = "", HeadWaiting = "", HeadOutro = "";
    public string ScrollUp = "", ScrollDown = "", ScrollLeft = "", ScrollRight = "";
    public string ScrollIssue = "", ScrollFind = "",
        ScrollHere = "", ScrollHereAny = "",
        ScrollOver = "", ScrollOverAny = "";
    public string IssueDialog = "";
    public string PrepareTitle = "", DialogTitle = "",
        PrepareText = "";
    public string DialogMark = "";
    public string DialogCloseMark = "";
    public string PrepareTextCover = "",
        CoverText = "",
        IssueCover = "";
    public string CantFind = "", DontGet = "", Cancel = "", HintHelp = "";
    public string Other = "", OtherTitle = "", Send = "",
        OtherHint = "";
    public string Sketch = "", Sketched = "", SketchTitle = "",
        SketchFrozen = "", SketchPen = "", SketchCircle = "", SketchRect = "",
        SketchUndo = "", SketchCancel = "", SketchCancelTip = "",
        SketchDoneTip = "", SketchMove = "";
    public string ChatHello = "", ChatAgent = "",
        ChatHint = "", ChatAsk = "", ChatReply = "",
        ChatRow = "", ChatLooking = "", ChatSketchOnly = "",
        ChatShapes = "", ChatBack = "", ChatRemoveSketch = "",
        ReplyElsewhere = "";

    public string OutroHead = "", OutroTitle = "", OutroText = "",
        OutroSave = "", OutroRepeat = "", OutroContinue = "", OutroClose = "", WaitFinish = "",
        OutroSavedTitle = "", OutroSavedText = "",
        OutroChangedTitle = "", OutroChangedText = "",
        OutroOverwriteTitle = "", OutroOverwriteText = "",
        OutroOverwrite = "", OutroKeep = "",
        TrayTutorials = "", TrayTutorialsEmpty = "", TrayTutorialsFolder = "";
    public string HoldKeys = "";
    public string AskTitle = "", AskHint = "";

    // tray icon
    public string TrayRunning = "", TrayAgentNever = "", TrayAgentAgo = "",
        TrayTutorial = "", TrayNoTutorial = "",
        TraySettings = "", TrayCopyKey = "", TrayCopyUrl = "",
        TrayCopyCommand = "",
        AlreadyRunning = "",
        TrayOpenFolder = "", TrayStopTutorial = "", TrayExit = "",
        TrayExitConfirm = "", Sec = "", Min = "", Hour = "";

    // settings window
    public string SetTitle = "", SetTabServer = "", SetTabLook = "", SetTabKeys = "",
        SetPort = "", SetPortArg = "",
        SetPortNote = "",
        SetPortFree = "", SetPortBusy = "", SetPortBusyUnknown = "", SetPortHere = "",
        SetPortBad = "", SetPortPick = "", SetPortNone = "",
        SetForAgent = "", SetForAgentBtn = "", Copied = "",
        SetForAgentNote = "",
        RestartBusy = "",
        SetUrl = "", SetKey = "", SetKeySource = "", Show = "", Hide = "", Copy = "",
        SetLang = "", SetLangOwn = "", SetAccent = "", SetPickColor = "", SetReset = "",
        SetAutoColor = "", SetAnimate = "", SetMascot = "",
        SetHideInactive = "",
        SetHideCapture = "",
        SetAutoSpeed = "", SetSpeedSlow = "", SetSpeedNormal = "", SetSpeedFast = "",
        SetCursorBadge = "",
        SetAutoSpeedNote = "",
        SetKeysNote = "",
        KeyStop = "", KeyNone = "", SetSave = "", SetDefaults = "",
        SetRestartAsk = "",
        SetAfterTutorial = "", SetSaveFailed = "";
    public string HelpTitle = "", HelpVisible = "",
        HelpHidden = "", HelpAgentToo = "",
        HelpAgentIdle = "";
    public string ContinueUnheard = "";
    public string AgentOn = "",
        AgentOff = "",
        AgentOffFor = "",
        AgentOffContinue = "";
    public string HelpSent = "", Highlighting = "", Checking = "", LaterPreparing = "";
    public string IssueMinimized = "", IssueOccluded = "",
        IssueInactive = "", IssueMissing = "",
        IssueClosed = "",
        IssueClosedIn = "",
        IssueOpenPrevious = "",
        ReopenTitle = "", ReopenText = "",
        IssueOffscreen = "", IssueNotRunning = "",
        IssueNotAnswering = "",
        IssueElevated = "";

    public string ForbidTag = "", OffscreenTag = "";

    // autoplay ("Show me") and what cannot be undone
    public string ShowMe = "", ShowStep = "", ShowAll = "", AutoStop = "",
        AutoResume = "", KeyAutoplay = "", HintAuto = "",
        HeadAuto = "", CursorAuto = "",
        AutoIntroTitle = "", AutoIntro = "",
        AutoIntroGo = "",
        AutoStarting = "",
        AutoActing = "",
        AutoActingAgent = "",
        AutoStepDone = "",
        AutoLooking = "",
        AutoAskedAgent = "",
        AutoFinished = "",
        AutoPausedTitle = "", AutoRepeatSelf = "",
        MascotPress = "", MascotHandBack = "",
        DangerTag = "", DangerCard = "",
        DangerPayment = "", DangerIrreversible = "", DangerSend = "", DangerUser = "";
    public Dictionary<string, string> AutoReasons = new();
    /// <summary>The language's code ("es", "zh-Hans") and its own name ("Español"), for the settings' list.</summary>
    public string Code = "en", Name = "English";

    private static readonly Dictionary<string, Loc> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Reflection.Assembly Asm = typeof(Loc).Assembly;

    /// <summary>The user's own language files: a new language, or changes to a built-in one.</summary>
    public static string UserDir => Path.Combine(Files.AppDir, "lang");

    public static Loc En => For("en");
    public static Loc Ru => For("ru");

    /// <summary>The words for a language code (any form: "es", "es-MX", "zh", "zh-CN", "zh-TW"); English if there are none.</summary>
    public static Loc For(string? lang)
    {
        string code = Resolve(lang);
        lock (Cache)
        {
            if (!Cache.TryGetValue(code, out var l)) Cache[code] = l = Load(code);
            return l;
        }
    }

    /// <summary>Forget what was loaded: the user's language files are read again.</summary>
    public static void Reload() { lock (Cache) Cache.Clear(); }

    /// <summary>Every language there are words for — built in and the user's own — as (code, its own name), English first.</summary>
    public static List<(string Code, string Name)> Available()
    {
        var codes = new SortedSet<string>(BuiltinCodes(), StringComparer.OrdinalIgnoreCase);
        foreach (var c in UserCodes()) codes.Add(c);
        return codes.Select(c => (c, For(c).Name)).OrderBy(x => x.c == "en" ? 0 : 1).ThenBy(x => x.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The code there are words for that best fits <paramref name="lang"/>: the same code, else the same language
    /// ("es-MX" → "es"; "zh-CN", "zh-SG", "zh" → "zh-Hans"; "zh-TW", "zh-HK", "zh-MO" → "zh-Hant"), else "en".
    /// </summary>
    public static string Resolve(string? lang)
    {
        if (string.IsNullOrWhiteSpace(lang)) return "en";
        lang = lang.Trim().Replace('_', '-');
        var all = BuiltinCodes().Concat(UserCodes()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string? Pick(string c) => all.FirstOrDefault(x => x.Equals(c, StringComparison.OrdinalIgnoreCase));
        if (Pick(lang) is string exact) return exact;
        var parts = lang.Split('-');
        string head = parts[0].ToLowerInvariant();
        if (head == "zh")
        {
            bool trad = parts.Skip(1).Any(x => x.Equals("Hant", StringComparison.OrdinalIgnoreCase) || x.ToUpperInvariant() is "TW" or "HK" or "MO");
            if (Pick(trad ? "zh-Hant" : "zh-Hans") is string z) return z;
        }
        if (Pick(head) is string same) return same;
        return all.FirstOrDefault(x => x.Split('-')[0].Equals(head, StringComparison.OrdinalIgnoreCase)) ?? "en";
    }

    /// <summary>The language of Windows' own interface, if there are words for it; else English.</summary>
    public static string SystemLang() => Resolve(System.Globalization.CultureInfo.CurrentUICulture.Name);

    private static IEnumerable<string> BuiltinCodes() =>
        Asm.GetManifestResourceNames().Where(n => n.StartsWith("lang.") && n.EndsWith(".json")).Select(n => n[5..^5]);

    private static IEnumerable<string> UserCodes()
    {
        try { return Directory.Exists(UserDir) ? Directory.GetFiles(UserDir, "*.json").Select(f => Path.GetFileNameWithoutExtension(f)!).Where(c => !c.StartsWith('_')).ToList() : new List<string>(); }
        catch { return new List<string>(); }
    }

    private static JsonNode? Builtin(string code)
    {
        using var st = Asm.GetManifestResourceStream("lang." + code + ".json");
        if (st == null) return null;
        using var r = new StreamReader(st);
        return JsonNode.Parse(r.ReadToEnd());
    }

    private static JsonNode? UserFile(string code)
    {
        try
        {
            var f = Path.Combine(UserDir, code + ".json");
            return File.Exists(f) ? JsonNode.Parse(File.ReadAllText(f)) : null;
        }
        catch (Exception e) { Log.Warn($"language file {code}.json: {e.Message}"); return null; }
    }

    private static Loc Load(string code)
    {
        var l = new Loc { Code = code };
        Apply(l, Builtin("en"));
        if (!code.Equals("en", StringComparison.OrdinalIgnoreCase)) Apply(l, Builtin(code));
        Apply(l, UserFile(code));
        return l;
    }

    /// <summary>Each field from the key of its name; "_name" is the language's own name. Unknown keys are left alone.</summary>
    internal static void Apply(Loc l, JsonNode? n)
    {
        if (n is not JsonObject o) return;
        if (J.Str(o, "_name") is string name) l.Name = name;
        foreach (var f in typeof(Loc).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (f.Name is nameof(Code) or nameof(Name) || o[f.Name] is not JsonNode v) continue;
            if (f.FieldType == typeof(string) && v is JsonValue sv && sv.TryGetValue<string>(out var str))
                f.SetValue(l, str);
            else if (f.FieldType == typeof(Dictionary<string, string>) && v is JsonObject d && f.GetValue(l) is Dictionary<string, string> dict)
                foreach (var (k, x) in d)
                    if (x is JsonValue xv && xv.TryGetValue<string>(out var xs)) dict[k] = xs;
        }
    }

    /// <summary>
    /// The language a lesson is written in, from the words of its title and steps, or null when there are too few
    /// letters to tell. The script tells most: Cyrillic "ru", Chinese characters "zh-Hans", kana "ja", Hangul "ko".
    /// Latin letters are told apart by the most common short words (Spanish, German, French, Portuguese, else English).
    /// A spec without "lang" gets its card in that language, so its steps do not sit between buttons in another one.
    /// A lesson names items in other scripts ("Mesh" in a Russian one, «Файл» quoted in an English one), hence a
    /// share, not any letter.
    /// </summary>
    public static string? LangOf(JsonNode? spec)
    {
        int cyr = 0, lat = 0, han = 0, kana = 0, hangul = 0;
        var text = new StringBuilder();
        void Count(string? s)
        {
            if (s == null) return;
            text.Append(' ').Append(s);
            foreach (var ch in s)
                if (ch is >= 'А' and <= 'я' or 'Ё' or 'ё') cyr++;
                else if (ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z' || ch is >= 'À' and <= 'ž') lat++;
                else if (ch is >= '\u3040' and <= '\u30FF') kana++;
                else if (ch is >= '\uAC00' and <= '\uD7AF') hangul++;
                else if (ch is >= '\u4E00' and <= '\u9FFF') han++;
        }
        Count(J.Str(spec, "title"));
        foreach (var s in J.Items(J.Get(spec, "steps")))
            foreach (var k in new[] { "title", "say", "why", "text" }) Count(J.Str(s, k));
        // a Chinese or Japanese character says as much as a word: weigh it like one
        int all = cyr + lat + 3 * (han + kana + hangul);
        if (all < 12) return null;
        if (kana * 3 >= all * 0.1) return "ja";
        if (hangul * 3 >= all * 0.3) return "ko";
        if ((han + kana) * 3 >= all * 0.3) return "zh-Hans";
        if (cyr >= (cyr + lat) * 0.3) return "ru";
        return LatinLang(text.ToString());
    }

    // the short words that give a Latin-script language away
    private static readonly (string code, string[] words)[] Common =
    {
        ("en", new[] { "the", "and", "to", "of", "click", "on", "is", "it", "you", "your", "then", "this", "with", "open", "press" }),
        ("es", new[] { "el", "la", "los", "las", "del", "que", "y", "en", "para", "con", "una", "un", "haga", "pulse", "abra", "es" }),
        ("de", new[] { "der", "die", "das", "und", "mit", "auf", "sie", "ist", "nicht", "ein", "eine", "im", "zu", "klicken", "öffnen" }),
        ("fr", new[] { "le", "les", "des", "et", "du", "une", "est", "pour", "dans", "cliquez", "ouvrez", "vous", "sur" }),
        ("pt", new[] { "o", "os", "as", "do", "da", "que", "e", "em", "para", "com", "uma", "um", "clique", "abra", "não" }),
    };

    private static string LatinLang(string text)
    {
        var words = new List<string>();
        var w = new StringBuilder();
        foreach (var ch in text.ToLowerInvariant() + " ")
            if (char.IsLetter(ch)) w.Append(ch);
            else if (w.Length > 0) { words.Add(w.ToString()); w.Clear(); }
        string best = "en";
        int top = 0;
        foreach (var (code, list) in Common)
        {
            int n = words.Count(x => list.Contains(x));
            if (n > top) { top = n; best = code; }
        }
        // a few accents settle what the short words leave open
        if (text.IndexOfAny(new[] { 'ñ', '¿', '¡' }) >= 0 && best is "en" or "pt") best = "es";
        if (text.IndexOfAny(new[] { 'ß', 'ä', 'ö', 'ü' }) >= 0 && best == "en") best = "de";
        if (text.IndexOfAny(new[] { 'ã', 'õ' }) >= 0 && best is "en" or "es") best = "pt";
        return best;
    }
}

/// <summary>
/// Global defaults from %LOCALAPPDATA%\TutorialOverlay\config.json; a tutorial spec can override
/// lang / accent / hotkeys for its own run.
/// </summary>
internal sealed class Settings
{
    public string Lang = Loc.SystemLang();
    public Color Accent = Palette.DefaultAccent;
    public Dictionary<string, Chord> Hotkeys = DefaultHotkeys();
    public bool? Animate, HideWhenInactive;
    /// <summary>
    /// true: our windows are excluded from every capture (also the user's screenshots and recordings).
    /// false (default): visible to the user's tools; our own probes look through holes, screenshot() hides briefly.
    /// </summary>
    public bool HideFromCapture;
    /// <summary>Pick mark colours that contrast with the app around each target (explicit "color" still wins).</summary>
    public bool AutoColor = true;
    /// <summary>"Шажок", the mascot that points at targets during a tutorial.</summary>
    public bool Mascot = true;
    /// <summary>Server port when --port is not given (read at start-up).</summary>
    public int? Port;

    public static readonly string[] Actions = { "done", "back", "skip", "undo", "stop", "help", "autoplay" };

    /// <summary>
    /// How fast the autoplay acts: a factor on all of its timings (moves, pauses, waits). 1 = a calm person.
    /// Clamped so it is never so fast that apps miss its input, nor so slow that it seems to hang.
    /// </summary>
    public float AutoplaySpeed = 1f;
    public bool CursorBadge;           // the "auto" pill beside the pointer while the autoplay moves it: off by default
    // The autoplay's own pace only: how fast the pointer travels, how long it rests on a target before pressing, how
    // long the card is left to read between steps. What it waits for from the app is never scaled — the user's 2 s of
    // stillness, the app's time to answer a click, a target still loading — so a fast pace cannot outrun the app.
    // 0.6 (slow): a long move 2 s, the rest before a press 0.5 s, the card up to 6 s — still clearly moving, never
    //   "is it stuck?"; slower than that a person thinks the computer hangs.
    // 1.6 (fast): a short move 0.2 s, a long one 0.75 s, the rest 0.19 s (hover effects, a menu's highlight still
    //   show), the card at least 0.75 s; faster than that the eye loses the pointer and apps miss the hover.
    public const float AutoplaySpeedMin = 0.6f, AutoplaySpeedMax = 1.6f;
    public static readonly float[] AutoplaySpeeds = { 0.6f, 1f, 1.6f };
    /// <summary>The user's own words for what cannot be undone (settings): added to the built-in list, never taken from it.</summary>
    public string[] DangerWords = Array.Empty<string>();
    /// <summary>The autoplay's first-time note ("I will move your mouse…") has been read.</summary>
    public bool AutoplayIntroShown;

    public static Dictionary<string, Chord> DefaultHotkeys() => new()
    {
        ["done"] = new Chord(Native.MOD_CONTROL | Native.MOD_ALT, 0x27),
        ["back"] = new Chord(Native.MOD_CONTROL | Native.MOD_ALT, 0x25),
        ["skip"] = new Chord(Native.MOD_CONTROL | Native.MOD_ALT, 0x28),
        ["undo"] = new Chord(Native.MOD_CONTROL | Native.MOD_ALT, 'Z'),
        ["stop"] = new Chord(Native.MOD_CONTROL | Native.MOD_ALT, 'X'),
        ["help"] = new Chord(Native.MOD_CONTROL | Native.MOD_ALT, 'H'),
        ["autoplay"] = new Chord(Native.MOD_CONTROL | Native.MOD_ALT, 'P'),
    };

    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TutorialOverlay", "config.json");

    public static Settings Global { get; private set; } = new();

    /// <summary>What is in effect right now (global, or the running tutorial's overrides).</summary>
    public static volatile Settings Current = Global;

    public static void LoadGlobal()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var n = JsonNode.Parse(File.ReadAllText(FilePath));
            Global = new Settings().Apply(n);
            Current = Global;
            Log.Info($"settings loaded from {FilePath}");
        }
        catch (Exception e) { Log.Warn($"config.json ignored: {e.Message}"); }
    }

    public Settings Clone() => new()
    {
        Lang = Lang, Accent = Accent, Hotkeys = new Dictionary<string, Chord>(Hotkeys), Animate = Animate, HideWhenInactive = HideWhenInactive,
        HideFromCapture = HideFromCapture, AutoColor = AutoColor, Port = Port, Mascot = Mascot,
        AutoplaySpeed = AutoplaySpeed, CursorBadge = CursorBadge, DangerWords = DangerWords, AutoplayIntroShown = AutoplayIntroShown,
    };

    /// <summary>Overlay the fields present in <paramref name="n"/>.</summary>
    public Settings Apply(JsonNode? n)
    {
        if (n == null) return this;
        if ((J.Str(n, "lang") ?? J.Str(n, "language")) is string lang) Lang = lang;
        if (J.Str(n, "accent") is string acc) Accent = J.ParseColor(acc, Accent);
        if (J.Bool(n, "animate") is bool an) Animate = an;
        if (J.Bool(n, "hideWhenInactive") is bool hw) HideWhenInactive = hw;
        if (J.Bool(n, "hideFromCapture") is bool hc) HideFromCapture = hc;
        if (J.Bool(n, "autoColor") is bool ac) AutoColor = ac;
        if (J.Bool(n, "mascot") is bool ms) Mascot = ms;
        if (J.Int(n, "port") is int port && port is > 0 and < 65536) Port = port;
        if (J.Bool(n, "cursorBadge") is bool cb) CursorBadge = cb;
        if (J.Num(n, "autoplaySpeed") is double sp) AutoplaySpeed = (float)Math.Clamp(sp, AutoplaySpeedMin, AutoplaySpeedMax);
        if (J.Get(n, "dangerWords") is JsonArray dw) DangerWords = dw.Select(x => J.AsStr(x) ?? "").Where(x => x.Trim().Length > 0).ToArray();
        if (J.Bool(n, "autoplayIntroShown") is bool ai) AutoplayIntroShown = ai;
        if (J.Get(n, "hotkeys") is JsonObject hk)
            foreach (var a in Actions)
            {
                var v = J.Get(hk, a);
                if (v == null) continue;
                var s = J.AsStr(v);
                if (s == null || s.Length == 0 || s == "none" || s == "off") { Hotkeys.Remove(a); continue; }
                if (Keys.Parse(s) is Chord c) Hotkeys[a] = c;
                else Log.Warn($"hotkey for {a} not understood: \"{s}\"");
            }
        return this;
    }

    public Loc Text => Loc.For(Lang);

    /// <summary>Footer line of the card: "Ctrl+Alt+→ done · …".</summary>
    public string HintLine(bool waiting)
    {
        var t = Text;
        if (waiting) return Hotkeys.TryGetValue("stop", out var st) ? $"{st} — {t.HintStop}" : "";
        var order = new[] { ("done", t.HintDone), ("back", t.HintBack), ("skip", t.HintSkip), ("undo", t.HintUndo), ("help", t.HintHelp) };
        var used = order.Where(o => Hotkeys.ContainsKey(o.Item1)).ToList();
        if (used.Count == 0) return "";
        // shared modifiers are written once: "Ctrl+Alt: → done · ← back · …"
        uint mods = Hotkeys[used[0].Item1].Mods;
        if (used.All(o => Hotkeys[o.Item1].Mods == mods) && mods != 0)
        {
            return Keys.FormatMods(mods) + ":  " + string.Join(" · ", used.Select(o => $"{Keys.KeyName(Hotkeys[o.Item1].Vk)} {o.Item2}"));
        }
        return string.Join(" · ", used.Select(o => $"{Hotkeys[o.Item1]} {o.Item2}"));
    }

    /// <summary>Writes one field into config.json, keeping everything else in it (a flag such as "the note was read").</summary>
    public static void SaveField(string key, JsonNode? value)
    {
        try
        {
            JsonObject root = File.Exists(FilePath) ? JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject ?? new() : new();
            root[key] = value;
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            Files.WriteAtomic(FilePath, root.ToJsonString(J.Pretty));
        }
        catch (Exception e) { Log.Warn($"config.json: {key} not saved: {e.Message}"); }
    }

    public string HotkeySignature => string.Join(",", Hotkeys.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value.Mods}:{k.Value.Vk}"));
}
