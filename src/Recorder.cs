using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;

namespace TutorialOverlay;

/// <summary>
/// --record: a picture of the screen (overlay included) for every lesson event and every change of a mark's state,
/// with what happened beside it — a UX run can then be looked at afterwards exactly as the user saw it, instead of
/// being pieced together from the log. Development only; the pictures stay on this computer
/// (%LOCALAPPDATA%\TutorialOverlay\records\&lt;start time&gt;\: NNNN-reason.jpg and index.jsonl).
/// </summary>
internal static class Recorder
{
    public static bool Enabled;
    private static string? _dir;
    private static int _n;
    private static readonly BlockingCollection<(DateTime due, string reason, JsonObject info)> Queue = new(64);

    // events worth a picture: what the user did, what the lesson did in answer, and windows coming and going
    private static readonly string[] Worth =
    {
        "tutorial_started", "step_started", "step_completed", "step_back", "hit", "unhit", "miss", "no_effect",
        "window_opened", "window_closed", "target_pinned", "target_not_found", "target_changed", "help", "hint",
        "prepare_started", "prepare_done", "anchor_found", "anchor_lost", "tutorial_stopped", "tutorial_finished",
    };

    /// <summary>Waits this long after the event: the picture shows its result (the menu that opened).</summary>
    private const int DelayMs = 250;

    public static void Start()
    {
        if (!Enabled) return;
        _dir = Path.Combine(Path.GetDirectoryName(Log.Dir)!, "records", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(_dir);
        Log.Info("recording lesson pictures to " + _dir);
        Events.Published += ev =>
        {
            var type = J.Str(ev, "event") ?? "";
            if (Worth.Contains(type)) Note(type, new JsonObject { ["event"] = ev.DeepClone() });
        };
        new Thread(Loop) { IsBackground = true, Name = "recorder", Priority = ThreadPriority.BelowNormal }.Start();
    }

    /// <summary>Something to take a picture of (an event, a mark changing state), with what to write beside it.</summary>
    public static void Note(string reason, JsonObject info)
    {
        if (!Enabled || _dir == null) return;
        info["marks"] ??= Engine.MarkStates();
        Queue.TryAdd((DateTime.UtcNow.AddMilliseconds(DelayMs), reason, info));
    }

    private static void Loop()
    {
        foreach (var (due, reason, info) in Queue.GetConsumingEnumerable())
        {
            try
            {
                var wait = due - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) Thread.Sleep(wait);
                int n = Interlocked.Increment(ref _n);
                string safe = new string(reason.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray());
                string file = $"{n:0000}-{safe}.jpg";
                using (var shot = ScreenCapture.Capture(Native.VirtualScreen())) Save(shot, Path.Combine(_dir!, file));
                info["n"] = n;
                info["time"] = DateTime.Now.ToString("HH:mm:ss.fff");
                info["reason"] = reason;
                info["file"] = file;
                File.AppendAllText(Path.Combine(_dir!, "index.jsonl"), info.ToJsonString(J.Compact) + Environment.NewLine);
            }
            catch (Exception e) { Log.Warn("recorder: " + e.Message); }
        }
    }

    /// <summary>Half size, JPEG: enough to read the card and see where the marks are, small enough to keep many.</summary>
    private static void Save(Bitmap shot, string path)
    {
        int w = Math.Max(1, shot.Width * 2 / 3), h = Math.Max(1, shot.Height * 2 / 3);
        using var small = new Bitmap(w, h);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(shot, 0, 0, w, h);
        }
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var ps = new EncoderParameters(1);
        ps.Param[0] = new EncoderParameter(Encoder.Quality, 80L);
        small.Save(path, codec, ps);
    }
}
