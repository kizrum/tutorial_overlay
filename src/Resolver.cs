using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Automation;
using UiaCondition = System.Windows.Automation.Condition;

namespace TutorialOverlay;

internal sealed record TopWindow(IntPtr Hwnd, int Pid, string Title, string Class, Rectangle Frame);

internal sealed record AnchorResult(bool Found, IntPtr Root, RectangleF? Rel, int[] Pids, string Reason, DateTime At)
{
    public static AnchorResult Missing(string reason, int[] pids) => new(false, IntPtr.Zero, null, pids, reason, DateTime.UtcNow);
}

/// <summary>
/// Background (MTA) thread that resolves anchors to window handles and UIA rectangles.
/// UIA can be slow, so the UI thread never calls it: it reads the latest <see cref="AnchorResult"/>
/// and adds the window's *current* position, which keeps marks glued to a window being dragged.
/// </summary>
internal static class Resolver
{
    private sealed class Cached
    {
        public AutomationElement? Element;
        public IntPtr Root;
        public DateTime LastSearch;
        public DateTime NextSearch;
        public int Misses;
    }

    private static readonly ConcurrentDictionary<string, AnchorResult> Results = new();
    private static readonly Dictionary<string, Cached> Cache = new();
    private static volatile AnchorSpec[] _wanted = Array.Empty<AnchorSpec>();
    private static volatile string[] _watchedProcesses = Array.Empty<string>();
    private static readonly AutoResetEvent Wake = new(false);
    private static Thread? _thread;
    private static volatile bool _stop;
    private static readonly Dictionary<IntPtr, TopWindow> KnownWindows = new();
    private static bool _windowsBaseline;
    private static string _watchKey = "";
    private static readonly int OwnPid = Environment.ProcessId;

    public static void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "anchor-resolver" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public static void Stop() { _stop = true; Wake.Set(); }

    public static void SetWanted(IEnumerable<AnchorSpec> anchors, IEnumerable<string> processes)
    {
        var list = anchors.Where(a => !a.IsScreen).GroupBy(a => a.Key).Select(g => g.First()).ToArray();
        bool changed = list.Length != _wanted.Length || list.Any(a => !Results.ContainsKey(a.Key));
        _wanted = list;
        _watchedProcesses = processes.Concat(list.Select(a => a.Process).OfType<string>())
            .Select(p => p.ToLowerInvariant()).Distinct().ToArray();
        if (changed) Wake.Set();
    }

    public static AnchorResult? Get(string key) => Results.TryGetValue(key, out var r) ? r : null;

    private static void Loop()
    {
        while (!_stop)
        {
            try { Cycle(); }
            catch (Exception e) { Log.Warn("resolver: " + e.Message); }
            Wake.WaitOne(140);
        }
    }

    private static void Cycle()
    {
        var wanted = _wanted;
        var pidCache = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
        var windows = EnumTopWindows();
        var liveKeys = new HashSet<string>();

        foreach (var a in wanted)
        {
            liveKeys.Add(a.Key);
            AnchorResult r;
            try { r = Resolve(a, windows, pidCache, useCache: true); }
            catch (Exception e) { r = AnchorResult.Missing("error: " + e.Message, Array.Empty<int>()); }
            var prev = Get(a.Key);
            Results[a.Key] = r;
            if (prev != null && prev.Found != r.Found && (a.Path.Count > 0 || a.Window != null || a.Title != null))
                Events.Emit(r.Found ? "anchor_found" : "anchor_lost", new() { ["anchor"] = a.Describe(), ["reason"] = r.Reason });
        }
        foreach (var k in Results.Keys.Where(k => !liveKeys.Contains(k)).ToList()) Results.TryRemove(k, out _);
        lock (Cache) foreach (var k in Cache.Keys.Where(k => !liveKeys.Contains(k)).ToList()) Cache.Remove(k);

        TrackWindows(windows, pidCache);
    }

    /// <summary>Emit window_opened / window_closed for top-level windows of watched processes.</summary>
    private static void TrackWindows(List<TopWindow> windows, Dictionary<string, int[]> pidCache)
    {
        var procs = _watchedProcesses;
        var key = string.Join(",", procs);
        if (key != _watchKey) { _watchKey = key; _windowsBaseline = false; KnownWindows.Clear(); }
        if (procs.Length == 0) return;
        var pids = new Dictionary<int, string>();
        foreach (var p in procs) foreach (var pid in Pids(p, pidCache)) pids[pid] = p;
        var now = windows.Where(w => pids.ContainsKey(w.Pid) && w.Frame.Width > 1 && w.Frame.Height > 1).ToDictionary(w => w.Hwnd);
        if (_windowsBaseline)
        {
            foreach (var w in now.Values.Where(w => !KnownWindows.ContainsKey(w.Hwnd)))
                Events.Emit("window_opened", WinJson(w, pids[w.Pid]));
            foreach (var w in KnownWindows.Values.Where(w => !now.ContainsKey(w.Hwnd)))
                Events.Emit("window_closed", WinJson(w, pids.GetValueOrDefault(w.Pid, "?")));
        }
        KnownWindows.Clear();
        foreach (var kv in now) KnownWindows[kv.Key] = kv.Value;
        _windowsBaseline = true;
    }

    private static System.Text.Json.Nodes.JsonObject WinJson(TopWindow w, string proc) => new()
    {
        ["hwnd"] = $"0x{w.Hwnd.ToInt64():X}", ["title"] = w.Title, ["class"] = w.Class, ["process"] = proc, ["rect"] = J.RectJson(w.Frame),
    };

    // ---------------- resolution ----------------

    public static int[] Pids(string process, Dictionary<string, int[]>? cache = null)
    {
        if (cache != null && cache.TryGetValue(process, out var c)) return c;
        int[] res;
        try
        {
            var ps = Process.GetProcessesByName(process);
            res = ps.Select(p => p.Id).ToArray();
            foreach (var p in ps) p.Dispose();
        }
        catch { res = Array.Empty<int>(); }
        if (cache != null) cache[process] = res;
        return res;
    }

    public static List<TopWindow> EnumTopWindows()
    {
        var list = new List<TopWindow>();
        Native.EnumWindows((h, _) =>
        {
            try
            {
                if (!Native.IsWindowVisible(h) || Native.IsCloaked(h)) return true;
                int pid = Native.ProcessId(h);
                if (pid == OwnPid) return true;
                list.Add(new TopWindow(h, pid, Native.WindowTitle(h), Native.ClassName(h), Native.FrameRect(h)));
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>One-shot synchronous resolution (screenshot / describe) without touching the cache.</summary>
    public static AnchorResult ResolveNow(AnchorSpec a) => Resolve(a, EnumTopWindows(), new(StringComparer.OrdinalIgnoreCase), useCache: false);

    private static AnchorResult Resolve(AnchorSpec a, List<TopWindow> all, Dictionary<string, int[]> pidCache, bool useCache)
    {
        int[] pids = a.Process != null ? Pids(a.Process, pidCache) : Array.Empty<int>();
        if (a.Process != null && pids.Length == 0) return AnchorResult.Missing("process not running", pids);

        IEnumerable<TopWindow> cands = all;
        if (a.Hwnd is long hv) cands = cands.Where(w => w.Hwnd == (IntPtr)hv);
        if (a.Process != null) cands = cands.Where(w => pids.Contains(w.Pid));
        if (a.WindowClass != null) cands = cands.Where(w => w.Class.Contains(a.WindowClass, StringComparison.OrdinalIgnoreCase));
        if (a.Window != null) cands = cands.Where(w => w.Title.Contains(a.Window, StringComparison.OrdinalIgnoreCase));
        var wins = cands.Where(w => w.Frame.Width > 1 && w.Frame.Height > 1)
                        .OrderByDescending(w => (long)w.Frame.Width * w.Frame.Height).ToList();
        if (a.Process == null && a.Hwnd == null && wins.Count > 0)
            pids = wins.Select(w => w.Pid).Distinct().ToArray();
        // minimized windows are not "visible" for EnumTopWindows purposes but still exist: keep them as candidates
        if (wins.Count == 0 && a.Hwnd is long hv2 && Native.IsWindow((IntPtr)hv2))
            wins.Add(new TopWindow((IntPtr)hv2, Native.ProcessId((IntPtr)hv2), Native.WindowTitle((IntPtr)hv2), Native.ClassName((IntPtr)hv2), Native.FrameRect((IntPtr)hv2)));

        if (a.Title != null)
        {
            // "window or pane": a top-level window title, or (apps that draw their own UI) a Window/Pane element name
            var top = wins.FirstOrDefault(w => w.Title.Contains(a.Title, StringComparison.OrdinalIgnoreCase));
            if (top != null && a.Path.Count == 0)
                return new AnchorResult(true, top.Hwnd, null, pids.Length > 0 ? pids : new[] { top.Pid }, "window", DateTime.UtcNow);
            var paneSpec = new List<ElementSpec> { new() { Name = a.Title, Role = "Window|Pane" } };
            paneSpec.AddRange(a.Path);
            return ResolvePath(a, paneSpec, a.Process == null ? wins.Where(w => w.Title.Length >= 0).ToList() : wins, pids, useCache);
        }

        if (a.Path.Count == 0)
        {
            if (wins.Count == 0) return AnchorResult.Missing(a.Window != null ? "window not found" : "no visible window", pids);
            var w = wins[0];
            return new AnchorResult(true, w.Hwnd, null, pids.Length > 0 ? pids : new[] { w.Pid }, "window", DateTime.UtcNow);
        }
        if (wins.Count == 0) return AnchorResult.Missing("no visible window", pids);
        return ResolvePath(a, a.Path, wins, pids, useCache);
    }

    private static AnchorResult ResolvePath(AnchorSpec a, List<ElementSpec> path, List<TopWindow> wins, int[] pids, bool useCache)
    {
        Cached? c = null;
        if (useCache)
        {
            lock (Cache)
                if (!Cache.TryGetValue(a.Key, out c)) Cache[a.Key] = c = new Cached();
            // fast path: re-read the cached element's rectangle (one cross-process call)
            if (c.Element != null && Native.IsWindow(c.Root) && DateTime.UtcNow - c.LastSearch < TimeSpan.FromSeconds(2))
            {
                var rect = SafeRect(c.Element, cached: false);
                if (rect is RectangleF r && r.Width > 0 && r.Height > 0)
                    return Found(c.Root, r, pids);
                c.Element = null;
            }
            if (c.Element == null && DateTime.UtcNow < c.NextSearch)
                return Get(a.Key) is { Found: false } prev ? prev with { Pids = pids } : AnchorResult.Missing("searching", pids);
        }

        foreach (var w in wins)
        {
            var el = FindPath(w.Hwnd, path, out var rect);
            if (el != null && rect is RectangleF r)
            {
                if (c != null) { c.Element = el; c.Root = w.Hwnd; c.LastSearch = DateTime.UtcNow; c.Misses = 0; }
                return Found(w.Hwnd, r, pids);
            }
        }
        if (c != null)
        {
            c.Misses++;
            c.NextSearch = DateTime.UtcNow.AddMilliseconds(Math.Min(250 * c.Misses, 1500));
        }
        return AnchorResult.Missing("element not found", pids);
    }

    private static AnchorResult Found(IntPtr root, RectangleF screenRect, int[] pids)
    {
        var frame = Native.FrameRect(root);
        var rel = new RectangleF(screenRect.X - frame.X, screenRect.Y - frame.Y, screenRect.Width, screenRect.Height);
        return new AnchorResult(true, root, rel, pids.Length > 0 ? pids : new[] { Native.ProcessId(root) }, "element", DateTime.UtcNow);
    }

    // ---------------- UI Automation ----------------

    public static CacheRequest NewCacheRequest()
    {
        var cr = new CacheRequest { TreeScope = TreeScope.Element };
        cr.Add(AutomationElement.NameProperty);
        cr.Add(AutomationElement.BoundingRectangleProperty);
        cr.Add(AutomationElement.ControlTypeProperty);
        cr.Add(AutomationElement.AutomationIdProperty);
        cr.Add(AutomationElement.ClassNameProperty);
        return cr;
    }

    public sealed record ElInfo(string Name, string Role, string AutomationId, string ClassName, RectangleF? Rect);

    /// <summary>Reads element properties; null if the element vanished meanwhile.</summary>
    public static ElInfo? Read(AutomationElement e, bool cached)
    {
        try
        {
            var info = cached ? e.Cached : e.Current;
            return new ElInfo(info.Name ?? "", RoleName(info.ControlType), info.AutomationId ?? "", info.ClassName ?? "", ToRect(info.BoundingRectangle));
        }
        catch (Exception ex) when (IsGone(ex))
        {
            if (cached) return Read(e, false);
            return null;
        }
        catch (InvalidOperationException) when (cached)
        {
            return Read(e, false);  // property was not cached
        }
    }

    private static RectangleF? SafeRect(AutomationElement e, bool cached)
    {
        try { return ToRect(cached ? e.Cached.BoundingRectangle : e.Current.BoundingRectangle); }
        catch (Exception ex) when (IsGone(ex) || ex is InvalidOperationException) { return null; }
    }

    public static bool IsGone(Exception ex) => ex is ElementNotAvailableException or System.Runtime.InteropServices.COMException
        or TimeoutException or ArgumentException or UnauthorizedAccessException;

    public static string RoleName(ControlType? ct)
    {
        if (ct == null) return "";
        var n = ct.ProgrammaticName ?? "";
        return n.StartsWith("ControlType.") ? n[12..] : n;
    }

    public static RectangleF? ToRect(System.Windows.Rect r)
    {
        if (r.IsEmpty || double.IsInfinity(r.Width) || double.IsNaN(r.Width) || r.Width <= 0 || r.Height <= 0) return null;
        return new RectangleF((float)r.X, (float)r.Y, (float)r.Width, (float)r.Height);
    }

    private static readonly Dictionary<string, ControlType> RoleMap = BuildRoles();

    private static Dictionary<string, ControlType> BuildRoles()
    {
        var d = new Dictionary<string, ControlType>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in typeof(ControlType).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            if (f.GetValue(null) is ControlType ct) d[f.Name] = ct;
        return d;
    }

    private static UiaCondition BuildCondition(ElementSpec s)
    {
        var parts = new List<UiaCondition>();
        if (s.Role != null)
        {
            var roles = s.Role.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(r => RoleMap.TryGetValue(r, out var ct) ? ct : null).OfType<ControlType>()
                .Select(ct => (UiaCondition)new PropertyCondition(AutomationElement.ControlTypeProperty, ct)).ToArray();
            if (roles.Length == 1) parts.Add(roles[0]);
            else if (roles.Length > 1) parts.Add(new OrCondition(roles));
        }
        if (s.AutomationId != null) parts.Add(new PropertyCondition(AutomationElement.AutomationIdProperty, s.AutomationId));
        if (s.Name != null && s.NameExact) parts.Add(new PropertyCondition(AutomationElement.NameProperty, s.Name, PropertyConditionFlags.IgnoreCase));
        if (s.ClassName != null) parts.Add(new PropertyCondition(AutomationElement.ClassNameProperty, s.ClassName));
        return parts.Count switch { 0 => UiaCondition.TrueCondition, 1 => parts[0], _ => new AndCondition(parts.ToArray()) };
    }

    private static bool Matches(ElementSpec s, ElInfo i)
    {
        if (i.Rect == null) return false;
        if (s.Name != null && !(s.NameExact ? i.Name.Equals(s.Name, StringComparison.OrdinalIgnoreCase)
                                            : i.Name.Contains(s.Name, StringComparison.OrdinalIgnoreCase))) return false;
        if (s.Role != null && !s.Role.Split('|').Any(r => r.Trim().Equals(i.Role, StringComparison.OrdinalIgnoreCase))) return false;
        if (s.AutomationId != null && !i.AutomationId.Equals(s.AutomationId, StringComparison.Ordinal)) return false;
        if (s.ClassName != null && !i.ClassName.Contains(s.ClassName, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    public static AutomationElement? FindPath(IntPtr hwnd, List<ElementSpec> path, out RectangleF? rect)
    {
        rect = null;
        AutomationElement? cur;
        try { cur = AutomationElement.FromHandle(hwnd); }
        catch (Exception ex) when (IsGone(ex) || ex is InvalidOperationException) { return null; }
        foreach (var seg in path)
        {
            var found = FindOne(cur!, seg);
            if (found == null) return null;
            cur = found.Value.el;
            rect = found.Value.rect;
        }
        return cur;
    }

    private static (AutomationElement el, RectangleF rect)? FindOne(AutomationElement root, ElementSpec s)
    {
        var matches = new List<(AutomationElement el, RectangleF rect)>();
        var cr = NewCacheRequest();
        bool viaFindAll = false;
        try
        {
            AutomationElementCollection coll;
            using (cr.Activate()) coll = root.FindAll(TreeScope.Descendants, BuildCondition(s));
            viaFindAll = true;
            foreach (AutomationElement e in coll)
            {
                var info = Read(e, cached: true);  // each element may vanish independently
                if (info != null && Matches(s, info)) matches.Add((e, info.Rect!.Value));
            }
        }
        catch (Exception ex) when (IsGone(ex) || ex is InvalidOperationException)
        {
            if (!viaFindAll) matches.Clear();
        }
        if (!viaFindAll)
        {
            // provider threw mid-search (an element disappeared): walk manually, guarding every node
            Walk(root, cr, 30, 6000, (e, info, _) => { if (Matches(s, info)) matches.Add((e, info.Rect!.Value)); return true; });
        }
        if (matches.Count == 0) return null;
        if (s.Index is int idx) return idx >= 0 && idx < matches.Count ? matches[idx] : null;
        return s.Pick switch
        {
            "first" => matches[0],
            "last" => matches[^1],
            "smallest" => matches.OrderBy(m => m.rect.Width * m.rect.Height).First(),
            _ => matches.OrderByDescending(m => m.rect.Width * m.rect.Height).First(), // FL: the bigger of the two same-named panes
        };
    }

    /// <summary>Depth-first (pre-order) walk of the control view; every node access is individually guarded.</summary>
    public static int Walk(AutomationElement root, CacheRequest cr, int maxDepth, int maxNodes,
        Func<AutomationElement, ElInfo, int, bool> visit)
    {
        var walker = TreeWalker.ControlViewWalker;
        int count = 0;
        bool stop = false;

        void Children(AutomationElement el, int depth)
        {
            if (depth >= maxDepth || stop) return;
            AutomationElement? child;
            try { child = walker.GetFirstChild(el, cr); }
            catch (Exception ex) when (IsGone(ex) || ex is InvalidOperationException) { return; }
            while (child != null && !stop)
            {
                if (++count > maxNodes) { stop = true; return; }
                var info = Read(child, cached: true);
                if (info != null)
                {
                    if (!visit(child, info, depth + 1)) { stop = true; return; }
                    Children(child, depth + 1);
                }
                try { child = walker.GetNextSibling(child, cr); }
                catch (Exception ex) when (IsGone(ex) || ex is InvalidOperationException) { child = null; }
            }
        }

        Children(root, 0);
        return Math.Min(count, maxNodes);
    }
}
