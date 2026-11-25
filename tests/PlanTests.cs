using System;
using System.Linq;
using System.Text.Json.Nodes;

namespace TutorialOverlay.Tests;

/// <summary>
/// The lesson's whole plan is known from the start: a step that cannot be written yet goes in as {id, title, later},
/// the agent is told ahead (plan_ahead) and fills it in place by its id; the user sees the whole path meanwhile.
/// </summary>
public static class PlanTests
{
    private static string Lint(string spec) => string.Join("; ", SpecLint.CheckSpec(A.Json(spec)));

    public static void TestThePlanIsCheckedWhenTheLessonStarts()
    {
        string whole = """{"title":"t","keepOpen":true,"steps":[{"title":"Open"},{"id":"pick","title":"Pick your file","later":"the user's own files"}]}""";
        A.Eq("", Lint(whole), "a whole plan with a step written later");
        A.Check(Lint("""{"title":"t","keepOpen":true,"steps":[{"title":"Open"}]}""").Contains("keepOpen"), "keepOpen and nothing to fill: one step at a time");
        A.Eq("", Lint("""{"title":"t","steps":[{"title":"Open"}]}"""), "a short lesson without keepOpen is fine");
        A.Check(Lint("""{"title":"t","steps":[{"title":"Pick","later":"files"}]}""").Contains("needs an id"), "no id to fill it by");
        A.Check(Lint("""{"title":"t","steps":[{"id":"p","title":"Pick","later":"files","targets":[{"anchor":{"element":{"name":"x"}}}]}]}""").Contains("no targets"),
            "a later step with targets of its own");
        var dlg = Lint("""{"title":"t","steps":[{"title":"Save","until":[{"type":"key","keys":"Ctrl+S"}]},{"id":"name","title":"Name it","later":"the dialog"}]}""");
        A.Check(dlg.Contains("Save as"), "Save as is known ahead: " + dlg);
        A.Eq("", Lint("""{"title":"t","steps":[{"title":"Open","until":[{"type":"key","keys":"Ctrl+O"}]},{"id":"pick","title":"Pick your photo","later":"their files"}]}"""),
            "which file to open is the user's: later is right");
    }

    public static void TestAStepWrittenLaterWaitsForTheAgent()
    {
        var s = Step.Parse(A.Json("""{"id":"pick","title":"Pick your file","later":"the user's own files"}"""), new AnchorSpec { Process = "x" }, 3);
        A.Eq("the user's own files", s.Later, "why it is written later");
        var p = Engine.Plan(null, s);
        A.Eq("wait", p.Kind, "the autoplay waits for it");
        A.Eq("preparing", p.Reason, "and says why when it gives up");
        A.Check(Loc.For("ru").AutoReasons.ContainsKey("preparing"), "in words");
        A.Check(Loc.For("ja").LaterPreparing.Length > 0, "the card says the assistant prepares it");
    }

    /// <summary>The whole cycle: plan_ahead two steps before, the step filled in place by its id, the user's wait told.</summary>
    public static void TestTheAgentIsToldAheadAndFillsTheStepInPlace()
    {
        try
        {
            long seq0 = Events.Since(0).Select(e => (long?)J.Num(e, "seq")).Max() is long m0 ? m0 : 0;
            var started = Engine.StartTutorial(A.Json("""
                {"title":"plan","app":"no_such_app_for_tests","keepOpen":true,"replace":true,"outro":false,"steps":[
                  {"title":"One"},{"title":"Two"},{"id":"pick","title":"Pick your file","later":"the user's own files"},{"title":"Four"}]}
                """));
            A.Eq(4, (int)J.Num(started, "total")!, "the whole path counts from the start");
            A.Eq("pick", J.Str(J.Get(started, "later")?[0], "id"), "and says what is left to fill");
            var ev = Events.Since(seq0);
            A.Check(ev.Any(e => J.Str(e, "event") == "plan_ahead" && J.Str(e, "id") == "pick" && J.Num(e, "stepsAway") == 2), "told two steps ahead");

            // the agent fills it before the user gets there: in place, the plan unchanged
            var filled = Engine.AppendSteps(A.Json("""{"steps":[{"id":"pick","title":"Pick report.txt","targets":[{"anchor":{"element":{"name":"report.txt"}}}]}]}"""));
            A.Eq(4, (int)J.Num(filled, "total")!, "replaced, not appended");
            A.Eq("pick", J.AsStr(J.Get(filled, "replaced")?[0]), "says which");
            A.Check(J.Get(filled, "later") == null, "nothing left to fill");

            // a step written later reached before it is filled: step_needed, and the fill tells how long the user waited
            Engine.StartTutorial(A.Json("""
                {"title":"plan2","app":"no_such_app_for_tests","keepOpen":true,"replace":true,"outro":false,"steps":[
                  {"id":"pick","title":"Pick your file","later":"the user's own files"},{"title":"Two"}]}
                """));
            A.Check(Events.Since(seq0).Any(e => J.Str(e, "event") == "step_needed" && J.Str(e, "id") == "pick"), "the user is on it: step_needed");
            var now = Engine.AppendSteps(A.Json("""{"steps":[{"id":"pick","title":"Pick report.txt"}]}"""));
            A.Eq(2, (int)J.Num(now, "total")!, "filled in place");
            A.Eq("Pick report.txt", J.Str(J.Get(now, "step"), "title"), "the user sees the filled step at once");
            A.Check(J.Num(now, "userWaitedMs") is double, "and the agent learns how long the user waited");
        }
        finally { Engine.StopTutorial(); }
    }
}
