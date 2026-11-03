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
        t.Mascot && Settings.Current.Mascot && t.State == "running" && t.AdvanceAt == null && step.Targets.Count > 0
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
            Waiting = t.State == "waiting",
            StepKey = MascotKey(t),
            Scale = Native.ScaleAt(card.Focus),
        };
        var step = t.State == "running" && !t.OutroOpen ? t.Current : null;
        if (step == null) return m;

        m.Say = step.Say ?? step.Title;
        m.Thinking = step.HelpPending && step.Hint == null;
        if (t.AdvanceAt != null) return m;     // done, the next step is a moment away: stay put
        var target = step.Targets.FirstOrDefault(x => !x.Satisfied && !x.Optional && x.Geo != null)
                  ?? step.Targets.FirstOrDefault(x => !x.Satisfied && x.Geo != null);
        if (target == null)
            // targets that are all off screen right now: hide with them; a step without targets: stay put
            return step.Targets.Any(x => !x.Satisfied && !x.Optional) ? null : m;

        var g = target.Geo!;
        var h = g.Hits.FirstOrDefault();
        m.Target = h == null ? g.Center : h.Circle ? h.C : new PointF(h.Rect.X + h.Rect.Width / 2, h.Rect.Y + h.Rect.Height / 2);
        m.TargetBounds = h == null ? g.TargetBounds : h.Circle ? new RectangleF(h.C.X - h.R, h.C.Y - h.R, 2 * h.R, 2 * h.R) : h.Rect;
        m.Root = g.A.Root;
        m.Scale = g.A.Scale;
        return m;
    }
}
