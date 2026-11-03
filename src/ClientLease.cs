using System;
using System.Collections.Generic;

namespace TutorialOverlay;

/// <summary>
/// One server, one client. The overlay serves one agent at a time: the first MCP session (Mcp-Session-Id) that
/// calls a tool holds the server, and every other session is refused until it is released — the user can follow
/// only one tutorial, and two agents driving one screen would fight over it. Another agent waits for its turn
/// (wait_for_turn) instead of starting a second server.
///
/// The server is released when its holder's client goes away — its event stream (GET /mcp) closes and is not
/// reopened within a few seconds; Claude Code sends no DELETE on exit, but its connection closes — when it ends
/// its session (HTTP DELETE), or calls release. While the holder's stream is open it keeps the server however
/// quiet it is. A client without a stream is released after going silent: 3 minutes without a request, or 30
/// while a tutorial it started is running (the user may take a while over a step).
/// </summary>
internal sealed class ClientLease
{
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan TutorialTimeout = TimeSpan.FromMinutes(30);

    private readonly object _lock = new();
    private readonly Func<DateTime> _now;
    private string? _holder;
    private DateTime _since, _seen;

    public ClientLease(Func<DateTime>? now = null) => _now = now ?? (() => DateTime.UtcNow);

    /// <summary>Raised (outside the lock) when the holder changes: (new holder or null, previous holder or null, why).</summary>
    public event Action<string?, string?, string>? Changed;

    public string? Holder { get { lock (_lock) return _holder; } }

    /// <summary>Whether a tutorial started by the holder is running (keeps a silent holder longer). Set by the server.</summary>
    public Func<string, bool> HolderHasTutorial = _ => false;

    /// <summary>The holder, if it still counts as present (expires a silent one on the way).</summary>
    private string? Current(out string? expired)
    {
        expired = null;
        if (_holder == null) return null;
        // its event stream is open: the client is running, however quiet the agent is
        if (_streams.ContainsKey(_holder)) return _holder;
        var limit = HolderHasTutorial(_holder) ? TutorialTimeout : IdleTimeout;
        if (_now() - _seen <= limit) return _holder;
        expired = _holder;
        _holder = null;
        return null;
    }

    /// <summary>
    /// A tool call by <paramref name="session"/>: true if it may go ahead (it holds the server, or now takes it
    /// because nobody does). False: someone else holds it — <paramref name="busy"/> says who and since when.
    /// </summary>
    public bool TryUse(string session, out string? busy)
    {
        string? expired, prev = null, took = null;
        bool ok;
        busy = null;
        lock (_lock)
        {
            var cur = Current(out expired);
            if (cur == null)
            {
                prev = _holder;
                _holder = session;
                _since = _seen = _now();
                took = session;
                ok = true;
            }
            else if (cur == session)
            {
                _seen = _now();
                ok = true;
            }
            else
            {
                busy = $"held by another agent since {_since.ToLocalTime():HH:mm:ss} (its last request {(int)(_now() - _seen).TotalSeconds} s ago)";
                ok = false;
            }
        }
        if (expired != null) Changed?.Invoke(null, expired, "holder went silent");
        if (took != null) Changed?.Invoke(took, null, "first request");
        return ok;
    }

    /// <summary>A request (any method) from the holder keeps it present.</summary>
    public void Seen(string session)
    {
        lock (_lock) if (_holder == session) _seen = _now();
    }

    /// <summary>The session ended or let go: releases the server if it held it.</summary>
    public bool Release(string session, string why)
    {
        lock (_lock)
        {
            if (_holder != session) return false;
            _holder = null;
        }
        Changed?.Invoke(null, session, why);
        return true;
    }

    // open event streams (GET /mcp) per session: an open stream means that client is still running
    private readonly Dictionary<string, int> _streams = new();

    public void StreamOpened(string session)
    {
        lock (_lock) _streams[session] = _streams.GetValueOrDefault(session) + 1;
    }

    public void StreamClosed(string session)
    {
        lock (_lock)
        {
            int n = _streams.GetValueOrDefault(session) - 1;
            if (n > 0) _streams[session] = n; else _streams.Remove(session);
        }
    }

    public bool HasStream(string session) { lock (_lock) return _streams.ContainsKey(session); }

    /// <summary>The holder's stream closed and was not reopened: its client is gone — free the server now.</summary>
    public bool ReleaseIfDisconnected(string session, string why)
    {
        lock (_lock)
        {
            if (_holder != session || _streams.ContainsKey(session)) return false;
            _holder = null;
        }
        Changed?.Invoke(null, session, why);
        return true;
    }

    /// <summary>Whether the server is free right now (expiring a silent holder).</summary>
    public bool IsFree()
    {
        string? expired;
        bool free;
        lock (_lock) free = Current(out expired) == null;
        if (expired != null) Changed?.Invoke(null, expired, "holder went silent");
        return free;
    }
}
