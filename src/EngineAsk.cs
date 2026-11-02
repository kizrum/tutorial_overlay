using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TutorialOverlay;

/// <summary>ask / ask_area: the agent asks the user something and waits for the answer.</summary>
internal static partial class Engine
{
    private static TaskCompletionSource<JsonObject>? _ask;
    private static TaskCompletionSource<Sketch?>? _area;
    private static SketchPad? _areaPad;

    /// <summary>Question with answer buttons and/or a text field; returns {choice,label} | {text} | {cancelled} | {timedOut}.</summary>
    public static async Task<JsonObject> Ask(JsonNode args, CancellationToken ct)
    {
        var question = J.Str(args, "question") ?? J.Str(args, "text") ?? throw new ArgumentException("question is required");
        var options = new List<AskOption>();
        int n = 0;
        foreach (var o in J.Items(J.Get(args, "options")))
        {
            n++;
            if (o is JsonValue) { var t = J.AsStr(o) ?? ""; options.Add(new AskOption(t, t, "secondary")); }
            else
            {
                var text = J.Str(o, "text") ?? J.Str(o, "label") ?? J.Str(o, "id") ?? $"#{n}";
                options.Add(new AskOption(J.Str(o, "id") ?? text, text, J.Str(o, "style") ?? "secondary"));
            }
        }
        bool input = J.Bool(args, "input") ?? options.Count == 0;
        if (!input && options.Count == 0) throw new ArgumentException("give options, or input:true for a text answer");
        int timeout = Math.Clamp(J.Int(args, "timeoutMs") ?? 300000, 1000, 1800000);

        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _ask, tcs)?.TrySetResult(new JsonObject { ["cancelled"] = "superseded" });
        Program.OnUi(() => AskWindow.Show(question, J.Str(args, "title"), options, input, J.Str(args, "placeholder"), a => tcs.TrySetResult(a)));
        Events.Emit("ask", new() { ["question"] = question, ["options"] = options.Count, ["input"] = input });

        using (ct.Register(() => tcs.TrySetResult(new JsonObject { ["cancelled"] = "client gone" })))
        {
            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout, CancellationToken.None));
            if (done != tcs.Task) tcs.TrySetResult(new JsonObject { ["timedOut"] = true });
        }
        var answer = tcs.Task.Result;
        if (Interlocked.CompareExchange(ref _ask, null, tcs) == tcs) Program.OnUi(AskWindow.CloseCurrent);
        Events.Emit("answer", new() { ["question"] = question, ["answer"] = answer.DeepClone() });
        return answer;
    }

    /// <summary>
    /// The user circles an area on a frozen screen. Returns the sketch description (bounds, strokes, element at the
    /// centre, image path) and the PNG of the area — or {cancelled} / {timedOut}.
    /// </summary>
    public static async Task<(JsonObject info, byte[]? png)> AskArea(JsonNode args, CancellationToken ct)
    {
        var prompt = J.Str(args, "prompt") ?? J.Str(args, "text") ?? J.Str(args, "question");
        int timeout = Math.Clamp(J.Int(args, "timeoutMs") ?? 180000, 1000, 1800000);
        var tcs = new TaskCompletionSource<Sketch?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _area, tcs)?.TrySetResult(null);
        var sketch = new Sketch();
        Program.OnUi(() =>
        {
            _areaPad?.Cancel();
            _areaPad = SketchPad.Open(sketch, keep => tcs.TrySetResult(keep && !sketch.Empty ? sketch : null), prompt);
        });
        Events.Emit("ask_area", new() { ["prompt"] = prompt });

        string? why = null;
        using (ct.Register(() => { why = "client gone"; tcs.TrySetResult(null); }))
        {
            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout, CancellationToken.None));
            if (done != tcs.Task) { why = "timeout"; tcs.TrySetResult(null); }
        }
        if (Interlocked.CompareExchange(ref _area, null, tcs) == tcs)
            Program.OnUi(() => { _areaPad?.Cancel(); _areaPad = null; });

        var got = tcs.Task.Result;
        if (got == null)
        {
            sketch.Dispose();
            var res = why == "timeout" ? new JsonObject { ["timedOut"] = true } : new JsonObject { ["cancelled"] = why ?? "user (Esc, or nothing drawn)" };
            Events.Emit("area", res.DeepClone().AsObject());
            return (res, null);
        }
        try
        {
            var info = SketchJson(got);
            byte[]? png = J.Str(info, "image") is string path && File.Exists(path) ? File.ReadAllBytes(path) : null;
            Events.Emit("area", new() { ["bounds"] = info["bounds"]?.DeepClone(), ["image"] = J.Str(info, "image") });
            return (info, png);
        }
        finally { got.Dispose(); }
    }
}
