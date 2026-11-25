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
        var done = Engine.Plan(null, s);
        A.Eq("wait", done.Kind, "nothing left to press: not a click on an optional target, and not 'do it yourself' either");
        A.Eq("not_completed", done.Reason, "the step completes on the next frame; if it never does, the app did not answer");
        A.Check(done.WaitMs >= 2000, "and it is given seconds for that, not one millisecond");
        var gone = Engine.Plan(null, StepOf("""{"title":"t","targets":[{"id":"x","anchor":{"element":{"name":"Close"}}}],"until":[{"type":"disappear","title":"Settings"}]}"""));
        A.Eq("click", gone.Kind, "a window to close: its close button first");
        var w = StepOf("""{"title":"t","targets":[{"id":"x","anchor":{"element":{"name":"Close"}}}],"until":[{"type":"disappear","title":"Settings"}]}""");
        w.Targets[0].Dismissed = true;
        var wp = Engine.Plan(null, w);
        A.Eq("wait", wp.Kind, "then it waits for the window to go");
        A.Check(wp.WaitMs == 0 || wp.WaitMs >= 2000, "seconds (0: the default 30 s), never a click count taken for milliseconds");
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
        InputHook.NoteForeignMove(InputSource.User, 902, 901, t0 + 40);
        A.Eq(0L, InputHook.ForeignInputAt, "a hand resting on the mouse trembles around where the autoplay put the pointer: not a move");
        InputHook.NoteForeignMove(InputSource.User, 912, 900, t0 + 50);
        A.Eq(t0 + 50, InputHook.ForeignInputAt, "10 px: a hand on the mouse");
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

    // ---- a target it cannot find: look, ask the agent, hand back

    private static void Reset(Engine.AutoRun a)
    {
        a.State = "off"; a.Reason = a.ReasonArg = null; a.MissingSince = a.AskedAt = 0; a.MissingName = null;
        a.ActedAt = 0; a.StateAt = 0;
    }

    public static void TestAloneItLooksAsLongAsTheRobotThenHandsBack()
    {
        var s = StepOf("""{"title":"t","targets":[{"id":"a","name":"Format","anchor":{"element":{"name":"Format"}}}]}""");
        var t = new Tutorial { Owner = null };
        var a = Engine.Auto;
        try
        {
            Reset(a); a.State = "acting"; a.StateAt = 1000;
            Engine.Missing(t, s, s.Targets[0], 1000 + 9000);
            A.Eq("acting", a.State, "9 s: still looking (the robot waits 20 s for a step)");
            A.Eq(0L, a.AskedAt, "no agent: nobody to ask");
            Engine.Missing(t, s, s.Targets[0], 1000 + 19000);
            A.Eq("acting", a.State, "19 s: still looking");
            Engine.Missing(t, s, s.Targets[0], 1000 + 21000);
            A.Eq("paused", a.State, "past 20 s: the controls go back");
            A.Eq("target_missing", a.Reason, "saying it could not find it");
            A.Eq("Format", a.ReasonArg, "naming what");
        }
        finally { Reset(a); }
    }

    public static void TestWithAnAgentItAsksAndWaitsForTheAnswer()
    {
        var s = StepOf("""{"title":"t","targets":[{"id":"a","name":"Format","anchor":{"element":{"name":"Format"}}}]}""");
        const string session = "autoplay-test-session";
        A.Check(McpServer.Lease.TryUse(session, out _), "the test holds the server like an agent");
        var t = new Tutorial { Owner = session };
        var a = Engine.Auto;
        long ticksWas = System.Threading.Interlocked.Read(ref McpServer.LastRequestTicks);
        try
        {
            System.Threading.Interlocked.Exchange(ref McpServer.LastRequestTicks, 0);
            Reset(a); a.State = "acting"; a.StateAt = 1000;
            Engine.Missing(t, s, s.Targets[0], 1000 + 5000);
            A.Eq(0L, a.AskedAt, "5 s: it only looks (a menu fading in, a slow app)");
            long seq = Events.LastSeq;
            Engine.Missing(t, s, s.Targets[0], 1000 + 8500);
            A.Eq(1000L + 8500, a.AskedAt, "past 8 s: it asks the agent");
            var help = Events.Since(seq, 50).LastOrDefault(e => J.Str(e, "event") == "help");
            A.Check(help != null, "a help event");
            A.Eq("autoplay", J.Str(help, "source"), "from the autoplay, not the user");
            A.Eq("cant_find", J.Str(help, "reason"), "it cannot find the target");
            Engine.Missing(t, s, s.Targets[0], 1000 + 8500 + 15000);
            A.Eq("acting", a.State, "the agent has 20 s to answer");
            A.Eq(1000L + 8500, a.AskedAt, "asked once, not again and again");
            Engine.Missing(t, s, s.Targets[0], 1000 + 8500 + 21000);
            A.Eq("paused", a.State, "no answer in 20 s: the controls go back");
            A.Eq("target_missing_asked", a.Reason, "saying the assistant was asked and did not help");

            // the agent at work (a call a moment ago): it is given the time again, up to a minute
            Reset(a); a.State = "acting"; a.StateAt = 1000;
            Engine.Missing(t, s, s.Targets[0], 1000 + 8500);
            System.Threading.Interlocked.Exchange(ref McpServer.LastRequestTicks, DateTime.UtcNow.Ticks);
            Engine.Missing(t, s, s.Targets[0], 1000 + 8500 + 25000);
            A.Eq("acting", a.State, "the agent called a moment ago: still waiting for its answer");
            Engine.Missing(t, s, s.Targets[0], 1000 + 8500 + 61000);
            A.Eq("paused", a.State, "but not beyond a minute");
        }
        finally
        {
            Reset(a);
            System.Threading.Interlocked.Exchange(ref McpServer.LastRequestTicks, ticksWas);
            McpServer.Lease.Release(session, "test");
        }
    }

    public static void TestFoundAgainItCarriesOn()
    {
        var a = Engine.Auto;
        try
        {
            var s = StepOf("""{"title":"t","targets":[{"id":"a","name":"Format","anchor":{"element":{"name":"Format"}}}]}""");
            var t = new Tutorial { Owner = null };
            Reset(a); a.State = "acting"; a.StateAt = 1000;
            Engine.Missing(t, s, s.Targets[0], 1000 + 15000);
            A.Eq(1000L, a.MissingSince, "out of sight since the step began");
            a.ActedAt = 1000 + 16000;           // something happened meanwhile (an action): the clock starts again
            Engine.Missing(t, s, s.Targets[0], 1000 + 30000);
            A.Eq("acting", a.State, "14 s since the last action: still looking");
        }
        finally { Reset(a); }
    }

    // ---- a window the lesson opened is where it goes on, never a dialog to close

    /// <summary>
    /// Ctrl+S opened "Save as", the steps for it came after: it is the lesson's window (the autoplay once closed it as
    /// "a dialog in the way" and the file name had nowhere to go). One that pops up by itself later still is in the way.
    /// </summary>
    public static void TestAWindowTheLessonOpenedIsNotClosed()
    {
        var t0 = new DateTime(2026, 11, 25, 0, 51, 14, DateTimeKind.Utc);
        Tutorial Lesson(string second)
        {
            var t = new Tutorial { Defaults = new AnchorSpec { Process = "mspaint" }, Index = 1 };
            t.Steps.Add(StepOf("""{"title":"Ctrl+S","until":[{"type":"key","keys":"Ctrl+S"}]}"""));
            t.Steps.Add(StepOf(second));
            t.Steps[0].CompletedAt = t0;
            t.Steps[1].EnteredAt = t0.AddSeconds(37);     // appended once the window was seen
            return t;
        }
        var byTitle = Lesson("""{"title":"name","targets":[{"anchor":{"window":"Save as","element":{"name":"File name:"}}}]}""");
        A.Check(Engine.LedTo(byTitle, "Save as", "#32770", null), "a target anchored in it by its title");
        var asObject = Lesson("""{"title":"name","targets":[{"anchor":{"window":{"title":"Save as"},"element":{"name":"File name:"}}}]}""");
        A.Eq("Save as", asObject.Steps[1].Targets[0].Anchor.Window, "window as {title} names it too");
        A.Check(Engine.LedTo(asObject, "Save as", "#32770", null), "and is the lesson's window");
        var plain = Lesson("""{"title":"name","targets":[{"anchor":{"element":{"name":"File name:"}}}]}""");
        A.Check(Engine.LedTo(plain, "Save as", "#32770", t0.AddMilliseconds(600)), "opened just after the previous step");
        A.Check(!Engine.LedTo(plain, "Save changes?", "#32770", t0.AddSeconds(30)), "popped up by itself later: still in the way");
        A.Check(!Engine.LedTo(plain, "Save as", "#32770", null), "open before the lesson knew of it: in the way");
        // before the steps for it are appended: the lesson waits after Ctrl+S, the window is not "to close first"
        var waiting = new Tutorial { Defaults = new AnchorSpec { Process = "mspaint" }, Index = 0, State = TutorialState.Waiting };
        waiting.Steps.Add(StepOf("""{"title":"Ctrl+S","until":[{"type":"key","keys":"Ctrl+S"}]}"""));
        waiting.Steps[0].CompletedAt = t0;
        A.Check(Engine.LedTo(waiting, "Save as", "#32770", t0.AddMilliseconds(400)), "waiting after the step that opened it");
        A.Check(!Engine.LedTo(waiting, "Save as", "#32770", t0.AddSeconds(-20)), "open before that step: in the way");
        waiting.State = TutorialState.Running;     // the ✓ moment: done, not moved on yet
        A.Check(Engine.LedTo(waiting, "Save as", "#32770", t0.AddMilliseconds(300)), "the step's ✓ moment too");
    }

    // ---- the hotkey

    public static void TestTheHotkeyStopsEvenThoughItsOwnKeysPausedFirst()
    {
        var a = Engine.Auto;
        try
        {
            Reset(a);
            a.State = "paused"; a.Reason = "user_took_over"; a.StateAt = Environment.TickCount64;
            Engine.AutoplayCommand("toggle", "user");
            A.Eq("off", a.State, "Ctrl+Alt+P while it runs: the keys paused it a moment ago, the hotkey stops it");
        }
        finally { Reset(a); }
    }

    // ---- the tray's speed

    public static void TestSpeedIsKeptInTheSafeRange()
    {
        A.Eq(1.6f, new Settings().Apply(A.Json("""{"autoplaySpeed":5}""")).AutoplaySpeed, "too fast for apps and eyes: capped");
        A.Eq(0.6f, new Settings().Apply(A.Json("""{"autoplaySpeed":0.1}""")).AutoplaySpeed, "too slow, it would seem stuck: raised");
        A.Eq(1f, new Settings().AutoplaySpeed, "normal by default");
        A.Check(!new Settings().CursorBadge, "the 'auto' tag at the pointer is off unless the user turns it on");
        A.Check(new Settings().Apply(A.Json("""{"cursorBadge":true}""")).CursorBadge, "turned on in config.json");
        A.Check(new Settings().Apply(A.Json("""{"cursorBadge":true}""")).Clone().CursorBadge, "and kept by a copy of the settings");
        A.Check(Settings.AutoplaySpeeds.All(v => v >= Settings.AutoplaySpeedMin && v <= Settings.AutoplaySpeedMax), "every choice in the window is safe");
        var was = InputSend.Speed;
        try
        {
            var l = StepOf("{\"title\":\"Long\",\"text\":\"" + new string('a', 400) + "\"}");
            InputSend.Speed = Settings.AutoplaySpeedMin;
            A.Check(Engine.ReadMs(l) <= 6000, "slow: the card waits at most 6 s, never 'is it stuck?'");
            A.Check(InputSend.MoveMs(5000) <= 2100, "slow: a long move about 2 s");
            InputSend.Speed = Settings.AutoplaySpeedMax;
            A.Check(InputSend.MoveMs(10) >= 200, "fast: a move the eye still follows");
            A.Check(InputSend.Ms(300, 120) >= 180, "fast: the rest before a press keeps hover effects");
            A.Check(Engine.ReadMs(StepOf("""{"title":"S","text":"x"}""")) >= 600, "fast: the card still shows a moment");
        }
        finally { InputSend.Speed = was; }
    }

    // ---- a checklist: each target its own tries; the app's answer before the log line still counts

    public static void TestEachTargetOfAStepGetsItsOwnTries()
    {
        var s = StepOf("""{"title":"t","targets":[{"id":"a","name":"Line","anchor":{"element":{"name":"Line"}}},{"id":"b","name":"Curve","anchor":{"element":{"name":"Curve"}}}]}""");
        var a = Engine.Auto;
        long pressWas = InputSend.LastPressAt;
        try
        {
            Reset(a); a.State = "acting"; a.Tries = 0; a.TriedOn = null;
            Engine.Acted(new Engine.AutoPlan("click") { Target = s.Targets[0] }, "test");
            Engine.Acted(new Engine.AutoPlan("click") { Target = s.Targets[0] }, "test");
            A.Eq(2, a.Tries, "two presses on Line");
            Engine.Acted(new Engine.AutoPlan("click") { Target = s.Targets[1] }, "test");
            A.Eq(1, a.Tries, "Curve starts with its own count: Line's retry is not taken from it");
        }
        finally { Reset(a); a.Tries = 0; a.TriedOn = null; InputSend.LastPressAt = pressWas; }
    }

    public static void TestAHitBeforeTheLogLineIsHeard()
    {
        var s = StepOf("""{"title":"t","targets":[{"id":"a","name":"Curve","anchor":{"element":{"name":"Curve"}}},{"id":"b","name":"Oval","anchor":{"element":{"name":"Oval"}}}]}""");
        var a = Engine.Auto;
        long pressWas = InputSend.LastPressAt;
        try
        {
            Reset(a); a.State = "acting"; a.Tries = 0; a.TriedOn = null; a.MissedAt = 0;
            long press = Environment.TickCount64 - 80;
            InputSend.LastPressAt = press;
            a.HeardAt = press + 10;              // the hit arrived 70 ms before Acted ran
            s.Targets[0].Dismissed = true;
            Engine.Acted(new Engine.AutoPlan("click") { Target = s.Targets[0] }, "test");
            a.ActTarget = s.Targets[0]; a.ActKind = "click";
            A.Eq(press, a.ActedAt, "the action counts from the press going down");
            A.Check(Engine.Verify(null, s, press + 300), "heard and ticked: on to the next target at once");
            A.Eq("verifying", a.State, "not paused");
        }
        finally { Reset(a); a.Tries = 0; a.TriedOn = null; a.HeardAt = 0; InputSend.LastPressAt = pressWas; }
    }

    public static void TestPressedTargetsThenTheStateComesAMomentLater()
    {
        var s = StepOf("""{"title":"=","targets":[{"id":"eq","name":"Equals","anchor":{"element":{"name":"Equals"}}}],"until":[{"type":"uia","anchor":{"element":{"name":"Display is 42"}},"property":"exists"}]}""");
        A.Eq("click", Engine.Plan(null, s).Kind, "first the button");
        s.Targets[0].Dismissed = true;
        var p = Engine.Plan(null, s);
        A.Eq("wait", p.Kind, "pressed: the display answers a moment later — wait, not 'do it yourself'");
        A.Check(p.WaitMs >= 2000 && p.WaitMs <= 10000, "a short wait, but long enough: the app's time to answer");
    }

    public static void TestAStrokePressingOffTheTargetIsNotAMiss()
    {
        var s = StepOf("""{"title":"t","targets":[{"id":"a","anchor":{"element":{"name":"Canvas"}}}],"until":[{"type":"trace","path":[[0,0],[100,0]]}]}""");
        var a = Engine.Auto;
        try
        {
            Reset(a); a.State = "verifying"; a.ActKind = "trace"; a.ActedAt = 1000; a.MissedAt = 1001;
            Engine.Verify(null, s, 1200);
            A.Eq("verifying", a.State, "a stroke presses where it starts: the overlay's 'miss' there does not stop it");
            a.ActKind = "click";
            Engine.Verify(null, s, 1200);
            A.Eq("paused", a.State, "a click beside its target does");
        }
        finally { Reset(a); a.MissedAt = 0; }
    }
}

public static class SettingsWindowTests
{
    /// <summary>The tray's settings window builds (every page, every control found by its tag) and closes.</summary>
    public static void TestTheSettingsWindowOpens()
    {
        Exception? err = null;
        var th = new System.Threading.Thread(() =>
        {
            try
            {
                SettingsWindow.Open("look");
                SettingsWindow.CloseForTests();
            }
            catch (Exception e) { err = e; }
        });
        th.SetApartmentState(System.Threading.ApartmentState.STA);
        th.Start();
        th.Join(20000);
        A.Check(err == null, "the settings window opens: " + err);
    }
}

public static class CursorBadgeTests
{
    /// <summary>
    /// The "auto" pill draws itself, shows and hides — and its own drawing does not hide the Paint event every
    /// control has (it was a method named Paint: warning CS0108).
    /// </summary>
    public static void TestTheBadgeDrawsShowsAndHides()
    {
        A.Check(typeof(CursorBadge).GetMember("Paint", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.DeclaredOnly).Length == 0,
            "CursorBadge declares nothing named Paint: Control.Paint stays itself");
        Exception? err = null;
        bool shown = false, hidden = false;
        var th = new System.Threading.Thread(() =>
        {
            try
            {
                using var b = new CursorBadge();
                _ = b.Handle;
                b.Update(true);
                shown = Native.IsWindowVisible(b.Handle);
                b.Update(false);
                hidden = !Native.IsWindowVisible(b.Handle);
            }
            catch (Exception e) { err = e; }
        });
        th.SetApartmentState(System.Threading.ApartmentState.STA);
        th.Start();
        th.Join(20000);
        A.Check(err == null, "the badge draws without an error: " + err);
        A.Check(shown, "on: the badge is on screen");
        A.Check(hidden, "off: the badge is gone");
    }
}
