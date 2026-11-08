using System;
using System.Drawing;
using System.Linq;

namespace TutorialOverlay;

/// <summary>What the mascot sees each frame: the target to point at and the places it must not cover.</summary>
internal static partial class Engine
{
    /// <summary>Set by the mascot (UI thread) once it stands still, fully shown, at the current step's spot.</summary>
    public static volatile string? MascotSettledKey;

    /// <summary>The longest a step's highlight waits for the mascot to arrive.</summary>
    private const double MascotHoldSeconds = 1.5;

    public static string MascotKey(Tutorial t) => $"{t.Index}/{t.State}/{t.OutroOpen}";

    /// <summary>
    /// Keep the step's marks back until the mascot has arrived, so the highlight appears together with it —
    /// only while the mascot is on screen, and never longer than <see cref="MascotHoldSeconds"/>.
    /// </summary>
    private static bool HoldForMascot(Tutorial t, Step step, DateTime now) =>
        // not "was the mascot on screen last frame": on a step's (or a tutorial's) first frame it is not yet,
        // and the highlight would start fading in under a mascot that is only beginning to appear
        t.Mascot && Settings.Current.Mascot && t.State == TutorialState.Running && t.AdvanceAt == null && step.Targets.Count > 0
        && MascotSettledKey != MascotKey(t) && (now - step.EnteredAt).TotalSeconds < MascotHoldSeconds;

    /// <summary>
    /// The mascot follows the card: shown only while the card is (the app in front, a tutorial running, waiting
    /// or on its end card). It points at the first unfinished target that is on screen; a step whose targets
    /// are all hidden (covered, scrolled away) hides it too, like the marks. With nothing to point at (between
    /// steps, a step without targets, the end card) it stays where it is.
    /// </summary>
    private static MascotInput? MascotFor(Tutorial? t, CardModel card)
    {
        if (t == null || !t.Mascot || !Settings.Current.Mascot || !card.Visible) return null;
        var m = new MascotInput
        {
            Avoid = card.Avoid.ToList(),
            Progress = t.Steps.Count == 0 ? 1 : (t.Index + (t.AdvanceAt != null ? 1 : 0)) / (float)t.Steps.Count,
            Outro = t.OutroOpen,
            Waiting = t.State == TutorialState.Waiting,
            StepKey = MascotKey(t),
            Scale = Native.ScaleAt(card.Focus),
            Away = card.Agent == "off",
        };
        // the end card, or waiting for the next steps: nothing on screen to point at but the card itself — it walks over
        // and points at the card (celebrating at the end) instead of staying where the last target was, over the app
        if ((t.OutroOpen || t.State == TutorialState.Waiting) && CardRect is Rectangle cr && cr.Width > 0)
        {
            var at = new PointF(cr.Left + Math.Min(40f, cr.Width / 4f), cr.Top + Math.Min(40f, cr.Height / 4f));
            m.Target = at;
            m.TargetBounds = cr;
            m.Scale = Native.ScaleAt(at);
            m.StepKey += "/card";
            return m;
        }
        var step = t.State == TutorialState.Running && !t.OutroOpen ? t.Current : null;
        if (step == null) return m;

        m.Say = step.Say ?? step.Title;
        // the agent is looking into the user's question: until its answer, whatever the card says meanwhile (its own
        // hint for "can't find"), and only while an agent is there to answer
        m.Thinking = step.HelpPending && card.Agent == "on";
        m.KeyChord = step.Until.FirstOrDefault(c => c.Type == "key" && c.Keys.Count > 0)?.Keys[0];
        m.Keys = m.KeyChord?.ToString();
        if (t.AdvanceAt != null) return m;     // done, the next step is a moment away: stay put
        // the app waits on a dialog: nothing else on the step can happen until it is answered, so go there and say so
        if (BlockingDialogWindow(step) is { } dlg)
        {
            var fr = (RectangleF)dlg.frame;
            m.Target = new PointF(fr.X + fr.Width / 2, fr.Y + fr.Height / 2);
            m.TargetBounds = fr;
            m.Root = dlg.hwnd;
            m.Say = Settings.Current.Text.DialogMark;
            m.StepKey += "/dialog" + dlg.hwnd;      // another dialog is another spot to go to
            m.Scale = Native.ScaleAt(m.Target.Value);
            return m;
        }
        var target = step.Targets.FirstOrDefault(x => !x.Satisfied && !x.Optional && x.Geo != null)
                  ?? step.Targets.FirstOrDefault(x => !x.Satisfied && x.Geo != null);
        // the target is past the edge of its pane: stay where it is (no walking while the user scrolls — the wheel
        // at the edge shows the way) and just say which way and how far
        if (target == null && step.Targets.FirstOrDefault(x => !x.Satisfied && x.ScrollHint is { dir: "up" or "down" or "left" or "right" or "any" }) is { } off)
        {
            var hnt = off.ScrollHint!.Value;
            float sc = Native.ScaleAt(hnt.toward);
            if (hnt.dir == "any")
            {
                // not seen yet, way unknown: go and stand by the pane to scroll, at the wheel. Finding it is a new
                // spot to go to (once), not a ride-along
                var at = ScrollHintPoint("any", hnt.view, hnt.toward, sc);
                m.Target = at;
                m.TargetBounds = Prim.WheelBox(at, sc);
                var loc = Settings.Current.Text;
                m.Say = off.Label is { Length: > 0 } nm ? string.Format(loc.ScrollHere, nm) : loc.ScrollHereAny;
                m.StepKey += "/look";
                m.Scale = sc;
                return m;
            }
            m.Say = ScrollHintText(hnt.dir, hnt.beyond, hnt.view, sc).text;
            m.Scale = sc;
            return m;
        }
        if (target == null)
        {
            // a drag, stroke or scroll to do: the mascot shows the gesture
            if (step.Until.FirstOrDefault(c => !c.Met && c.Type is "drag" or "trace" or "scroll") is { } gest)
            {
                m.Gesture = gest.Type == "scroll" ? "scroll" : "drag";
                m.ScrollUp = gest.Direction == "up";
            }
            // a stroke or scroll step: point at where the path starts / where to scroll
            if (MotionFocus(step) is { } mf)
            {
                m.Target = mf.at;
                m.TargetBounds = mf.keepClear;
                m.Scale = Native.ScaleAt(mf.at);
                // scrolled the app but not over the area: say where (for a couple of seconds after each such turn)
                if (mf.scroll is { } sc && (DateTime.UtcNow - sc.OffAreaAt).TotalSeconds < 2.5)
                {
                    var loc = Settings.Current.Text;
                    var name = sc.Area!.Anchor.Path.LastOrDefault()?.Name ?? sc.Area.Anchor.Title ?? sc.Area.Anchor.Window;
                    m.Say = name is { Length: > 0 } ? string.Format(loc.ScrollOver, name) : loc.ScrollOverAny;
                }
                return m;
            }
            // targets that are all off screen right now: hide with them; a step without targets: stay put
            return step.Targets.Any(x => !x.Satisfied && !x.Optional) ? null : m;
        }

        var g = target.Geo!;
        var h = g.Hits.FirstOrDefault();
        m.Target = h == null ? g.Center : h.Circle ? h.C : new PointF(h.Rect.X + h.Rect.Width / 2, h.Rect.Y + h.Rect.Height / 2);
        m.TargetBounds = h == null ? g.TargetBounds : h.Circle ? new RectangleF(h.C.X - h.R, h.C.Y - h.R, 2 * h.R, 2 * h.R) : h.Rect;
        m.Root = g.A.Root;
        m.Scale = g.A.Scale;
        return m;
    }
}
