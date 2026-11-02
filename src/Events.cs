using System;
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
    private static readonly object Lock = new();
    private static readonly LinkedList<JsonObject> Ring = new();
    private static long _seq;
    private static TaskCompletionSource _pulse = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TextWriter _out = TextWriter.Null;

    /// <summary>Raised outside the lock with a ready-to-send event.</summary>
    public static event Action<JsonObject>? Published;

    public static long LastSeq { get { lock (Lock) return _seq; } }

    public static void Init()
    {
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        _out = TextWriter.Synchronized(stdout);
    }

    public static void Emit(string type, JsonObject? data = null)
    {
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
        try { _out.WriteLine(line); } catch { }
        pulse.TrySetResult();
        try { Published?.Invoke(ev); } catch (Exception e) { Log.Warn("event subscriber: " + e.Message); }
    }

    public static List<JsonObject> Since(long seq, int max = 200)
    {
        lock (Lock)
        {
            var res = new List<JsonObject>();
            foreach (var e in Ring)
                if ((long)e["seq"]! > seq) res.Add(e);
            if (res.Count > max) res.RemoveRange(0, res.Count - max);
            return res;
        }
    }

    /// <summary>Long-poll: returns as soon as there is at least one event after <paramref name="seq"/>.</summary>
    public static async Task<List<JsonObject>> WaitSince(long seq, int timeoutMs, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            Task wait;
            lock (Lock)
            {
                if (_seq > seq) return Since(seq);
                wait = _pulse.Task;
            }
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) return new List<JsonObject>();
            await Task.WhenAny(wait, Task.Delay(left, ct));
            ct.ThrowIfCancellationRequested();
        }
    }
}

/// <summary>Diagnostics go to stderr so stdout stays a clean event stream.</summary>
internal static class Log
{
    public static bool Verbose;
    private static readonly object L = new();

    public static void Info(string s) => Write("info", s);
    public static void Warn(string s) => Write("warn", s);
    public static void Debug(string s) { if (Verbose) Write("debug", s); }

    private static void Write(string level, string s)
    {
        lock (L)
        {
            try { Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {level}: {s}"); } catch { }
        }
    }
}
