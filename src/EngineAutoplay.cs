using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace TutorialOverlay;

/// <summary>
/// The autoplay ("Show me"): the overlay does the lesson's steps itself while the user watches — the same targets the
/// marks frame, the same conditions completing the step (a click the app does not answer completes nothing). The
/// user always wins: their hand on the mouse or keyboard takes control back at once, and before every action the
/// autoplay waits until they have been still for <see cref="QuietMs"/>. What cannot be undone (pay, order, delete
/// for good) it never presses: it stops in front of it and the user decides. Every action is logged with who made it.
/// Docs: docs/tutorials.md, "Autoplay".
/// </summary>
internal static partial class Engine
{
    /// <summary>The user must have been still this long before the autoplay acts (their input wins within it).</summary>
    internal const int QuietMs = 2000;
    private const int StartGiveUpMs = 10000;        // still not quiet after this long: control stays with the user
    private const int HeardMs = 1500;               // a click the overlay never heard by then is sent once more
    private const int VerifyMs = 6000;              // a heard click, a key, a gesture: the app's time to answer
    // a target not on screen: it looks as long as the test robot does — quietly for MissingMs (a menu fading in, a
    // slow app), then it asks the agent (a "help" event, source "autoplay") and waits for its answer (retarget, hint,
    // complete_step) AskWaitMs, again after each call of the agent's (it is working on it), up to AskMaxMs; with no
    // agent there it hands the controls back at AloneMs. Meanwhile it goes on looking: the target turning up carries on.
    private const int MissingMs = 8000;             // "I don't see it": the robot's VisibleTimeoutMs
    private const int AskWaitMs = 20000;            // the agent's time to answer: the robot's HelpAnswerTimeoutMs
    private const int AskMaxMs = 60000;
    private const int AloneMs = 20000;              // no agent: the robot's StepTimeoutMs
    private const int LookingNoteMs = 2500;         // the card says "looking for …" after this long
    private const int AppInactiveMs = 3000;
    private const int NoCloseMs = 4000;
    private const int WaitConditionMs = 30000;      // a step that only waits (a download, a window to appear)
    private const int MovingMs = 3000;
    private const int MaxWindowsClosed = 3;
    private const int LoopRuns = 3;                 // the same step done this many times within a minute: going round

    internal sealed class AutoRun
    {
        public string State = "off";   // off | intro | starting | acting | verifying | between | paused | finished
        public bool All;               // to the end of the lesson, else this step only
        public string StartedBy = "user";
        public string? Reason, ReasonArg;
        public bool ResumeAfterStep;   // "Continue" only once the user has done this step themselves
        public Step? PausedOn;
        public long StartedAt, QuietFrom, Watch, ActedAt, StateAt, BetweenUntil, HeardAt, MissedAt, DialogSince, InactiveSince;
        public int Actions, StepsDone, Tries, ClosedWindows, Scrolls;
        public Mark? TriedOn;          // the target Tries counts presses on: each target of a step gets its own
        public long MissingSince, AskedAt;  // the target out of sight since; the agent asked for help at
        public string? MissingName;
        public Step? Step;
        public string? ActKind;        // what it did last (click, key, scroll, …): what "verifying" waits for
        public Mark? ActTarget;
        public string? Pressing;       // the mascot's words while it acts
        public readonly Dictionary<Mark, (PointF c, long since)> Seen = new();
        public readonly Dictionary<Step, List<long>> Runs = new();
        public bool Running => State is "starting" or "acting" or "verifying" or "between";
        public bool On => State is not ("off" or "finished");
    }

    internal static readonly AutoRun Auto = new();
    private static Thread? _autoThread;
    private static readonly AutoResetEvent AutoWake = new(false);

    // =========================================================== commands (card, hotkey, the agent's tool)

    /// <summary>
    /// start_step | start_all | intro_ok | stop | resume | toggle (the hotkey). <paramref name="by"/>: user | agent.
    /// Called under <see cref="Sync"/> (from UserAction) or takes it.
    /// </summary>
    public static JsonObject AutoplayCommand(string action, string by, int? steps = null)
    {
        lock (Sync)
        {
            var t = _tut;
            long now = Environment.TickCount64;
            switch (action)
            {
                case "toggle":
                {
                    // the hotkey's own keys are the user's input: they pause a running autoplay a moment before the
                    // hotkey itself arrives here — that pause is this very press, and it means "stop"
                    bool pausedByThisPress = Auto.State == "paused" && Auto.Reason is "user_took_over" or "user_active" && now - Auto.StateAt < 1500;
                    return AutoplayCommand(Auto.Running || pausedByThisPress ? "stop" : Auto.State == "paused" ? "resume" : "start_step", by);
                }
                case "start_step":
                case "start_all":
                case "start":
                {
                    if (t is not { State: TutorialState.Running } || t.Current == null) throw new InvalidOperationException("no step to show (start_tutorial first)");
                    if (!t.AutoplayAllowed) throw new InvalidOperationException("this lesson is for practice: autoplay:false");
                    Auto.All = action == "start_all" || (action == "start" && (steps == null || steps > 1));
                    Auto.StartedBy = by;
                    // the first time ever, the user reads what is about to happen (their mouse will move) and says go
                    if (by == "user" && !Settings.Global.AutoplayIntroShown) { SetAuto("intro"); break; }
                    Begin(now);
                    break;
                }
                case "intro_ok":
                    Settings.Global.AutoplayIntroShown = true;
                    Settings.Current.AutoplayIntroShown = true;
                    Task.Run(() => Settings.SaveField("autoplayIntroShown", true));
                    if (Auto.State == "intro") Begin(now);
                    break;
                case "intro_cancel":
                    if (Auto.State == "intro") SetAuto("off");
                    break;
                case "resume":
                    if (Auto.State != "paused") break;
                    if (Auto.ResumeAfterStep && ReferenceEquals(t?.Current, Auto.PausedOn) && t?.AdvanceAt == null) break;
                    Begin(now, resumed: true);
                    break;
                case "stop":
                    if (Auto.On) End("stopped", by);
                    break;
                default: throw new ArgumentException("autoplay action: start | stop | resume");
            }
            return AutoplayJson();
        }
    }

    /// <summary>The agent's tool: only on the user's request (the skill says so); the card says the assistant is showing.</summary>
    public static JsonObject AutoplayTool(JsonNode args)
    {
        lock (Sync)
        {
            if (_tut is { Active: true }) RequireOwned();
            var action = (J.Str(args, "action") ?? "start").ToLowerInvariant();
            int? steps = J.Str(args, "steps") is string st && st.Equals("all", StringComparison.OrdinalIgnoreCase) ? int.MaxValue : J.Int(args, "steps");
            if (action == "start") action = steps is > 1 ? "start_all" : "start_step";
            return AutoplayCommand(action, "agent", steps);
        }
    }

    private static void Begin(long now, bool resumed = false)
    {
        Auto.StartedAt = Auto.QuietFrom = now;
        Auto.Reason = Auto.ReasonArg = null;
        Auto.ResumeAfterStep = false;
        Auto.PausedOn = null;
        Auto.Step = null;
        if (!resumed) Auto.StepsDone = Auto.Actions = 0;
        Auto.TriedOn = null;
        Auto.Tries = Auto.ClosedWindows = Auto.Scrolls = 0;
        Auto.Pressing = null;
        Auto.Seen.Clear();
        SetAuto("starting");
        Events.Emit(resumed ? "autoplay_resumed" : "autoplay_started", new() { ["scope"] = Auto.All ? "all" : "step", ["by"] = Auto.StartedBy, ["step"] = _tut?.Index });
        Log.Info($"autoplay {(resumed ? "resumed" : "started")} ({(Auto.All ? "to the end" : "one step")}, by {Auto.StartedBy}) on step {(_tut?.Index ?? -1) + 1}");
        EnsureAutoThread();
        AutoWake.Set();
    }

    private static void SetAuto(string state)
    {
        Auto.State = state;
        Auto.StateAt = Environment.TickCount64;
        _stepVersion++;
    }

    private static void End(string why, string by)
    {
        InputSend.ReleaseAll();
        SetAuto("off");
        Auto.Pressing = null;
        Events.Emit("autoplay_stopped", new() { ["by"] = by, ["via"] = why, ["steps"] = Auto.StepsDone });
        Log.Info($"autoplay stopped ({why}, by {by}) after {Auto.StepsDone} step(s)");
    }

    /// <summary>Control back to the user: the reason on the card, to the agent and in the log.</summary>
    private static void Pause(string reason, string? arg = null, bool afterStep = false, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        InputSend.ReleaseAll();
        var t = _tut;
        Auto.Reason = reason;
        Auto.ReasonArg = arg;
        Auto.ResumeAfterStep = afterStep;
        Auto.PausedOn = t?.Current;
        Auto.Pressing = null;
        SetAuto("paused");
        Events.Emit("autoplay_paused", new() { ["reason"] = reason, ["step"] = t?.Index, ["target"] = arg, ["afterStep"] = afterStep ? true : null });
        Log.Info($"autoplay paused: step {(t?.Index ?? -1) + 1} {reason}{(arg != null ? $" «{arg}»" : "")} (EngineAutoplay.cs:{line})");
    }

    /// <summary>The lesson stopped, ended or was replaced: the autoplay ends with it.</summary>
    private static void AutoLessonGone()
    {
        if (Auto.On && Auto.State != "intro") End("lesson_ended", "app");
        else if (Auto.State == "intro") SetAuto("off");
    }

    /// <summary>A click (hit / miss) the overlay heard from the autoplay's own input.</summary>
    private static void NoteAutoplayInput(string kind)
    {
        if (Events.InputBy != "autoplay") return;
        if (kind == "miss") Auto.MissedAt = Environment.TickCount64;
        else Auto.HeardAt = Environment.TickCount64;
    }

    internal static JsonObject AutoplayJson() => new()
    {
        ["state"] = Auto.State,
        ["scope"] = Auto.State == "off" ? null : Auto.All ? "all" : "step",
        ["by"] = Auto.State == "off" ? null : Auto.StartedBy,
        ["reason"] = Auto.Reason,
        ["target"] = Auto.ReasonArg,
        ["canResume"] = Auto.State == "paused" ? CanResume() : null,
        ["steps"] = Auto.StepsDone,
        ["actions"] = Auto.Actions,
    };

    private static bool CanResume() =>
        Auto.State == "paused" && Auto.Reason != "elevated"
        && !(Auto.ResumeAfterStep && ReferenceEquals(_tut?.Current, Auto.PausedOn) && _tut?.AdvanceAt == null);

    // =========================================================== the worker

    private static void EnsureAutoThread()
    {
        if (_autoThread != null) return;
        InputSend.Stop = () => InputHook.ForeignInputAt > Auto.Watch;
        _autoThread = new Thread(AutoLoop) { IsBackground = true, Name = "autoplay" };
        _autoThread.Start();
    }

    private static void AutoLoop()
    {
        while (true)
        {
            if (!Auto.Running) { AutoWake.WaitOne(500); continue; }
            AutoPlan? act;
            try
            {
                lock (Sync) act = AutoDecide(Environment.TickCount64);
            }
            catch (Exception e) { Log.Warn("autoplay: " + e); lock (Sync) Pause("not_completed"); continue; }
            if (act == null) { Thread.Sleep(40); continue; }
            try { Perform(act); }
            catch (TakenOverException)
            {
                lock (Sync) if (Auto.Running) Pause("user_took_over");
            }
            catch (InputBlockedException e)
            {
                Log.Warn("autoplay: " + e.Message);
                lock (Sync) if (Auto.Running) Pause("input_blocked");
            }
            catch (Exception e)
            {
                Log.Warn("autoplay action failed: " + e);
                lock (Sync) if (Auto.Running) Pause("not_completed");
            }
        }
    }

    /// <summary>
    /// One look at the lesson (under the lock): what to do now — null to wait a moment, or an action to perform
    /// outside the lock. State changes (pause, next step, finished) happen here.
    /// </summary>
    private static AutoPlan? AutoDecide(long now)
    {
        var t = _tut;
        InputSend.Speed = Settings.Global.AutoplaySpeed;      // the tray's setting, at once (not per lesson)
        if (t == null || !t.Active) { AutoLessonGone(); return null; }
        if (t.State == TutorialState.Waiting) return null;     // keepOpen: the agent is writing the next steps

        if (Auto.State == "starting")
        {
            // the hand that pressed "Show me" is still on the mouse: start once it has been still for a moment
            long foreign = InputHook.ForeignInputAt;
            if (foreign > Auto.QuietFrom) Auto.QuietFrom = foreign;
            if (QuietEnough(now, Auto.QuietFrom))
            {
                Auto.Watch = now;
                SetAuto("acting");
                return null;
            }
            if (now - Auto.StartedAt > StartGiveUpMs) Pause("user_active");
            return null;
        }

        // someone else's hands since it began: control goes back to them, nothing more is pressed
        if (InputHook.ForeignInputAt > Auto.Watch)
        {
            Log.Info($"autoplay: input by {InputHook.Name(InputHook.ForeignInputBy)} {now - InputHook.ForeignInputAt} ms ago");
            Pause(Auto.State == "acting" ? "user_took_over" : "user_active");
            return null;
        }

        var s = t.Current;
        if (s == null) return null;
        // the lesson moved on: one more step done (by the autoplay or not); between steps the user reads the card
        if (!ReferenceEquals(s, Auto.Step))
        {
            bool first = Auto.Step == null;
            if (!first)
            {
                Auto.StepsDone++;
                if (!Auto.All)
                {
                    SetAuto("finished");
                    Auto.Pressing = null;
                    Events.Emit("autoplay_finished", new() { ["steps"] = Auto.StepsDone, ["scope"] = "step" });
                    Log.Info("autoplay: the step is done");
                    return null;
                }
            }
            Auto.Step = s;
            Auto.Tries = Auto.ClosedWindows = Auto.Scrolls = 0; Auto.TriedOn = null;
            Auto.HeardAt = Auto.MissedAt = 0;
            Auto.DialogSince = Auto.InactiveSince = 0;
            Auto.MissingSince = Auto.AskedAt = 0; Auto.MissingName = null;
            Auto.Seen.Clear();
            if (!Auto.Runs.TryGetValue(s, out var runs)) Auto.Runs[s] = runs = new List<long>();
            runs.Add(now);
            runs.RemoveAll(x => now - x > 60000);
            if (runs.Count >= LoopRuns) { Pause("loop", s.Title); return null; }
            Auto.BetweenUntil = first ? now : now + ReadMs(s);
            SetAuto(first ? "acting" : "between");
            return null;
        }
        if (t.AdvanceAt != null) return null;                  // completed: the next step is a moment away
        if (Auto.State == "between")
        {
            if (now < Auto.BetweenUntil) return null;
            SetAuto("acting");
        }
        if (Auto.State == "verifying")
        {
            if (!Verify(t, s, now)) return null;
            SetAuto("acting");
        }
        return Gate(t, s, now);
    }

    /// <summary>The user's hands have been off the mouse and keyboard long enough for the autoplay to act.</summary>
    internal static bool QuietEnough(long now, long lastForeign) => now - lastForeign >= QuietMs;

    /// <summary>How long the user reads a new step before the autoplay acts on it: longer for a longer card.</summary>
    internal static int ReadMs(Step s) =>
        Math.Min(6000, InputSend.Ms(Math.Min(5000, 1200 + 25 * ((s.Text?.Length ?? 0) + (s.Why?.Length ?? 0) + s.Title.Length)), 600));

    /// <summary>
    /// After an action, until the step completes (the caller sees the next step) or it is time to try again (true).
    /// A click the overlay heard is never sent again — a second "Checkout" is worse than a pause.
    /// </summary>
    internal static bool Verify(Tutorial? t, Step s, long now)
    {
        long since = now - Auto.ActedAt;
        // a miss is a press beside the target; a drag or a stroke presses where it starts, not on a target
        if (Auto.ActKind is "click" or "close" && Auto.MissedAt >= Auto.ActedAt && Auto.MissedAt > 0) { Pause("missed", Auto.ActTarget?.Label); return false; }
        bool heard = Auto.HeardAt >= Auto.ActedAt && Auto.HeardAt > 0;
        switch (Auto.ActKind)
        {
            case "click":
            case "close":
                // the next target of a checklist, or a menu the click opened: plan again as soon as it was heard
                if (heard && Auto.ActTarget is { } m && m.Satisfied && m.OpensArmedAt == null) return true;
                if (heard)
                {
                    if (Auto.ActTarget?.OpensArmedAt is DateTime armed)
                        return (DateTime.UtcNow - armed).TotalMilliseconds > (Auto.ActTarget.OpensTimeoutMs + 1000) && Fail("not_completed");
                    if (Auto.ActKind == "close") return since > 1200;
                    return since > VerifyMs && Fail("not_completed");
                }
                if (since < HeardMs) return false;
                if (Auto.ActKind == "close") return true;
                if (Auto.Tries < 2) return true;                    // never heard: once more
                return Fail("not_completed");
            case "scroll":
                return since > InputSend.Ms(500, 250);              // plan again: it scrolls until it is done
            case "look":
                return since > 800 && Fail("not_completed");
            case "front":
                return since > 400;
            default:
                return since > VerifyMs && Fail("not_completed");
        }
    }

    private static bool Fail(string reason, string? arg = null, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        Pause(reason, arg ?? Auto.ActTarget?.Label, line: line);
        return false;
    }

    /// <summary>The checks before an action and the step's plan: what to do now, or null (wait / paused).</summary>
    private static AutoPlan? Gate(Tutorial t, Step s, long now)
    {
        var plan = Plan(t, s);
        if (plan.Kind == "pause")
        {
            Pause(plan.Reason!, plan.Arg, afterStep: plan.Reason is "choice" or "cant_do" or "danger" or "password");
            return null;
        }
        // the app in front; the card in front (it was just pressed) is put behind it
        int fg = ForegroundPid();
        if (!StepActive(t, s, fg))
        {
            if (fg == OwnPid && FirstRoot(s) is IntPtr root && root != IntPtr.Zero)
                return new AutoPlan("front") { Root = root };
            if (Auto.InactiveSince == 0) Auto.InactiveSince = now;
            if (now - Auto.InactiveSince > AppInactiveMs) Pause("app_inactive");
            return null;
        }
        Auto.InactiveSince = 0;
        // a window in the way: a modal one is the user's to answer; another one is closed by its close button
        if (BlockingDialogWindow(s) is { } d)
        {
            // the same rule as the user's: the card asks to close it and marks its close button; that cross is
            // "cancel" (a welcome screen, a reminder), never anything that cannot be undone. A modal one without a
            // close button found is the user's to answer
            if (Auto.DialogSince == 0) Auto.DialogSince = now;
            if (CloseSpot(d) is not RectangleF c)
            {
                if (now - Auto.DialogSince > NoCloseMs) Pause(d.modal ? "modal" : "no_close", d.title);
                return null;
            }
            if (Auto.ClosedWindows >= MaxWindowsClosed) { Pause("windows_keep_opening", d.title); return null; }
            return new AutoPlan("close") { At = new PointF(c.X + c.Width / 2, c.Y + c.Height / 2), Name = d.title, Root = d.hwnd };
        }
        Auto.DialogSince = 0;
        if (plan.Kind == "wait")
        {
            if (now - Math.Max(Auto.StateAt, Auto.ActedAt) > (plan.WaitMs > 0 ? plan.WaitMs : WaitConditionMs)) Pause(plan.Reason ?? "not_completed", plan.Arg);
            return null;
        }
        // a target: on screen and standing still
        if (plan.Target is Mark m)
        {
            if (m.Visibility != "visible" || m.Geo == null || m.Geo.Hits.Count == 0)
            {
                Auto.Seen.Remove(m);
                Missing(t, s, m, now);
                return null;
            }
            Auto.MissingSince = 0; Auto.MissingName = null;   // found (again): the agent's question, if any, is answered
            var c = HitCenter(m);
            if (!Auto.Seen.TryGetValue(m, out var seen) || Dist(seen.c, c) > 3) { Auto.Seen[m] = (c, now); seen = (c, now); }
            long still = now - seen.since;
            if (still < InputSend.Ms(300, 150))
            {
                if (now - Math.Max(Auto.StateAt, Auto.ActedAt) > MovingMs + MissingMs) Pause("target_moving", m.Label);
                return null;
            }
            if (m.DangerSeen != null) { PauseDanger(m); return null; }
            plan.At = c;
            plan.Pids = m.Anchor.IsScreen ? null : m.Geo.A.Pids;
            plan.Root = m.Geo.A.Root;
            plan.Name = m.Label ?? m.Anchor.Path.LastOrDefault()?.Name ?? m.Anchor.Find?.Text;
            plan.Way = !s.InMenu ? InputSend.Way.Straight
                     : t.Index > 0 && t.Steps[t.Index - 1].InMenu ? InputSend.Way.AcrossThenDown : InputSend.Way.DownThenAcross;
            plan.Path = m.Geo.Prims.FirstOrDefault(p => p.Kind == PrimKind.Arrow) is { } arrow ? new[] { arrow.A, arrow.B } : null;
        }
        return Resolve(plan, s) ? plan : null;
    }

    /// <summary>The target is not on screen: look, then ask the agent, then hand the controls back.</summary>
    internal static void Missing(Tutorial t, Step s, Mark m, long now)
    {
        long from = Math.Max(Auto.StateAt, Auto.ActedAt);
        if (Auto.MissingSince < from) Auto.MissingSince = from;
        Auto.MissingName = m.Label ?? m.Anchor.Path.LastOrDefault()?.Name ?? m.Anchor.Find?.Text ?? s.Title;
        long gone = now - Auto.MissingSince;
        if (Auto.AskedAt == 0)
        {
            if (AgentState(t) == "gone")
            {
                if (gone > AloneMs) Pause("target_missing", Auto.MissingName);
                return;
            }
            if (gone > MissingMs) AskAgent(t, s, m, now);
            return;
        }
        // asked: the agent has AskWaitMs to answer, counted again from each of its calls (it is working on it)
        long last = Auto.AskedAt;
        long ticks = System.Threading.Interlocked.Read(ref McpServer.LastRequestTicks);
        if (ticks != 0) last = Math.Max(last, now - (DateTime.UtcNow.Ticks - ticks) / TimeSpan.TicksPerMillisecond);
        if (now - Auto.AskedAt > AskMaxMs || now - last > AskWaitMs || AgentState(t) == "gone")
            Pause("target_missing_asked", Auto.MissingName);
    }

    /// <summary>
    /// Like the user's "I can't find it", from the autoplay: the agent gets what the overlay knows about the target
    /// and the step, and the card says it was asked. Nothing goes into the step's conversation: the user asked nothing.
    /// </summary>
    private static void AskAgent(Tutorial t, Step s, Mark m, long now)
    {
        Auto.AskedAt = now;
        Log.Info($"autoplay: step {t.Index + 1}: «{Auto.MissingName}» not on screen for {(now - Auto.MissingSince) / 1000} s, asking the agent");
        Native.GetCursorPos(out var cur);
        Events.Emit("help", new()
        {
            ["reason"] = "cant_find", ["source"] = "autoplay", ["step"] = t.Index, ["id"] = s.Id, ["title"] = s.Title,
            ["heard"] = AgentState(t) == "listening",
            ["target"] = MarkJson(m),
            ["targets"] = new JsonArray(s.Targets.Select(x => (JsonNode)MarkJson(x)).ToArray()),
            ["issue"] = TargetIssue(s),
            ["blockingDialog"] = BlockingDialog(s),
            ["cursor"] = new JsonArray(cur.X, cur.Y),
            ["foreground"] = ForegroundInfo(),
            ["advice"] = $"the autoplay (doing the lesson for the user) can't find this target on screen; it keeps looking " +
                         $"{AskWaitMs / 1000} s more (counted again after each of your calls), then hands the controls back. " +
                         "inspect/screenshot, then retarget the mark (it carries on by itself once the target is shown), " +
                         "or complete_step if the step is already done; hint() is shown to the user",
        });
    }

    private static void PauseDanger(Mark m)
    {
        var loc = Settings.Current.Text;
        var what = m.DangerNote ?? DangerWhat(m.DangerSeen!, loc);
        Pause("danger", m.Label ?? m.DangerSeen!.Word, afterStep: true);
        Auto.ReasonArg = what;
        Log.Info($"autoplay: «{m.Label}» cannot be undone ({m.DangerSeen!.Kind}: {m.DangerSeen.Word}) — left to the user");
    }

    internal static string DangerWhat(Danger.Hit h, Loc loc) => h.Kind switch
    {
        "payment" => loc.DangerPayment,
        "send" => loc.DangerSend,
        "user" => loc.DangerUser,
        _ => loc.DangerIrreversible,
    };

    /// <summary>Where the plan's gestures go on screen now (scroll area, drag ends, the stroke); false: not yet.</summary>
    private static bool Resolve(AutoPlan p, Step s)
    {
        var c = p.Cond;
        switch (p.Kind)
        {
            case "scroll":
                if (c!.Area != null && RegionGeo(c.Area) is { } ar) p.At = ar.c;
                else if (p.Target != null) { /* At set above */ }
                else if (s.Targets.FirstOrDefault(x => x.Geo != null) is { } any) p.At = HitCenter(any);
                else return false;
                if (p.At == default) return false;
                return true;
            case "drag":
                if (RegionGeo(c!.From!) is not { } f || RegionGeo(c.To!) is not { } to) return false;
                p.Points = new List<PointF> { f.c, to.c };
                return true;
            case "trace":
                if (AnchorNow(c!.Anchor!, out _) is not { } ag) return false;
                p.Points = Densify(c.Path.Select(q => new PointF(ag.P.X + q.X, ag.P.Y + q.Y)).ToList(), c.Smooth, 6);
                return true;
            case "key":
            case "look":
                return true;
            default:
                return p.At != default;
        }
    }

    private static IntPtr? FirstRoot(Step s) =>
        s.Targets.Select(m => m.Geo?.A.Root).FirstOrDefault(r => r is IntPtr h && h != IntPtr.Zero);

    private static PointF HitCenter(Mark m)
    {
        var g = m.Geo!;
        var h = g.Hits.FirstOrDefault();
        return h == null ? g.Center : h.Circle ? h.C : new PointF(h.Rect.X + h.Rect.Width / 2, h.Rect.Y + h.Rect.Height / 2);
    }

    private static float Dist(PointF a, PointF b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    // =========================================================== the plan (pure: tests call it)

    /// <summary>One thing the autoplay does next, and where (filled in by <see cref="Gate"/> and <see cref="Resolve"/>).</summary>
    internal sealed class AutoPlan
    {
        public string Kind;            // click | key | scroll | drag | trace | type | look | wait | pause | close | front
        public Mark? Target;
        public Condition? Cond;
        public int Times = 1;          // clicks (count), notches (scroll)
        public int WaitMs;             // a wait's limit (0: WaitConditionMs) — not Times, whose 1 is one click
        public string? Reason, Arg, Text, Button, Name;
        public Chord? Chord;
        public bool Up, Sideways;
        public List<uint> Hold = new();
        public List<Mark> Look = new();
        public PointF At;
        public int[]? Pids;
        public IntPtr Root;
        public InputSend.Way Way;
        public PointF[]? Path;
        public List<PointF> Points = new();
        public AutoPlan(string kind) { Kind = kind; }
        public override string ToString() => Kind + (Target != null ? $" «{Target.Label ?? Target.Id}»" : "") + (Reason != null ? $" ({Reason})" : "");
    }

    /// <summary>
    /// What the step needs done next, from its conditions and targets — no screen, no Windows: the tests call it with
    /// targets whose geometry they set. The first condition it can do wins (all of them in turn for untilMode all).
    /// </summary>
    internal static AutoPlan Plan(Tutorial? t, Step s)
    {
        // a step of the plan not written yet: wait for the agent to fill it (it carries on by itself once it is)
        if (s.Later != null) return new AutoPlan("wait") { Reason = "preparing", WaitMs = 120_000 };
        if (s.Buttons.Count > 0) return new AutoPlan("pause") { Reason = "choice" };
        if (s.AutoplayOff) return new AutoPlan("pause") { Reason = "cant_do", Arg = s.AutoplayNote };
        if (s.Until.Count == 0) return LookAt(s);
        var conds = s.UntilAll ? s.Until.Where(c => !c.Met).ToList() : s.Until;
        AutoPlan? waiting = null;
        foreach (var c in conds)
        {
            switch (c.Type)
            {
                case "all_clicked":
                {
                    var (next, wait) = NextTarget(s);
                    if (next != null) return Click(next);
                    if (wait) waiting ??= new AutoPlan("wait") { Reason = "not_completed" };
                    // all ticked: the step completes on the next frame — wait for it, a moment, not "do it yourself"
                    else if (s.Required.Any()) waiting ??= new AutoPlan("wait") { Reason = "not_completed", WaitMs = VerifyMs };
                    break;
                }
                case "count":
                {
                    var m = s.Required.FirstOrDefault() ?? s.Targets.FirstOrDefault();
                    if (m == null) break;
                    var p = Click(m);
                    p.Times = Math.Max(1, c.Count - m.Hits);
                    return p;
                }
                case "key":
                {
                    var chord = c.Keys[0];
                    if (Danger.OfChord(chord) is { } h) return new AutoPlan("pause") { Reason = "danger", Arg = h.Word };
                    var hover = s.Targets.FirstOrDefault(x => !x.Satisfied);
                    return new AutoPlan("key") { Chord = chord, Target = hover };
                }
                case "scroll":
                {
                    var dir = c.Direction;
                    int notches = c.UntilVisible ? 1 : Math.Max(1, c.Notches - c.ScrollCount);
                    var p = new AutoPlan("scroll")
                    {
                        Cond = c, Times = notches, Up = dir is "up" or "left", Sideways = dir is "left" or "right",
                        Hold = c.Hold.ToList(), Target = c.Area == null ? s.Targets.FirstOrDefault(x => !x.Satisfied) : null,
                    };
                    if (p.Sideways && !p.Hold.Contains(0x10)) p.Hold.Add(0x10);
                    return p;
                }
                case "drag":
                    return new AutoPlan("drag") { Cond = c, Button = c.Button == "any" ? "left" : c.Button, Hold = c.Hold.ToList() };
                case "trace":
                    return new AutoPlan("trace") { Cond = c, Button = c.Button == "any" ? "left" : c.Button, Hold = c.Hold.ToList() };
                case "manual":
                    return LookAt(s);
                case "uia" when c.EqualsText != null && c.Property is "value" or "text" && !c.Not:
                {
                    var field = s.Targets.FirstOrDefault();
                    if (field == null) break;
                    return new AutoPlan("type") { Target = field, Text = c.EqualsText, Cond = c };
                }
                case "appear":
                case "disappear":
                case "uia":
                case "pixel":
                {
                    var (next, wait) = NextTarget(s);
                    if (next != null) return Click(next);
                    if (wait || c.Type is "appear" or "disappear")
                        waiting ??= new AutoPlan("wait") { Reason = "not_completed" };
                    // its targets pressed: the state it waits for comes a moment later (= then "42" on the display)
                    else if (s.Required.Any()) waiting ??= new AutoPlan("wait") { Reason = "not_completed", WaitMs = VerifyMs };
                    break;
                }
            }
        }
        return waiting ?? new AutoPlan("pause") { Reason = "cant_do", Arg = s.AutoplayNote };
    }

    private static AutoPlan Click(Mark m) => new("click") { Target = m, Button = m.Button == "any" ? "left" : m.Button };

    private static AutoPlan LookAt(Step s)
    {
        var p = new AutoPlan("look");
        p.Look.AddRange(s.Targets.Where(x => !x.Satisfied));
        return p;
    }

    /// <summary>
    /// The next target to press: the first required one not done yet, in the spec's order (a sequence shows only that
    /// one anyway). wait: a pressed one is still waiting for the window it opens.
    /// </summary>
    internal static (Mark? next, bool wait) NextTarget(Step s)
    {
        bool wait = false;
        foreach (var m in s.Required)
        {
            if (m.OpensArmedAt != null) { wait = true; if (s.Sequence) return (null, true); continue; }
            if (m.Satisfied) continue;
            return (m, wait);
        }
        return (null, wait);
    }

    // =========================================================== doing it (outside the lock)

    private static void Perform(AutoPlan p)
    {
        Step? step;
        lock (Sync)
        {
            step = _tut?.Current;
            Auto.ActKind = p.Kind;
            Auto.ActTarget = p.Target;
            Auto.Pressing = p.Name is { Length: > 0 } n && p.Kind is "click" or "type" or "close"
                ? string.Format(Settings.Current.Text.MascotPress, n) : null;
        }
        switch (p.Kind)
        {
            case "front":
                Native.SetForegroundWindow(p.Root);
                break;
            case "click":
            case "close":
            {
                if (!SafeAt(p, step)) return;
                if (p.Path is { } path) { InputSend.Follow(new[] { path[0], path[1] }); }
                InputSend.Hover(p.At, p.Way);
                InputSend.Dwell();
                // aim where the target is now, not where it was a moment ago
                for (int i = 0; i < 2 && p.Target != null; i++)
                {
                    PointF now;
                    lock (Sync)
                    {
                        if (!ReferenceEquals(_tut?.Current, step) || p.Target.Geo == null) return;
                        now = HitCenter(p.Target);
                    }
                    if (Dist(now, p.At) <= 4) break;
                    p.At = now;
                    InputSend.MoveTo(now);
                    InputSend.Dwell();
                }
                if (!SafeAt(p, step)) return;
                lock (Sync)
                {
                    if (!ReferenceEquals(_tut?.Current, step) || _tut?.AdvanceAt != null) return;
                    if (p.Kind == "close") Auto.ClosedWindows++;
                }
                InputSend.PressHere(p.Button ?? "left", p.Times);
                Acted(p, $"{(p.Kind == "close" ? "close" : "click")} {p.Button ?? "left"}{(p.Times > 1 ? $" ×{p.Times}" : "")} at {InputSend.LastPress.X},{InputSend.LastPress.Y} → «{p.Name}»");
                break;
            }
            case "key":
            {
                var chord = p.Chord!.Value;
                if (p.Target?.Geo != null)
                {
                    PointF at;
                    lock (Sync) at = HitCenter(p.Target);
                    InputSend.Hover(at);
                }
                InputSend.Dwell();
                // Enter / Space press the focused button: judged by its name, like any target
                if (Danger.PressesButton(chord) && FocusedName() is string focused && Danger.Of(focused) is { } h)
                {
                    lock (Sync) Pause("danger", DangerWhat(h, Settings.Current.Text), afterStep: true);
                    return;
                }
                InputSend.Chord(chord.Mods, chord.Vk);
                Acted(p, $"keys {chord}");
                break;
            }
            case "scroll":
                InputSend.Hover(p.At);
                InputSend.Dwell();
                InputSend.Wheel(p.Times, p.Up, p.Hold);
                lock (Sync)
                {
                    Auto.Scrolls += p.Times;
                    if (Auto.Scrolls > 15) { Pause("target_missing", p.Target?.Label); return; }
                }
                Acted(p, $"scroll {(p.Up ? "up" : "down")}{(p.Sideways ? " sideways" : "")} ×{p.Times} at {(int)p.At.X},{(int)p.At.Y}");
                break;
            case "drag":
            case "trace":
                if (p.Points.Count < 2) return;
                p.At = p.Points[0];
                if (!SafeAt(p, step)) return;
                InputSend.Drag(p.Points, p.Button ?? "left", p.Hold);
                Acted(p, $"{p.Kind} {p.Button} from {(int)p.Points[0].X},{(int)p.Points[0].Y} to {(int)p.Points[^1].X},{(int)p.Points[^1].Y} ({p.Points.Count} points)");
                break;
            case "type":
            {
                if (!SafeAt(p, step)) return;
                InputSend.Hover(p.At);
                InputSend.Dwell();
                InputSend.PressHere("left");
                InputSend.Wait(InputSend.Ms(200, 100));
                InputSend.Chord(Native.MOD_CONTROL, 'A');
                InputSend.Type(p.Text ?? "");
                Acted(p, $"typed {p.Text?.Length ?? 0} chars into «{p.Name}»");
                break;
            }
            case "look":
            {
                foreach (var m in p.Look)
                {
                    PointF at;
                    lock (Sync)
                    {
                        if (!ReferenceEquals(_tut?.Current, step) || m.Geo == null || m.Visibility != "visible") continue;
                        at = HitCenter(m);
                    }
                    InputSend.Hover(at);
                    InputSend.Wait(InputSend.Ms(1200, 500));
                }
                lock (Sync)
                {
                    if (!ReferenceEquals(_tut?.Current, step) || _tut is not { State: TutorialState.Running, AdvanceAt: null }) return;
                    Auto.ActedAt = Environment.TickCount64;
                    Auto.Actions++;
                    SetAuto("verifying");
                    Log.Info($"autoplay: step {_tut.Index + 1} looked at {p.Look.Count} target(s), Done · by autoplay");
                    Complete("autoplay");
                }
                return;
            }
        }
    }

    /// <summary>An action is done: the log, the agent, and "verifying" until the step answers.</summary>
    internal static void Acted(AutoPlan p, string what)
    {
        lock (Sync)
        {
            long now = Environment.TickCount64;
            // from the moment the press went down: the app's answer (the hit) often comes before this line runs, and
            // a hit "before the action" would pass for one never heard — and be pressed again
            Auto.ActedAt = p.Kind is "click" or "close" or "drag" or "trace" && InputSend.LastPressAt > 0 && now - InputSend.LastPressAt < 5000
                ? InputSend.LastPressAt : now;
            Auto.Actions++;
            if (!ReferenceEquals(p.Target, Auto.TriedOn)) { Auto.TriedOn = p.Target; Auto.Tries = 0; }
            if (p.Kind is "click" or "key" or "type") Auto.Tries++;
            var idx = _tut?.Index ?? -1;
            Log.Info($"autoplay: step {idx + 1} {what} · by autoplay");
            Events.Emit("autoplay_action", new()
            {
                ["step"] = idx, ["kind"] = p.Kind, ["target"] = p.Name, ["mark"] = p.Target?.Id,
                ["x"] = (int)p.At.X, ["y"] = (int)p.At.Y, ["by"] = "autoplay",
            });
            if (Auto.State == "acting") SetAuto("verifying");
        }
    }

    /// <summary>
    /// The last look before pressing at <see cref="AutoPlan.At"/>: the window there is the lesson's app (not the
    /// overlay, not another program), not run above our rights, and the control there is not one that cannot be
    /// undone, nor a password box. False: paused (or the overlay was in the way — try again in a moment).
    /// </summary>
    private static bool SafeAt(AutoPlan p, Step? step)
    {
        var (root, rootPid, childPid) = Native.HitWindow(p.At);
        if (rootPid == OwnPid || childPid == OwnPid)
        {
            // the mascot or a label of ours over the spot: it moves away; the next look tries again
            lock (Sync) if (Auto.State == "acting") { Auto.ActedAt = Environment.TickCount64 - VerifyMs; }
            Thread.Sleep(200);
            return false;
        }
        if (p.Pids != null && !p.Pids.Contains(rootPid) && !p.Pids.Contains(childPid) && p.Kind != "close")
        {
            lock (Sync) Pause("wrong_window", p.Name);
            return false;
        }
        if (Native.IsAboveUs(rootPid)) { lock (Sync) Pause("elevated", p.Name); return false; }
        var (name, password) = ElementAt(p.At);
        if (password) { lock (Sync) Pause("password", p.Name, afterStep: true); return false; }
        if (p.Kind != "close" && Danger.Of(name) is { } h)
        {
            lock (Sync)
            {
                if (p.Target != null) p.Target.DangerSeen ??= h;
                var loc = Settings.Current.Text;
                Pause("danger", p.Target?.DangerNote ?? DangerWhat(h, loc), afterStep: true);
            }
            Log.Info($"autoplay: the control under the pointer is «{name}» ({h.Kind}: {h.Word}) — left to the user");
            return false;
        }
        return true;
    }

    /// <summary>The name of the UI Automation element at a point, and whether it is a password box (slow apps: given up after 600 ms).</summary>
    private static (string? name, bool password) ElementAt(PointF p)
    {
        var task = Task.Run(() =>
        {
            try
            {
                var e = AutomationElement.FromPoint(new System.Windows.Point(p.X, p.Y));
                var c = e.Current;
                return (c.Name, c.IsPassword);
            }
            catch { return ((string?)null, false); }
        });
        return task.Wait(600) ? task.Result : (null, false);
    }

    /// <summary>The name of the focused control (what Enter would press).</summary>
    private static string? FocusedName()
    {
        var task = Task.Run(() =>
        {
            try { return AutomationElement.FocusedElement?.Current.Name; }
            catch { return null; }
        });
        return task.Wait(600) ? task.Result : null;
    }

    // =========================================================== danger on the marks (every lesson)

    /// <summary>
    /// Why a target counts as one that cannot be undone: the spec's danger:true, or a name that says so (its name,
    /// the element it is, the text it is found by). Looked at when the step starts; the autoplay adds what it sees
    /// under the pointer.
    /// </summary>
    internal static Danger.Hit? DangerOf(Mark m)
    {
        if (m.Danger) return new Danger.Hit("spec", m.DangerNote ?? m.Label ?? "");
        var names = new List<string?> { m.Label, m.Anchor.Path.LastOrDefault()?.Name, m.Anchor.Find?.Text };
        if (m.Anchor.Find?.Texts is { } texts) names.AddRange(texts);
        return Danger.OfAny(names);
    }

    // =========================================================== the card and the mascot

    private static void AutoCard(CardModel card, Tutorial t, Step s)
    {
        var loc = Settings.Current.Text;
        card.AutoAvailable = t.AutoplayAllowed && t.State == TutorialState.Running && !t.OutroOpen;
        card.AutoState = Auto.State;
        card.AutoCanResume = CanResume();
        card.AutoDanger = Auto.State == "paused" && Auto.Reason == "danger";
        card.AutoNote = null;
        switch (Auto.State)
        {
            case "intro":
                card.AutoTitle = loc.AutoIntroTitle;
                card.AutoNote = loc.AutoIntro;
                break;
            case "starting":
            {
                long left = QuietMs - (Environment.TickCount64 - Auto.QuietFrom);
                card.AutoNote = string.Format(loc.AutoStarting, Math.Max(1, (int)Math.Ceiling(left / 1000.0)));
                card.AutoTitle = null;
                break;
            }
            case "acting":
            case "verifying":
            case "between":
                card.AutoTitle = null;
                long missing = Auto.MissingSince == 0 ? 0 : Environment.TickCount64 - Auto.MissingSince;
                if (Auto.AskedAt != 0 && Auto.MissingName != null) card.AutoNote = string.Format(loc.AutoAskedAgent, Auto.MissingName);
                else if (missing > LookingNoteMs && Auto.MissingName != null) card.AutoNote = string.Format(loc.AutoLooking, Auto.MissingName);
                else card.AutoNote = Auto.StartedBy == "agent" ? loc.AutoActingAgent : string.Format(loc.AutoActing, t.Index + 1, t.Steps.Count);
                break;
            case "paused":
                card.AutoTitle = loc.AutoPausedTitle;
                var text = loc.AutoReasons.TryGetValue(Auto.Reason ?? "", out var r) ? r : Auto.Reason ?? "";
                if (Auto.Reason == "cant_do" && Auto.ReasonArg is { Length: > 0 } note) text = note + " " + text;
                card.AutoNote = string.Format(text, Auto.ReasonArg ?? s.Title);
                break;
            case "finished":
                card.AutoTitle = null;
                card.AutoNote = loc.AutoStepDone;
                break;
        }
    }

    /// <summary>The mascot's outfit: on while the autoplay has the controls, taken off as it hands them back.</summary>
    private static void AutoMascot(MascotInput m)
    {
        m.Autoplay = Auto.State switch
        {
            "starting" or "between" or "verifying" => "on",
            "acting" => "drive",
            _ => "off",
        };
        if (Auto.State == "acting" && Auto.Pressing != null) m.Say = Auto.Pressing;
        else if (Auto.State == "paused" && Environment.TickCount64 - Auto.StateAt < 2500) m.Say = Settings.Current.Text.MascotHandBack;
    }
}
