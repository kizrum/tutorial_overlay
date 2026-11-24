using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TutorialOverlay;

/// <summary>
/// Outbound event bus. Every event goes, as one JSON line, to stdout (for Claude Code's Monitor),
/// into a ring buffer (for the `events` long-poll tool) and to MCP SSE subscribers.
/// </summary>
internal static class Events
{
    private const int RingSize = 1000;
    private const int StdoutQueue = 2000;
    private static readonly object Lock = new();
    private static readonly LinkedList<JsonObject> Ring = new();
    private static long _seq;
    private static TaskCompletionSource _pulse = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // stdout is written by its own thread: a reader that stops reading fills the pipe, and a blocking write must
    // never hold up the caller (which usually holds Engine.Sync — frames, input and tool calls would all stop)
    private static readonly BlockingCollection<string> Out = new(new ConcurrentQueue<string>(), StdoutQueue);
    private static long _stdoutDropped;

    /// <summary>Raised outside the lock with a ready-to-send event.</summary>
    public static event Action<JsonObject>? Published;

    public static long LastSeq { get { lock (Lock) return _seq; } }

    /// <summary>Events written to stdout so far were lost because nobody read the pipe.</summary>
    public static long StdoutDropped => Interlocked.Read(ref _stdoutDropped);

    public static void Init()
    {
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        new Thread(() =>
        {
            foreach (var line in Out.GetConsumingEnumerable())
            {
                try { stdout.WriteLine(line); }
                catch { /* reader gone: drop */ }
            }
        }) { IsBackground = true, Name = "stdout-writer" }.Start();
    }

    /// <summary>Waits a moment for queued lines to reach stdout (at exit).</summary>
    public static void Flush(int timeoutMs = 500)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (Out.Count > 0 && Environment.TickCount64 < until) Thread.Sleep(10);
    }

    /// <summary>Publishes an event. The fields of <paramref name="data"/> are moved into the event (data is left empty).</summary>
    /// <summary>Events that mean the user did something in the lesson (clicked, pressed, asked).</summary>
    private static readonly HashSet<string> UserEvents = new()
    {
        "tutorial_started", "hit", "miss", "key_matched", "button", "help", "outro", "continue_requested", "step_completed", "drag_done", "trace_done", "scroll_done",
    };

    /// <summary>UTC ticks of the user's last action in a lesson (0: none yet).</summary>
    public static long LastUserTicks;

    /// <summary>Who made the input being handled on this thread (user | autoplay | other): the events it causes say so.</summary>
    [ThreadStatic] public static string? InputBy;

    public static void Emit(string type, JsonObject? data = null)
    {
        if (InputBy != null && data != null && !data.ContainsKey("by")) data["by"] = InputBy;
        if (UserEvents.Contains(type) && InputBy != "autoplay") Interlocked.Exchange(ref LastUserTicks, DateTime.UtcNow.Ticks);
        JsonObject ev;
        TaskCompletionSource pulse;
        lock (Lock)
        {
            ev = new JsonObject { ["event"] = type, ["seq"] = ++_seq, ["ts"] = DateTime.Now.ToString("HH:mm:ss.fff") };
            if (data != null)
                foreach (var kv in data.ToArray())
                {
                    data.Remove(kv.Key);
                    ev[kv.Key] = kv.Value;
                }
            Ring.AddLast(ev);
            if (Ring.Count > RingSize) Ring.RemoveFirst();
            pulse = _pulse;
            _pulse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        string line = ev.ToJsonString(J.Compact);
        Log.File("event " + line);
        if (!Out.IsAddingCompleted && !Out.TryAdd(line))
        {
            // the pipe is full: drop the oldest queued line so the newest still gets through when reading resumes
            Out.TryTake(out _);
            Out.TryAdd(line);
            if (Interlocked.Increment(ref _stdoutDropped) == 1) Log.Warn("stdout is not being read: oldest events are dropped from it (the events tool still has them)");
        }
        pulse.TrySetResult();
        try { Published?.Invoke(ev); } catch (Exception e) { Log.Warn("event subscriber: " + e.Message); }
    }

    /// <summary>Events after <paramref name="seq"/>, at most <paramref name="max"/> (the newest). <paramref name="gap"/>: some were skipped.</summary>
    public static List<JsonObject> Since(long seq, out bool gap, int max = 200)
    {
        lock (Lock)
        {
            // a "since" from before a server restart (numbering starts again at 1): everything there is, flagged
            bool ahead = seq > _seq;
            if (ahead) seq = 0;
            var res = new List<JsonObject>();
            foreach (var e in Ring)
                if ((long)e["seq"]! > seq) res.Add(e);
            // asked for events older than the ring keeps
            long oldest = Ring.First is { } f ? (long)f.Value["seq"]! : _seq + 1;
            gap = seq >= 0 && seq + 1 < oldest && _seq > seq;
            if (res.Count > max) { res.RemoveRange(0, res.Count - max); gap = true; }
            gap |= ahead;
            return res;
        }
    }

    public static List<JsonObject> Since(long seq, int max = 200) => Since(seq, out _, max);

    private static int _listeners;
    private static long _lastListenTicks;

    /// <summary>UTC ticks when the last events long-poll ended (0: none yet).</summary>
    public static long LastListenTicks => Interlocked.Read(ref _lastListenTicks);

    /// <summary>
    /// An agent follows the lesson: a long-poll is waiting now, or one ended a moment ago (the agent is between two
    /// polls, reacting to what the last one brought). An agent whose turn has ended polls nothing — what the user
    /// asks on the card then reaches it only with the user's next chat message.
    /// </summary>
    public static bool Listening(TimeSpan grace)
    {
        if (Volatile.Read(ref _listeners) > 0) return true;
        long t = Interlocked.Read(ref _lastListenTicks);
        return t != 0 && DateTime.UtcNow.Ticks - t < grace.Ticks;
    }

    /// <summary>
    /// events({wake: 'decisions'}): the events an agent must act on. The rest (hit, step_started / step_completed,
    /// window_opened / closed, target_pinned, check_changed, …) are the overlay doing its job: they do not end the
    /// long-poll, and come with the next event that does (or at the timeout) — none is dropped, so an agent sees the
    /// same stream, only in fewer turns. When in doubt an event is here: a needless turn costs tokens, a missed one
    /// leaves the user waiting.
    /// </summary>
    /// <summary>A filtered long-poll returns once this many events came without waking it.</summary>
    private const int PileMax = 200;

    public static readonly HashSet<string> Decisions = new(StringComparer.Ordinal)
    {
        // the user asks or answers
        "help", "continue_requested", "button", "outro", "answer", "area", "pick", "pick_finished",
        // the lesson needs fixing: a target that is not there, a click that did nothing or went wide, a dialog in the way
        "target_not_found", "no_effect", "miss", "blocked", "prepare_started", "step_skipped",
        // the lesson ran out of steps, ended or was taken over; the server is going away
        "tutorial_waiting", "tutorial_finished", "tutorial_stopped", "tutorial_save_failed", "exit", "already_running",
    };

    /// <summary>
    /// The wake filter of an events call: null (any event ends the long-poll — the old behaviour), or the event
    /// names that do. <paramref name="wake"/> is "decisions" (<see cref="Decisions"/>), "all", or event names
    /// (comma-separated, or an array) that may include "decisions".
    /// </summary>
    public static HashSet<string>? WakeSet(JsonNode? wake)
    {
        if (wake == null) return null;
        var parts = wake is JsonArray arr ? arr.Select(n => J.AsStr(n)) : new[] { J.AsStr(wake) };
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n0 in parts.SelectMany(p => (p ?? "").Split(',')))
        {
            var n = n0.Trim();
            if (n.Length == 0) continue;
            if (n == "all") return null;
            if (n == "decisions") set.UnionWith(Decisions);
            else set.Add(n);
        }
        return set.Count == 0 ? null : set;
    }

    /// <summary>
    /// Long-poll: returns as soon as there is an event after <paramref name="seq"/> — with <paramref name="wakeOn"/>,
    /// one of those names; the events before it come along. At the timeout, whatever came meanwhile.
    /// </summary>
    public static async Task<(List<JsonObject> list, bool gap)> WaitSince(long seq, int timeoutMs, CancellationToken ct, HashSet<string>? wakeOn = null)
    {
        bool poll = timeoutMs > 0;
        if (poll) Interlocked.Increment(ref _listeners);
        try
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                Task wait;
                lock (Lock)
                {
                    // a "since" from before a restart returns at once, whatever the filter: the agent must resync
                    if (seq > _seq) return (Since(seq, out var g0), g0);
                    if (_seq > seq)
                    {
                        if (wakeOn == null) return (Since(seq, out var g1), g1);
                        // a pile of events that did not wake it is returned before the ring would drop its oldest
                        var got = Since(seq, out var g2, RingSize);
                        if (g2 || got.Count >= PileMax || got.Any(e => wakeOn.Contains((string?)e["event"] ?? ""))) return (got, g2);
                    }
                    wait = _pulse.Task;
                }
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero)
                {
                    // the timeout: what came meanwhile (with a filter, events that did not wake it; else nothing)
                    var rest = wakeOn == null ? Since(seq, out var gl) : Since(seq, out gl, RingSize);
                    return (rest, gl);
                }
                await Task.WhenAny(wait, Task.Delay(left, ct));
                ct.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            if (poll)
            {
                Interlocked.Exchange(ref _lastListenTicks, DateTime.UtcNow.Ticks);
                Interlocked.Decrement(ref _listeners);
            }
        }
    }

    /// <summary>Test hook: forget everything.</summary>
    internal static void ResetForTests()
    {
        lock (Lock) { Ring.Clear(); _seq = 0; }
    }
}

/// <summary>
/// Diagnostics go to stderr so stdout stays a clean event stream, and to a log file
/// (%LOCALAPPDATA%\TutorialOverlay\logs\overlay.log, rotated at 2 MB, 3 old files kept) — stderr is lost when
/// the exe is started without a console.
/// </summary>
internal static class Log
{
    public static bool Verbose;
    private static readonly object L = new();
    private static StreamWriter? _file;
    private static string? _path;
    private const long MaxBytes = 2 * 1024 * 1024;
    private const int Keep = 3;

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TutorialOverlay", "logs");

    /// <summary>Opens the log file (name per instance, so a development instance does not write into the main one's).</summary>
    public static void OpenFile(string instance)
    {
        lock (L)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                _path = Path.Combine(Dir, instance.Length == 0 ? "overlay.log" : $"overlay.{instance}.log");
                Rotate();
                _file = new StreamWriter(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
            }
            catch (Exception e)
            {
                _file = null;
                try { Console.Error.WriteLine($"log file not opened: {e.Message}"); } catch { }
            }
        }
    }

    public static void CloseFile()
    {
        lock (L) { try { _file?.Dispose(); } catch { } _file = null; }
    }

    private static void Rotate()
    {
        if (_path == null || !System.IO.File.Exists(_path) || new FileInfo(_path).Length < MaxBytes) return;
        _file?.Dispose();
        _file = null;
        for (int i = Keep; i >= 1; i--)
        {
            var from = i == 1 ? _path : $"{_path}.{i - 1}";
            var to = $"{_path}.{i}";
            if (!System.IO.File.Exists(from)) continue;
            if (System.IO.File.Exists(to)) System.IO.File.Delete(to);
            System.IO.File.Move(from, to);
        }
    }

    public static void Info(string s) => Write("info", s);
    public static void Warn(string s) => Write("warn", s);
    public static void Debug(string s) { if (Verbose) Write("debug", s); }

    /// <summary>Only into the file (events: they already went to stdout).</summary>
    public static void File(string s)
    {
        if (_file == null) return;
        lock (L) ToFile($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {s}");
    }

    private static void Write(string level, string s)
    {
        lock (L)
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {level}: {s}";
            try { Console.Error.WriteLine(line); } catch { }
            ToFile($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {level}: {s}");
        }
    }

    private static void ToFile(string line)
    {
        if (_file == null) return;
        try
        {
            if (_file.BaseStream.Length >= MaxBytes)
            {
                Rotate();
                _file = new StreamWriter(new FileStream(_path!, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
            }
            _file.WriteLine(line);
        }
        catch { }
    }
}
