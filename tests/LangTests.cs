using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TutorialOverlay.Tests;

/// <summary>The overlay's own words in many languages: the files, the choice of language, the font, danger.</summary>
public static class LangTests
{
    private static readonly string LangDir = Path.Combine(Root(), "src", "lang");

    private static string Root()
    {
        var d = AppContext.BaseDirectory;
        while (d != null && !File.Exists(Path.Combine(d, "TutorialOverlay.csproj"))) d = Path.GetDirectoryName(d);
        return d ?? throw new Exception("repository root not found");
    }

    private static JsonObject File_(string code) => (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(LangDir, code + ".json")))!;

    private static IEnumerable<string> Fields() =>
        typeof(Loc).GetFields(BindingFlags.Public | BindingFlags.Instance).Where(f => f.Name is not "Code" and not "Name").Select(f => f.Name);

    private static IEnumerable<(string key, string text)> Strings(JsonObject o)
    {
        foreach (var (k, v) in o)
        {
            if (k.StartsWith('_')) continue;
            if (v is JsonObject d) foreach (var (dk, dv) in d) yield return (k + "." + dk, dv!.GetValue<string>());
            else yield return (k, v!.GetValue<string>());
        }
    }

    private static string Holes(string s) => string.Join(",", Regex.Matches(s, @"\{\d+\}").Select(m => m.Value).OrderBy(x => x));

    /// <summary>English is the fallback: it has every word the code has, and nothing the code does not know.</summary>
    public static void TestEnglishHasEveryWord()
    {
        var en = File_("en");
        foreach (var f in Fields()) A.Check(en.ContainsKey(f), $"en.json has {f}");
        foreach (var (k, _) in en) A.Check(k.StartsWith('_') || Fields().Contains(k), $"en.json: {k} is a word the code uses");
        A.Check(en["AutoReasons"] is JsonObject r && r.Count >= 20, "the autoplay's reasons are there");
    }

    /// <summary>Every built-in language: the same keys as English, the same {0} holes in each, a name of its own.</summary>
    public static void TestEveryLanguageMatchesEnglish()
    {
        var en = Strings(File_("en")).ToDictionary(x => x.key, x => x.text);
        var codes = Directory.GetFiles(LangDir, "*.json").Select(f => Path.GetFileNameWithoutExtension(f)).ToList();
        foreach (var c in new[] { "en", "ru", "es", "de", "zh-Hans" }) A.Check(codes.Contains(c), $"{c}.json is built in");
        foreach (var code in codes.Where(c => c != "en"))
        {
            var o = File_(code);
            A.Check(J.Str(o, "_name") is string n && n.Length > 1 && n != "English", $"{code}: its own name");
            var mine = Strings(o).ToDictionary(x => x.key, x => x.text);
            foreach (var (k, t) in en)
            {
                A.Check(mine.ContainsKey(k), $"{code}.json has {k}");
                if (mine.TryGetValue(k, out var m))
                {
                    A.Eq(Holes(t), Holes(m), $"{code}.json {k}: the same {{0}} holes as English");
                    A.Check(m.Trim().Length > 0, $"{code}.json {k}: not empty");
                }
            }
            foreach (var k in mine.Keys) A.Check(en.ContainsKey(k), $"{code}.json: {k} is a key English has");
        }
    }

    /// <summary>The words come from the files: each language speaks its own, a missing word falls back on English.</summary>
    public static void TestWordsComeFromTheFiles()
    {
        A.Eq("Back", Loc.For("en").Back, "English");
        A.Eq("Назад", Loc.For("ru").Back, "Russian");
        A.Eq("Atrás", Loc.For("es").Back, "Spanish");
        A.Eq("Zurück", Loc.For("de").Back, "German");
        A.Eq("上一步", Loc.For("zh-Hans").Back, "Chinese");
        A.Eq("Tienes el control", Loc.For("es").AutoPausedTitle, "the autoplay's words too");
        A.Check(Loc.For("de").AutoReasons["user_took_over"].Contains("Maus"), "and its reasons");

        var l = new Loc();
        Loc.Apply(l, JsonNode.Parse("""{"_name":"Test","Back":"Retour","AutoReasons":{"loop":"boucle"},"Unknown":"x"}"""));
        A.Eq("Retour", l.Back, "a file sets a word");
        A.Eq("boucle", l.AutoReasons["loop"], "and a reason");
        A.Eq("Test", l.Name, "its _name is the language's name");
    }

    /// <summary>A code in any form finds the language there are words for; one there are none for is English.</summary>
    public static void TestTheLanguageIsFoundByItsCode()
    {
        A.Eq("es", Loc.Resolve("es-MX"), "Mexican Spanish → Spanish");
        A.Eq("de", Loc.Resolve("de-AT"), "Austrian German → German");
        A.Eq("zh-Hans", Loc.Resolve("zh-CN"), "zh-CN → simplified");
        A.Eq("zh-Hans", Loc.Resolve("zh"), "zh → simplified");
        A.Eq("ru", Loc.Resolve("RU"), "any case");
        A.Eq("en", Loc.Resolve("xx-YY"), "no words for it: English");
        A.Eq("en", Loc.Resolve(null), "none given: English");
        A.Eq("en", Loc.For("xx").Code, "For falls back the same way");
        A.Check(Loc.Available().First().Code == "en", "English heads the settings' list");
        A.Check(Loc.Available().Any(x => x.Code == "zh-Hans" && x.Name == "简体中文"), "each language by its own name");
    }

    /// <summary>A lesson without "lang" is told by its words: the script first, then the common short words.</summary>
    public static void TestTheLessonsLanguageIsToldFromItsWords()
    {
        JsonNode Spec(params string[] says) => new JsonObject
        {
            ["steps"] = new JsonArray(says.Select(s => (JsonNode)new JsonObject { ["title"] = s }).ToArray()),
        };
        A.Eq("es", Loc.LangOf(Spec("Abra el menú Ver", "Haga clic en Ajuste de línea para que las líneas largas se ajusten")), "Spanish");
        A.Eq("de", Loc.LangOf(Spec("Öffnen Sie das Menü Ansicht", "Klicken Sie auf Zeilenumbruch, damit die Zeilen in das Fenster passen")), "German");
        A.Eq("zh-Hans", Loc.LangOf(Spec("打开“视图”菜单", "点击“自动换行”，长行就会在窗口边缘换行")), "Chinese");
        A.Eq("zh-Hans", Loc.LangOf(Spec("点击 File 菜单", "选择 Save As 保存文件")), "Chinese naming Latin items");
        A.Eq("en", Loc.LangOf(Spec("Open the View menu", "Click Word wrap so long lines fit the window")), "English stays English");
    }

    /// <summary>Chinese gets Windows' own Chinese UI font: Segoe UI has none and the borrowed glyphs come out uneven.</summary>
    public static void TestChineseGetsAFontOfItsOwn()
    {
        A.Eq("Microsoft YaHei UI", Theme.OwnFont("zh-Hans"), "simplified Chinese");
        A.Eq("Microsoft JhengHei UI", Theme.OwnFont("zh-TW"), "traditional Chinese");
        A.Eq("Yu Gothic UI", Theme.OwnFont("ja"), "Japanese");
        A.Eq(null, Theme.OwnFont("de"), "German needs none");
        A.Eq(Theme.SystemFamily.Name, Theme.FamilyFor("es").Name, "Spanish: the system's UI font");
        if (Theme.FamilyFor("zh-Hans").Name != Theme.SystemFamily.Name)
            A.Eq("Microsoft YaHei UI", Theme.FamilyFor("zh-Hans").Name, "Chinese: YaHei, where Windows has it");
        A.Eq("Microsoft YaHei UI", Theme.OwnFont("zh-Hans"), "the lesson's language decides before the settings'");
    }

    /// <summary>What cannot be undone is known in Spanish, German and Chinese too, whatever language the overlay speaks.</summary>
    public static void TestDangerInOtherLanguages()
    {
        A.Eq("payment", Danger.Of("Comprar ahora")?.Kind, "Spanish: buy now");
        A.Eq("payment", Danger.Of("Zahlungspflichtig bestellen")?.Kind, "German: the order button the law asks for");
        A.Eq("payment", Danger.Of("立即支付")?.Kind, "Chinese: pay now");
        A.Eq("payment", Danger.Of("提交订单")?.Kind, "Chinese: place the order");
        A.Eq("irreversible", Danger.Of("Endgültig löschen")?.Kind, "German: delete for good");
        A.Eq("irreversible", Danger.Of("永久删除")?.Kind, "Chinese: delete for good");
        A.Eq("send", Danger.Of("Enviar")?.Kind, "Spanish: send");
        A.Check(Danger.Of("Añadir al carrito") == null, "Spanish: add to cart is not");
        A.Check(Danger.Of("In den Warenkorb") == null, "German: into the cart is not");
        A.Check(Danger.Of("加入购物车") == null, "Chinese: add to cart is not");
        A.Check(Danger.Of("支付宝设置") != null, "Chinese has no spaces: the word inside a longer name counts");
    }

    /// <summary>The settings window builds in every language (its rows measure their own words).</summary>
    public static void TestTheSettingsWindowOpensInEveryLanguage()
    {
        var was = Settings.Global.Lang;
        try
        {
            foreach (var (code, _) in Loc.Available())
            {
                Settings.Global.Lang = code;
                Exception? err = null;
                var th = new System.Threading.Thread(() =>
                {
                    try { SettingsWindow.Open("look"); SettingsWindow.CloseForTests(); }
                    catch (Exception e) { err = e; }
                });
                th.SetApartmentState(System.Threading.ApartmentState.STA);
                th.Start();
                th.Join(20000);
                A.Check(err == null, $"the settings window opens in {code}: {err}");
            }
        }
        finally { Settings.Global.Lang = was; }
    }
}
