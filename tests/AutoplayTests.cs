using System;
using System.Drawing;
using System.Linq;

namespace TutorialOverlay.Tests;

/// <summary>The autoplay ("Show me"), what cannot be undone, and who pressed what. Design: docs/autoplay-design.md.</summary>
public static class AutoplayTests
{
    private static Step StepOf(string json) => Step.Parse(A.Json(json), new AnchorSpec { Process = "Notepad" }, 0);

    /// <summary>A target as the frame loop leaves it: on screen, framed at a rectangle.</summary>
    private static void Show(Mark m, float x, float y, float w = 40, float h = 20)
    {
        var r = new RectangleF(x, y, w, h);
        m.Geo = new MarkGeo { A = new AnchorGeo { P = new PointF(x, y), Bounds = r, Pids = new[] { 1 } } };
        m.Geo.Hits.Add(new HitArea { Rect = r });
        m.Visibility = "visible";
    }

    // ---- the plan

    public static void TestPlanClicksTheNextRequiredTargetInOrder()
    {
        var s = StepOf("""
            {"title":"t","targets":[
              {"id":"opt","name":"Maybe","optional":true,"anchor":{"element":{"name":"Maybe"}}},
              {"id":"a","name":"First","anchor":{"element":{"name":"First"}}},
              {"id":"b","name":"Second","anchor":{"element":{"name":"Second"}}}]}
            """);
        var p = Engine.Plan(null, s);
        A.Eq("click", p.Kind, "a step of clicks");
        A.Eq("a", p.Target?.Id, "the first required target; the optional one is left alone");
        s.Targets[1].Dismissed = true;
        A.Eq("b", Engine.Plan(null, s).Target?.Id, "then the next one");
        s.Targets[2].Dismissed = true;
        A.Eq("pause", Engine.Plan(null, s).Kind, "nothing left to press: not a click on an optional target");
    }

    public static void TestPlanRightButtonAndCount()
    {
        var s = StepOf("""{"title":"t","targets":[{"id":"a","button":"right","anchor":{"element":{"name":"Cell"}}}],"until":[{"type":"count","count":3}]}""");
        s.Targets[0].Hits = 1;
        var p = Engine.Plan(null, s);
        A.Eq("click", p.Kind, "count: clicks");
        A.Eq(2, p.Times, "the clicks still missing, not all three again");
        A.Eq("right", p.Button, "with the target's button");
    }

    public static void TestPlanKeysAndKeysThatDestroy()
    {
        var p = Engine.Plan(null, StepOf("""{"title":"t","until":[{"type":"key","keys":["Ctrl+S"]}]}"""));
        A.Eq("key", p.Kind, "a key step");
        A.Eq("Ctrl+S", p.Chord?.ToString(), "its chord");
        var d = Engine.Plan(null, StepOf("""{"title":"t","until":[{"type":"key","keys":["Shift+Delete"]}]}"""));
        A.Eq("pause", d.Kind, "Shift+Delete deletes for good: left to the user");
        A.Eq("danger", d.Reason, "because it cannot be undone");
    }

    public static void TestPlanScrollsSidewaysWithShift()
    {
        var p = Engine.Plan(null, StepOf("""{"title":"t","until":[{"type":"scroll","direction":"left","notches":4}]}"""));
        A.Eq("scroll", p.Kind, "a scroll step");
        A.Check(p.Up && p.Sideways, "left = the wheel up, sideways");
        A.Check(p.Hold.Contains(0x10u), "sideways is Shift + the wheel");
        A.Eq(4, p.Times, "the notches asked for");
        var v = Engine.Plan(null, StepOf("""{"title":"t","targets":[{"anchor":{"element":{"name":"Row"}}}],"until":[{"type":"scroll","until":"visible"}]}"""));
        A.Eq(1, v.Times, "until visible: a notch at a time, looking after each");
    }

    public static void TestPlanLeavesChoicesAndMarkedStepsToTheUser()
    {
        var c = Engine.Plan(null, StepOf("""{"title":"t","buttons":["Light","Dark"]}"""));
        A.Eq("choice", c.Reason, "the agent's buttons: the user chooses");
        var off = Engine.Plan(null, StepOf("""{"title":"t","autoplay":false,"autoplayNote":"Pick your own name","targets":[{"anchor":{"element":{"name":"x"}}}]}"""));
        A.Eq("cant_do", off.Reason, "autoplay:false");
        A.Eq("Pick your own name", off.Arg, "with the lesson's reason");
        A.Eq("cant_do", Engine.Plan(null, StepOf("""{"title":"t","until":[{"type":"pixel","at":[1,1],"expr":"r>1"}]}""")).Reason, "a pixel with nothing to press");
    }

    public static void TestPlanLooksTypesAndWaits()
    {
        A.Eq("look", Engine.Plan(null, StepOf("""{"title":"t","targets":[{"anchor":{"element":{"name":"x"}}}],"until":["manual"]}""")).Kind, "a step to look at: point, then Done");
        var ty = Engine.Plan(null, StepOf("""{"title":"t","targets":[{"anchor":{"element":{"name":"Name"}}}],"until":[{"type":"uia","property":"value","equals":"Anna","anchor":{"element":{"name":"Name"}}}]}"""));
        A.Eq("type", ty.Kind, "a field to fill in with a known value");
        A.Eq("Anna", ty.Text, "the value");
        A.Eq("wait", Engine.Plan(null, StepOf("""{"title":"t","until":[{"type":"appear","window":"Download complete"}]}""")).Kind, "a step that only waits");
        var opens = StepOf("""{"title":"t","targets":[{"id":"m","anchor":{"element":{"name":"File"}},"opens":{"window":"Open"}}]}""");
        opens.Targets[0].OpensArmedAt = DateTime.UtcNow;
        A.Eq("wait", Engine.Plan(null, opens).Kind, "pressed, its window still opening: not pressed again");
    }

    // ---- what cannot be undone

    public static void TestDangerWords()
    {
        foreach (var yes in new[] { "Оформить заказ", "Купить", "Buy now", "Place order", "Оплатить 499 ₽", "Удалить навсегда", "Checkout", "Empty Recycle Bin", "Отправить" })
            A.Check(Danger.Of(yes) != null, $"«{yes}» cannot be undone");
        foreach (var no in new[] { "Добавить в корзину", "Купить позже", "Add to cart", "Удалить фильтр", "Format Painter", "Перевести страницу", "Eraser", "Settings", "Удалить", "Delete", "" })
            A.Check(Danger.Of(no) == null, $"«{no}» is not");
        A.Check(Danger.Of("Чтобы купить подписку, откройте раздел Аккаунт и выберите тариф на год") == null, "a sentence is not a button");
        A.Eq("payment", Danger.Of("Подтвердить оплату")?.Kind, "a payment");
        A.Eq("irreversible", Danger.OfChord(TutorialOverlay.Keys.Parse("Shift+Delete")!.Value)?.Kind, "Shift+Delete");
        A.Check(Danger.OfChord(TutorialOverlay.Keys.Parse("Delete")!.Value) == null, "Delete alone goes to the recycle bin");
        var was = Danger.Extra;
        try
        {
            Danger.Extra = new[] { "Сбросить прогресс" };
            A.Eq("user", Danger.Of("Сбросить прогресс")?.Kind, "the user's own words count");
            A.Eq("payment", Danger.Of("Купить")?.Kind, "and never take the built-in ones away");
        }
        finally { Danger.Extra = was; }
    }

    public static void TestDangerOfATarget()
    {
        var s = StepOf("""
            {"title":"t","targets":[
              {"id":"spec","name":"Next","danger":true,"dangerNote":"499 ₽ will be charged","anchor":{"element":{"name":"Next"}}},
              {"id":"name","anchor":{"element":{"name":"Оформить заказ"}}},
              {"id":"text","anchor":{"find":{"text":"Buy now"}}},
              {"id":"ok","name":"Save","anchor":{"element":{"name":"Save"}}}]}
            """);
        A.Eq("spec", Engine.DangerOf(s.Targets[0])?.Kind, "the spec says so");
        A.Check(Engine.DangerOf(s.Targets[1]) != null, "the element's name says so");
        A.Check(Engine.DangerOf(s.Targets[2]) != null, "the text it is found by says so");
        A.Check(Engine.DangerOf(s.Targets[3]) == null, "Save does not");
    }

    public static void TestDangerTargetIsRedWithAPadlock()
    {
        var m = Mark.Parse(A.Json("""{"name":"Buy","anchor":{"process":"x","element":{"name":"Buy"}}}"""), null, "m");
        m.DangerSeen = new Danger.Hit("payment", "buy");
        var geo = Layout.Build(m, new AnchorGeo { P = new PointF(100, 100), Bounds = new RectangleF(80, 90, 60, 24) }, false);
        A.Check(geo.Prims.Any(p => p.Kind == PrimKind.Rect && p.Color.ToArgb() == Palette.Danger.ToArgb()), "its frame is red");
        A.Check(geo.Prims.Any(p => p.Kind == PrimKind.DangerTag), "with the «press it yourself» tag");
        m.DangerSeen = null;
        var plain = Layout.Build(m, new AnchorGeo { P = new PointF(100, 100), Bounds = new RectangleF(80, 90, 60, 24) }, false);
        A.Check(!plain.Prims.Any(p => p.Kind == PrimKind.DangerTag || p.Color.ToArgb() == Palette.Danger.ToArgb()), "an ordinary target is not red");
    }

    public static void TestValidateAsksToMarkWhatCannotBeUndone()
    {
        var w = SpecLint.CheckSteps(A.Json("""[{"title":"t","targets":[{"name":"Оформить заказ","anchor":{"element":{"name":"x"}}}]}]"""), 0);
        A.Check(w.Any(x => x.Contains("danger:true")), "a «Checkout» target without danger:true is pointed out");
        var ok = SpecLint.CheckSteps(A.Json("""[{"title":"t","autoplay":false,"autoplayNote":"n","targets":[{"name":"Оформить заказ","danger":true,"dangerNote":"n","anchor":{"element":{"name":"x"}}}]}]"""), 0);
        A.Check(ok.Count == 0, "marked: nothing to say, and the new fields are known: " + string.Join("; ", ok));
    }

    // ---- who pressed

    public static void TestInputSources()
    {
        A.Eq(InputSource.User, InputHook.SourceOf(false, IntPtr.Zero), "a real mouse");
        A.Eq(InputSource.Other, InputHook.SourceOf(true, IntPtr.Zero), "another program's synthetic input");
        A.Eq(InputSource.Autoplay, InputHook.SourceOf(true, (IntPtr)InputHook.AutoplayTag), "the autoplay's own");
        A.Eq(InputSource.Probe, InputHook.SourceOf(true, (IntPtr)InputHook.ProbeTag), "the hook watchdog's probe");
        A.Eq("autoplay", new InputEvent(InputKind.MouseDown, 0, 0, "left", 0, 0, true, false, 0, 0, InputSource.Autoplay).By, "events say who");
    }

    public static void TestAJoltIsNotAHandOnTheMouse()
    {
        long t0 = Environment.TickCount64 + 100000;
        InputHook.NoteForeignAt(0);
        InputHook.NoteForeignMove(InputSource.User, 500, 500, t0);
        InputHook.NoteForeignMove(InputSource.User, 502, 501, t0 + 20);
        A.Eq(0L, InputHook.ForeignInputAt, "2 px: the desk was knocked");
        InputHook.NoteForeignMove(InputSource.Autoplay, 900, 900, t0 + 30);
        A.Eq(0L, InputHook.ForeignInputAt, "the autoplay's own moves are not someone else's");
        InputHook.NoteForeignMove(InputSource.User, 512, 500, t0 + 40);
        A.Eq(t0 + 40, InputHook.ForeignInputAt, "10 px: a hand on the mouse");
        A.Eq(InputSource.User, InputHook.ForeignInputBy, "the user's");
        InputHook.NoteForeignAt(0);
    }

    public static void TestTheUserWinsWithinTwoSeconds()
    {
        A.Check(!Engine.QuietEnough(10_000 + 1500, 10_000), "1.5 s after the user's input: the autoplay does not act");
        A.Check(Engine.QuietEnough(10_000 + 2100, 10_000), "2.1 s after: it may");
    }

    public static void TestAHeardClickIsNeverSentAgain()
    {
        var s = StepOf("""{"title":"t","targets":[{"id":"a","name":"Pay","anchor":{"element":{"name":"x"}}}]}""");
        var a = Engine.Auto;
        try
        {
            a.State = "verifying"; a.ActKind = "click"; a.ActTarget = s.Targets[0]; a.ActedAt = 1000; a.Tries = 1; a.MissedAt = 0;
            a.HeardAt = 1100;
            A.Check(!Engine.Verify(null, s, 1000 + 3000), "heard, the app still slow: wait, do not press again");
            a.HeardAt = 0;
            A.Check(!Engine.Verify(null, s, 1000 + 800), "not heard yet: wait a moment");
            A.Check(Engine.Verify(null, s, 1000 + 1600), "never heard: once more");
            a.Tries = 2; a.State = "verifying";
            A.Check(!Engine.Verify(null, s, 1000 + 1600), "never heard twice: no third time");
            A.Eq("paused", a.State, "control back to the user");
            A.Eq("not_completed", a.Reason, "saying the app did not answer");
            a.State = "verifying"; a.MissedAt = 1200; a.Tries = 1;
            A.Check(!Engine.Verify(null, s, 1300), "a miss");
            A.Eq("missed", a.Reason, "is said as such");
        }
        finally { a.State = "off"; a.Reason = null; a.MissedAt = a.HeardAt = 0; }
    }

    // ---- pace

    public static void TestPaceStaysInARangeAppsAndPeopleFollow()
    {
        var was = InputSend.Speed;
        try
        {
            InputSend.Speed = 1;
            A.Eq(350, InputSend.MoveMs(10), "a short move still takes a moment the eye follows");
            A.Eq(1200, InputSend.MoveMs(5000), "a long one is capped");
            InputSend.Speed = 4;
            A.Check(InputSend.MoveMs(10) >= 150, "even at top speed a move is seen");
            A.Check(InputSend.Ms(300, 120) >= 120, "and the rest before a press stays long enough for hover effects");
            var s = StepOf("""{"title":"Short","text":"x"}""");
            InputSend.Speed = 1;
            A.Check(Engine.ReadMs(s) >= 1200 && Engine.ReadMs(s) < 1500, "a short card is read quickly");
            var l = StepOf("{\"title\":\"Long\",\"text\":\"" + new string('a', 400) + "\"}");
            A.Eq(5000, Engine.ReadMs(l), "a long one gets at most 5 s");
        }
        finally { InputSend.Speed = was; }
    }
}
