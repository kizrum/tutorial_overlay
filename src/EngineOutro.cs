using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;

namespace TutorialOverlay;

/// <summary>
/// The end of a tutorial: a card that offers to save it to the library, go through it again,
/// or ask the agent for the next lesson. Plus the library tools.
/// </summary>
internal static partial class Engine
{
    /// <summary>"Continue learning" is offered only while an agent is around to answer it.</summary>
    private static readonly TimeSpan AgentRecent = TimeSpan.FromMinutes(30);

    private static bool AgentConnected()
    {
        long ticks = Interlocked.Read(ref McpServer.LastRequestTicks);
        return ticks != 0 && DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) < AgentRecent;
    }

    private static void BuildOutroCard(CardModel card, Tutorial t, int fgPid)
    {
        if (t.HideWhenInactive && t.Defaults.Process != null && !AppActive(Array.Empty<int>(), t.Defaults.Process, fgPid)) return;
        var loc = Settings.Current.Text;
        card.Visible = true;
        var vs = Native.VirtualScreen();
        foreach (var w in Resolver.Popups)
            if (w.Frame.IntersectsWith(vs)) card.Avoid.Add(w.Frame);
        card.State = "outro";
        card.Index = t.Steps.Count - 1;
        card.Total = t.Steps.Count;
        card.Completed = true;           // full progress bar and the green tick next to the header
        card.Title = loc.OutroTitle;
        card.Text = string.Format(loc.OutroText, t.Title.Length > 0 ? t.Title : loc.Tutorial, t.Steps.Count);
        if (t.SavedAs != null) { card.HintTitle = loc.OutroSavedTitle; card.Hint = loc.OutroSavedText; }
        var buttons = new List<CardButton>();
        if (t.SavedAs == null) buttons.Add(new CardButton("outro_save", loc.OutroSave, "primary", "outro_save"));
        if (AgentConnected()) buttons.Add(new CardButton("outro_continue", loc.OutroContinue, t.SavedAs == null ? "secondary" : "primary", "outro_continue"));
        buttons.Add(new CardButton("outro_repeat", loc.OutroRepeat, "secondary", "outro_repeat"));
        buttons.Add(new CardButton("outro_close", loc.OutroClose, "ghost", "outro_close"));
        card.Extra = buttons;
        card.Controls = new HashSet<string> { "stop" };
        var appGeo = t.Defaults.Process != null ? AnchorNow(t.Defaults, out _) : null;
        card.AppFrame = appGeo?.Bounds;
        if (appGeo?.Bounds is RectangleF ab) card.Focus = new PointF(ab.X + ab.Width / 2, ab.Y + ab.Height / 2);
    }

    /// <summary>A choice on the end card (under Sync).</summary>
    private static void Outro(string action, string source)
    {
        var t = _tut;
        if (t == null || !t.OutroOpen) return;
        Events.Emit("outro", new() { ["choice"] = action.Replace("outro_", ""), ["title"] = t.Title, ["source"] = source });
        switch (action)
        {
            case "outro_save":
                try
                {
                    var (name, path) = Tutorials.Save(t.Spec);
                    t.SavedAs = name;
                    Events.Emit("tutorial_saved", new() { ["name"] = name, ["path"] = path, ["title"] = t.Title, ["source"] = source });
                }
                catch (Exception e)
                {
                    Log.Warn("save tutorial: " + e.Message);
                    Events.Emit("tutorial_save_failed", new() { ["error"] = e.Message });
                }
                break;
            case "outro_repeat":
                var spec = (JsonObject)t.Spec.DeepClone();
                var saved = t.SavedAs;
                StartTutorial(spec);
                if (_tut != null) _tut.SavedAs = saved;
                break;
            case "outro_continue":
                // the card waits ("preparing the next step") until the agent appends steps
                t.OutroOpen = false;
                t.State = "waiting";
                Events.Emit("continue_requested", new()
                {
                    ["title"] = t.Title, ["completed"] = t.Steps.Count, ["app"] = t.Defaults.Process, ["savedAs"] = t.SavedAs,
                    ["advice"] = "the user wants to keep learning: append_steps with the next lesson (they continue on this card)",
                });
                break;
            default:
                t.OutroOpen = false;
                ApplySettings(Settings.Global);
                break;
        }
        _stepVersion++;
    }

    // =========================================================== library tools

    public static JsonObject SaveTutorial(JsonNode args)
    {
        lock (Sync)
        {
            JsonObject spec;
            if (J.Unwrap(J.Get(args, "spec")) is JsonObject given) spec = given;
            else if (_tut != null) spec = _tut.Spec;
            else throw new InvalidOperationException("pass spec, or run a tutorial first");
            var (name, path) = Tutorials.Save(spec, J.Str(args, "name"));
            if (_tut != null && ReferenceEquals(spec, _tut.Spec)) { _tut.SavedAs = name; _stepVersion++; }
            Events.Emit("tutorial_saved", new() { ["name"] = name, ["path"] = path, ["title"] = J.Str(spec, "title"), ["source"] = "agent" });
            return new JsonObject { ["name"] = name, ["path"] = path, ["steps"] = J.Items(J.Get(spec, "steps")).Count() };
        }
    }
}
