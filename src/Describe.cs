using System;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows.Automation;

namespace TutorialOverlay;

/// <summary>describe(app): top-level windows of a process and their UIA trees with physical rectangles.</summary>
internal static class Describe
{
    public static string Run(JsonNode args, CancellationToken ct)
    {
        string? result = null;
        Exception? error = null;
        var th = new Thread(() =>
        {
            try { result = RunInner(args); }
            catch (Exception e) { error = e; }
        }) { IsBackground = true, Name = "describe" };
        th.SetApartmentState(ApartmentState.MTA);
        th.Start();
        if (!th.Join(TimeSpan.FromSeconds(J.Int(args, "timeoutSec") ?? 25)))
            return "describe timed out (UIA tree too large or app not responding); narrow it with window/element/depth";
        if (error != null) throw error;
        return result ?? "";
    }

    private static string RunInner(JsonNode args)
    {
        var process = AnchorSpec.NormalizeProcess(J.Str(args, "app") ?? J.Str(args, "process"));
        var window = J.Str(args, "window");
        int depth = Math.Clamp(J.Int(args, "depth") ?? 8, 1, 40);
        int max = Math.Clamp(J.Int(args, "maxNodes") ?? 600, 10, 20000);
        bool namedOnly = J.Bool(args, "namedOnly") ?? false;
        var sb = new StringBuilder();

        var windows = Resolver.EnumTopWindows();
        if (process != null)
        {
            var pids = Resolver.Pids(process);
            if (pids.Length == 0) return $"process \"{process}\" is not running";
            windows = windows.Where(w => pids.Contains(w.Pid)).ToList();
        }
        if (window != null) windows = windows.Where(w => w.Title.Contains(window, StringComparison.OrdinalIgnoreCase)).ToList();
        windows = windows.Where(w => w.Frame.Width > 1 && w.Frame.Height > 1).OrderByDescending(w => (long)w.Frame.Width * w.Frame.Height).ToList();

        if (process == null && window == null)
        {
            // no target: just list windows of every app (cheap, good first call)
            sb.AppendLine("Top-level windows (pass app=<process> for UIA trees):");
            foreach (var w in windows.Take(80))
                sb.AppendLine($"  {Engine.ProcName(w.Pid)}  0x{w.Hwnd.ToInt64():X}  \"{Trim(w.Title)}\"  class={w.Class}  {R(w.Frame)}{(Native.IsIconic(w.Hwnd) ? " minimized" : "")}");
            return sb.ToString();
        }

        var path = J.Items(J.Unwrap(J.Get(args, "element"))).Select(ElementSpec.Parse).ToList();
        sb.AppendLine($"Coordinates are physical screen pixels. rel = offset from the window's visible frame top-left.");
        int total = 0;
        foreach (var w in windows)
        {
            sb.AppendLine();
            sb.AppendLine($"window 0x{w.Hwnd.ToInt64():X} \"{Trim(w.Title)}\" class={w.Class} pid={w.Pid} frame={R(w.Frame)}{(Native.IsIconic(w.Hwnd) ? " (minimized)" : "")}");
            AutomationElement? root;
            RectangleF? rootRect = null;
            if (path.Count > 0)
            {
                root = Resolver.FindPath(w.Hwnd, path, out rootRect);
                if (root == null) { sb.AppendLine("  (element path not found in this window)"); continue; }
                sb.AppendLine($"  subtree of {string.Join(" > ", path.Select(p => p.Describe()))} {R(rootRect)}");
            }
            else
            {
                try { root = AutomationElement.FromHandle(w.Hwnd); }
                catch (Exception e) when (Resolver.IsGone(e) || e is InvalidOperationException) { sb.AppendLine("  (UIA unavailable)"); continue; }
            }
            var frame = w.Frame;
            var cr = Resolver.NewCacheRequest();
            int count = Resolver.Walk(root!, cr, depth, Math.Max(1, max - total), (el, info, d) =>
            {
                if (namedOnly && info.Name.Length == 0 && info.AutomationId.Length == 0) return true;
                var line = new StringBuilder();
                line.Append(new string(' ', 2 * d)).Append(info.Role);
                if (info.Name.Length > 0) line.Append(" \"").Append(Trim(info.Name)).Append('"');
                if (info.AutomationId.Length > 0) line.Append(" #").Append(info.AutomationId);
                if (info.ClassName.Length > 0) line.Append(" .").Append(info.ClassName);
                if (info.Rect is RectangleF r)
                    line.Append(' ').Append(R(r)).Append(" rel=(").Append((int)(r.X - frame.X)).Append(',').Append((int)(r.Y - frame.Y)).Append(')');
                else line.Append(" [no rect]");
                sb.AppendLine(line.ToString());
                return true;
            });
            total += count;
            if (total >= max) { sb.AppendLine($"... node limit {max} reached; use window/element/depth to narrow"); break; }
        }
        if (windows.Count == 0) sb.AppendLine("no visible windows matched");
        return sb.ToString();
    }

    private static string Trim(string s)
    {
        s = s.Replace('\n', ' ').Replace('\r', ' ');
        return s.Length > 90 ? s[..87] + "..." : s;
    }

    private static string R(RectangleF? r) => r is RectangleF v ? $"[{(int)v.X},{(int)v.Y} {(int)v.Width}x{(int)v.Height}]" : "[?]";
}
