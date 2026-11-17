using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;

namespace TutorialOverlay.Tests;

/// <summary>
/// The agent's ear beside the robot: a long-poll on events kept going the way an agent in a lesson keeps it, with a
/// pause after each answer for the agent's own turn (the model reading and answering). Two ears run on the same
/// events — one with wake: 'decisions', one without — so a run shows how many turns each would have cost, how late
/// each heard what it must act on, and that the filtered ear lost no event (its seqs run on without a hole).
/// </summary>
internal sealed class AgentEar
{
    public const int ThinkMs = 2000, PollMs = 15000;

    public readonly string Name;
    private readonly string? _wake;
    private readonly Autopilot.Mcp _mcp;
    private readonly Thread _thread;
    private volatile bool _stop;
    private readonly object _lock = new();
    private long _since, _expect;

    public sealed class Tally
    {
        public int Turns, DecisionTurns, IdleTurns, Events, Decisions, Holes;
        public List<long> LatencyMs = new();
        /// <summary>From a help event to the agent's hint being in the card (the agent's thinking included).</summary>
        public List<long> AnswerMs = new();
        public Tally Clone() => new() { Turns = Turns, DecisionTurns = DecisionTurns, IdleTurns = IdleTurns, Events = Events, Decisions = Decisions, Holes = Holes, LatencyMs = LatencyMs.ToList(), AnswerMs = AnswerMs.ToList() };
        public Tally Minus(Tally o) => new()
        {
            Turns = Turns - o.Turns, DecisionTurns = DecisionTurns - o.DecisionTurns, IdleTurns = IdleTurns - o.IdleTurns,
            Events = Events - o.Events, Decisions = Decisions - o.Decisions, Holes = Holes - o.Holes,
            LatencyMs = LatencyMs.Skip(o.LatencyMs.Count).ToList(), AnswerMs = AnswerMs.Skip(o.AnswerMs.Count).ToList(),
        };
        public string Answers => AnswerMs.Count == 0 ? "" : " · answered help in " + string.Join(", ", AnswerMs.Select(a => a + " ms"));
        public string Latency => LatencyMs.Count == 0 ? "—" : $"median {Median(LatencyMs)} ms, max {LatencyMs.Max()} ms";
        private static long Median(List<long> v) { var s = v.OrderBy(x => x).ToList(); return s[s.Count / 2]; }
    }

    private readonly Tally _t = new();
    public Tally Snapshot() { lock (_lock) return _t.Clone(); }

    /// <summary>This ear is the agent: it answers help with a hint after its own thinking time.</summary>
    private readonly bool _answers;

    public AgentEar(Autopilot.Mcp mcp, string name, string? wake, bool answers = false)
    {
        _mcp = mcp; Name = name; _wake = wake; _answers = answers;
        var now = mcp.Call("events", new JsonObject { ["since"] = long.MaxValue / 2, ["timeoutMs"] = 0 }).Json;
        // a since from "the future" returns everything there is with its lastSeq (nothing yet: since stays as asked)
        _since = J.Items(J.Get(now, "events")).Any() ? (long)(J.Num(now, "lastSeq") ?? 0) : 0;
        _expect = _since + 1;
        _thread = new Thread(Loop) { IsBackground = true, Name = "ear-" + name };
        _thread.Start();
    }

    private void Loop()
    {
        while (!_stop)
        {
            JsonNode? ans;
            try
            {
                var args = new JsonObject { ["since"] = _since, ["timeoutMs"] = PollMs };
                if (_wake != null) args["wake"] = _wake;
                ans = _mcp.Call("events", args).Json;
            }
            catch { Thread.Sleep(500); continue; }
            var received = DateTime.Now;
            var list = J.Items(J.Get(ans, "events")).ToList();
            lock (_lock)
            {
                _t.Turns++;
                if (list.Count == 0) _t.IdleTurns++;
                if (J.Get(ans, "gap") != null) _t.Holes++;
                bool decision = false;
                foreach (var e in list)
                {
                    long seq = (long)(J.Num(e, "seq") ?? 0);
                    if (seq != _expect) _t.Holes++;
                    _expect = seq + 1;
                    _t.Events++;
                    if (!Events.Decisions.Contains(J.Str(e, "event") ?? "")) continue;
                    _t.Decisions++;
                    decision = true;
                    if (EmittedAt(J.Str(e, "ts"), received) is DateTime at) _t.LatencyMs.Add((long)Math.Max(0, (received - at).TotalMilliseconds));
                }
                if (decision) _t.DecisionTurns++;
                if (J.Num(ans, "lastSeq") is double ls) _since = (long)ls;
            }
            // the agent's own turn: it reads what came and answers before it polls again
            if (list.Count > 0) Thread.Sleep(ThinkMs);
            if (!_answers) continue;
            foreach (var h in list.Where(e => J.Str(e, "event") == "help"))
            {
                var target = J.Str(J.Items(J.Get(h, "targets")).FirstOrDefault(), "name") ?? "the framed control";
                try
                {
                    _mcp.Call("hint", new JsonObject
                    {
                        ["text"] = $"It is {target} — framed on screen; do what the step says there.",
                        ["expectIndex"] = J.Num(h, "step") is double st ? (int)st : null,
                    });
                    if (EmittedAt(J.Str(h, "ts"), DateTime.Now) is DateTime at)
                        lock (_lock) _t.AnswerMs.Add((long)(DateTime.Now - at).TotalMilliseconds);
                }
                catch { /* the step moved on: nothing to answer */ }
            }
        }
    }

    private static DateTime? EmittedAt(string? ts, DateTime received)
    {
        if (ts == null || !TimeSpan.TryParseExact(ts, @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture, out var tod)) return null;
        var at = received.Date + tod;
        return at > received.AddMinutes(1) ? at.AddDays(-1) : at;
    }

    /// <summary>Stops polling and waits for the poll in flight (at most one PollMs).</summary>
    public void Stop() { _stop = true; _thread.Join(PollMs + ThinkMs + 5000); }
}
