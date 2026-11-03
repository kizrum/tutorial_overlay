using System;
using System.Threading;

namespace TutorialOverlay;

/// <summary>
/// Second line of defence behind <see cref="ClientLease"/> (which already lets only one agent in at a time).
/// Which agent a tutorial belongs to. Several agents can talk to the one server of a user; without this the
/// second one's start_tutorial silently replaced the first one's tutorial, and the first one's complete_step /
/// goto_step then drove a tutorial that was not its own. A tutorial belongs to the MCP session that started it
/// (Mcp-Session-Id); another session gets a clear refusal unless it asks to take over (replace: true), or the
/// owner has been silent long enough to count as gone. The user (tray menu, card buttons) is never refused.
/// </summary>
internal static partial class Engine
{
    /// <summary>The MCP session of the tool call being handled; null = not an MCP call (the user, the UI).</summary>
    public static readonly AsyncLocal<string?> Caller = new();

    /// <summary>An owner silent this long has most likely gone away: its tutorial may be taken over.</summary>
    private static readonly TimeSpan OwnerTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Calls without an Mcp-Session-Id header (scripts, curl) share this owner name.</summary>
    public const string Anonymous = "anonymous";

    /// <summary>The running tutorial belongs to another agent who is still around (null = no conflict).</summary>
    private static string? ForeignOwner(Tutorial? t)
    {
        var caller = Caller.Value;
        if (caller == null || t is not { Active: true } || t.Owner == null || t.Owner == caller) return null;
        // the server serves one agent at a time: a tutorial of an agent that no longer holds it is free to replace
        if (McpServer.Lease.Holder != t.Owner) return null;
        var ago = McpServer.SessionAgo(t.Owner);
        if (ago == null || ago > OwnerTimeout) return null;
        return $"another agent's tutorial \"{t.Title}\" is running (step {t.Index + 1} of {t.Steps.Count}; its agent was active {(int)ago.Value.TotalSeconds} s ago)";
    }

    /// <summary>start_tutorial: refuse to replace another agent's tutorial unless asked to.</summary>
    private static void CheckStartAllowed(bool replace)
    {
        if (replace || ForeignOwner(_tut) is not string what) return;
        throw new InvalidOperationException(what + ". The user can follow one tutorial at a time. Do not start another TutorialOverlay " +
            "for it (no --instance): wait for tutorial_finished / tutorial_stopped (events tool, or status()), or ask the user, " +
            "then pass replace:true to take over — the other agent gets tutorial_stopped with reason 'replaced'.");
    }

    /// <summary>Tools that drive the running tutorial: only its owner (or anyone once the owner is gone).</summary>
    private static Tutorial RequireOwned()
    {
        var t = Require();
        if (ForeignOwner(t) is string what)
            throw new InvalidOperationException(what + ": it is not yours to control. status() shows it; start your own when it ends " +
                                                "(or start_tutorial with replace:true after asking the user).");
        return t;
    }

    /// <summary>For results: whose tutorial this is, from the caller's point of view.</summary>
    private static string? OwnerLabel(Tutorial t)
    {
        var caller = Caller.Value;
        if (t.Owner == null) return "user";                 // started from the tray menu
        if (caller == null) return null;
        return t.Owner == caller ? "you" : "another agent";
    }
}
