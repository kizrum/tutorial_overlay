using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TutorialOverlay;

/// <summary>
/// MCP over Streamable HTTP (POST = JSON-RPC request → JSON response, GET = SSE stream of notifications).
/// Bound to 127.0.0.1 only, guarded by a shared key and an Origin check.
/// </summary>
internal sealed class McpServer
{
    private readonly int _port;
    private readonly string _key;
    private WebApplication? _app;
    public event Action? Stopping;
    /// <summary>UTC ticks of the agent's last JSON-RPC request (0 = none yet) — shown in the tray.</summary>
    public static long LastRequestTicks;

    /// <summary>When each MCP session (Mcp-Session-Id) last made a request: whether a tutorial's owner is still around.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> Sessions = new();

    public static TimeSpan? SessionAgo(string session) =>
        Sessions.TryGetValue(session, out var t) ? DateTime.UtcNow - new DateTime(t, DateTimeKind.Utc) : null;

    /// <summary>One client at a time: the agent that holds the server (see <see cref="ClientLease"/>).</summary>
    public static readonly ClientLease Lease = NewLease();

    private static ClientLease NewLease()
    {
        var l = new ClientLease { HolderHasTutorial = s => Engine.Tutorial is { Active: true } t && t.Owner == s };
        l.Changed += (now, was, why) =>
        {
            Log.Info(now != null ? $"agent session {Short(now)} now holds the server ({why})" : $"agent session {Short(was!)} released the server ({why})");
            Events.Emit(now != null ? "client_connected" : "client_released", new JsonObject { ["reason"] = why });
        };
        return l;
    }

    private static string Short(string? s) => s == null ? "(none)" : s.Length > 8 ? s[..8] : s;

    /// <summary>The caller's MCP session; null when the request carries no Mcp-Session-Id.</summary>
    private static string? SessionOf(HttpContext ctx)
    {
        var s = ctx.Request.Headers["Mcp-Session-Id"].ToString();
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    /// <summary>
    /// Every request after initialize must name its session: the server serves one agent at a time and tells them
    /// apart by Mcp-Session-Id, so clients without one (they would all look like the same agent) are turned away.
    /// </summary>
    private const string NoSession = "missing Mcp-Session-Id header: call initialize first and send the session id it returns with every later request";

    private static bool IsInitialize(JsonNode? body) => body switch
    {
        JsonArray a => a.Any(m => J.Str(m, "method") == "initialize"),
        _ => J.Str(body, "method") == "initialize",
    };

    public McpServer(int port, string key) { _port = port; _key = key; }

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();   // stdout is reserved for events
        builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, _port));
        _app = builder.Build();
        _app.Map("/mcp", Handle);
        _app.MapGet("/", () => Results.Text("tutorial-overlay MCP server: POST /mcp\n"));
        _app.Lifetime.ApplicationStopping.Register(() => Stopping?.Invoke());
        await _app.StartAsync();
    }

    public Task StopAsync() => _app?.StopAsync() ?? Task.CompletedTask;

    /// <summary>
    /// The key comes in a header only (Authorization: Bearer or X-Overlay-Key): a key in the URL ends up in logs,
    /// shell history and process lists.
    /// </summary>
    private bool Authorized(HttpContext ctx)
    {
        string? got = null;
        var auth = ctx.Request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) got = auth[7..].Trim();
        if (string.IsNullOrEmpty(got)) got = ctx.Request.Headers["X-Overlay-Key"];
        if (string.IsNullOrEmpty(got)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(got), Encoding.UTF8.GetBytes(_key));
    }

    private static bool OriginOk(HttpContext ctx)
    {
        var origin = ctx.Request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin)) return true;
        return Uri.TryCreate(origin, UriKind.Absolute, out var u) && (u.Host == "127.0.0.1" || u.Host == "localhost" || u.Host == "[::1]");
    }

    private async Task Handle(HttpContext ctx)
    {
        if (!OriginOk(ctx)) { ctx.Response.StatusCode = 403; return; }
        if (!Authorized(ctx))
        {
            // what reached us and was turned away (never the key itself): a client whose DELETE or stream lacks the key shows up here
            Log.Info($"{ctx.Request.Method} /mcp refused: bad or missing key (session {Short(SessionOf(ctx))})");
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsync(ctx.Request.Query.ContainsKey("key")
                ? "the key is no longer accepted in the URL: send it as a header (Authorization: Bearer <key>)"
                : "bad or missing key");
            return;
        }
        switch (ctx.Request.Method)
        {
            case "POST": await HandlePost(ctx); break;
            case "GET": await HandleSse(ctx); break;
            case "DELETE":
                // the client ends its session (MCP clients do on exit): the server is free for the next agent
                if (SessionOf(ctx) is not string ending) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync(NoSession); break; }
                Log.Info($"DELETE /mcp: session {Short(ending)} ends");
                Lease.Release(ending, "session ended");
                ctx.Response.StatusCode = 200;
                break;
            default: ctx.Response.StatusCode = 405; break;
        }
    }

    private async Task HandlePost(HttpContext ctx)
    {
        JsonNode? body;
        try
        {
            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
            body = JsonNode.Parse(await reader.ReadToEndAsync());
        }
        catch (Exception e)
        {
            await WriteJson(ctx, Error(null, -32700, "parse error: " + e.Message));
            return;
        }
        if (SessionOf(ctx) == null && !IsInitialize(body))
        {
            Log.Info($"POST /mcp refused: no Mcp-Session-Id ({J.Str(body, "method") ?? "batch"})");
            ctx.Response.StatusCode = 400;
            await WriteJson(ctx, Error(body is JsonArray ? null : J.Get(body, "id")?.DeepClone(), -32600, NoSession));
            return;
        }
        if (body is JsonArray batch)
        {
            var responses = new JsonArray();
            foreach (var msg in batch)
                if (await Dispatch(msg, ctx) is JsonObject r) responses.Add(r);
            if (responses.Count == 0) { ctx.Response.StatusCode = 202; return; }
            await WriteJson(ctx, responses);
            return;
        }
        // a tool call that waits for the user (ask, ask_area, pick, events long-poll) answers as an SSE stream when
        // the client accepts one: progress notifications (if it sent a progressToken) or keep-alive comments flow
        // while it waits, so neither the client's timeout nor an idle connection cuts it off
        if (LongCall(body) && ctx.Request.Headers.Accept.ToString().Contains("text/event-stream"))
        {
            await StreamCall(body!, ctx);
            return;
        }
        var resp = await Dispatch(body, ctx);
        if (resp == null) { ctx.Response.StatusCode = 202; return; }
        await WriteJson(ctx, resp);
    }

    private static readonly HashSet<string> LongTools = new() { "ask", "ask_area", "pick", "events", "wait_for_turn" };

    private static bool LongCall(JsonNode? msg) =>
        J.Str(msg, "method") == "tools/call" && J.Get(msg, "id") != null && LongTools.Contains(J.Str(J.Get(msg, "params"), "name") ?? "");

    private const int ProgressEveryMs = 10000;

    private async Task StreamCall(JsonNode msg, HttpContext ctx)
    {
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        var token = J.Get(J.Get(J.Get(msg, "params"), "_meta"), "progressToken")?.DeepClone();
        var started = DateTime.UtcNow;
        var call = Dispatch(msg, ctx);
        try
        {
            await ctx.Response.WriteAsync(": waiting for the user\n\n", ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            int n = 0;
            while (await Task.WhenAny(call, Task.Delay(ProgressEveryMs, ctx.RequestAborted)) != call)
            {
                ctx.RequestAborted.ThrowIfCancellationRequested();
                n++;
                string chunk = token == null ? ": still waiting\n\n" : "event: message\ndata: " + new JsonObject
                {
                    ["jsonrpc"] = "2.0", ["method"] = "notifications/progress",
                    ["params"] = new JsonObject
                    {
                        ["progressToken"] = token.DeepClone(), ["progress"] = n,
                        ["message"] = $"waiting for the user ({(int)(DateTime.UtcNow - started).TotalSeconds} s)",
                    },
                }.ToJsonString(J.Compact) + "\n\n";
                await ctx.Response.WriteAsync(chunk, ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            }
            var resp = await call;
            if (resp != null)
            {
                await ctx.Response.WriteAsync("event: message\ndata: " + resp.ToJsonString(J.Compact) + "\n\n", ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    private static async Task WriteJson(HttpContext ctx, JsonNode node)
    {
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync(node.ToJsonString(J.Compact));
    }

    private async Task<JsonObject?> Dispatch(JsonNode? msg, HttpContext ctx)
    {
        var method = J.Str(msg, "method");
        var id = J.Get(msg, "id")?.DeepClone();
        if (method == null) return null;            // a response from the client
        Interlocked.Exchange(ref LastRequestTicks, DateTime.UtcNow.Ticks);
        var session = method == "initialize" ? null : SessionOf(ctx);
        // a batch that carries initialize got past the session check in HandlePost: its other messages still need one
        if (session == null && method != "initialize") return id == null ? null : Error(id, -32600, NoSession);
        if (session != null)
        {
            Sessions[session] = DateTime.UtcNow.Ticks;
            if (Sessions.Count > 200)
                foreach (var old in Sessions.Where(kv => DateTime.UtcNow.Ticks - kv.Value > TimeSpan.FromHours(2).Ticks).Select(kv => kv.Key).ToList())
                    Sessions.TryRemove(old, out _);
        }
        if (session != null) Lease.Seen(session);
        if (id == null) return null;                // a notification (initialized, cancelled, ...)
        var p = J.Get(msg, "params");
        if (method == "tools/call" && session != null)
        {
            var tool = J.Str(p, "name") ?? "";
            if (tool == "wait_for_turn") return Result(id, await WaitForTurn(session, J.Unwrap(J.Get(p, "arguments")), ctx.RequestAborted));
            if (!Lease.TryUse(session, out var busy)) return Result(id, Busy(busy!));
            if (tool == "release") return Result(id, ReleaseTool(session));
        }
        try
        {
            JsonNode result = method switch
            {
                "initialize" => Initialize(p, ctx),
                "ping" => new JsonObject(),
                "tools/list" => new JsonObject { ["tools"] = Tools.List() },
                "tools/call" => await Tools.Call(J.Str(p, "name") ?? "", J.Unwrap(J.Get(p, "arguments")) ?? new JsonObject(), ctx.RequestAborted, session),
                "logging/setLevel" => new JsonObject(),
                "resources/list" => new JsonObject { ["resources"] = new JsonArray() },
                "resources/templates/list" => new JsonObject { ["resourceTemplates"] = new JsonArray() },
                "prompts/list" => new JsonObject { ["prompts"] = new JsonArray() },
                _ => throw new MissingMethodException(method),
            };
            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        }
        catch (MissingMethodException)
        {
            return Error(id, -32601, "method not found: " + method);
        }
        catch (Exception e)
        {
            Log.Warn($"{method} failed: {e}");
            return Error(id, -32603, e.Message);
        }
    }

    private static JsonObject Result(JsonNode? id, JsonNode result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject ToolText(string text, bool error = false)
    {
        var o = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) };
        if (error) o["isError"] = true;
        return o;
    }

    private static string WhenFree =>
        $"it is released when that agent ends its session, calls release, or makes no requests for {(int)ClientLease.IdleTimeout.TotalMinutes} min " +
        $"({(int)ClientLease.TutorialTimeout.TotalMinutes} min while its tutorial runs)";

    private static JsonObject Busy(string who) => ToolText(
        $"busy: TutorialOverlay serves one agent at a time, and it is {who}. Nothing was done. Do not start another TutorialOverlay " +
        $"(one server, one client). Call wait_for_turn to wait for it — {WhenFree} — or tell the user it is in use.", error: true);

    /// <summary>wait_for_turn: waits until no other agent holds the server, then takes it for the caller.</summary>
    private static async Task<JsonObject> WaitForTurn(string session, JsonNode? args, CancellationToken ct)
    {
        int timeout = Math.Clamp(J.Int(args, "timeoutMs") ?? 600000, 1000, 1800000);
        var started = DateTime.UtcNow;
        try
        {
            while (true)
            {
                if (Lease.TryUse(session, out var busy))
                    return ToolText(new JsonObject { ["granted"] = true, ["waitedMs"] = (int)(DateTime.UtcNow - started).TotalMilliseconds }.ToJsonString(J.Compact));
                if ((DateTime.UtcNow - started).TotalMilliseconds >= timeout)
                    return ToolText(new JsonObject { ["granted"] = false, ["busy"] = busy, ["advice"] = "still in use: wait again, or tell the user" }.ToJsonString(J.Compact));
                await Task.Delay(500, ct);
            }
        }
        catch (OperationCanceledException) { return ToolText("{\"granted\":false,\"cancelled\":true}"); }
    }

    private static JsonObject ReleaseTool(string session) =>
        ToolText(Lease.Release(session, "released by its agent")
            ? "{\"released\":true,\"note\":\"another agent may use the overlay now; a running tutorial keeps running for the user\"}"
            : "{\"released\":false}");

    /// <summary>Protocol revisions this server implements, newest first.</summary>
    public static readonly string[] SupportedVersions = { "2025-06-18", "2025-03-26", "2024-11-05" };

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    private static JsonObject Initialize(JsonNode? p, HttpContext ctx)
    {
        ctx.Response.Headers["Mcp-Session-Id"] = Guid.NewGuid().ToString("N");
        // answer with the client's version when we speak it, else with our newest (the client then decides)
        var asked = J.Str(p, "protocolVersion");
        var version = asked != null && Array.IndexOf(SupportedVersions, asked) >= 0 ? asked : SupportedVersions[0];
        return new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false }, ["logging"] = new JsonObject() },
            ["serverInfo"] = new JsonObject { ["name"] = "tutorial-overlay", ["version"] = Program.Version },
            ["instructions"] = Tools.Instructions,
        };
    }

    /// <summary>A holder whose stream dropped may reconnect within this time before the server counts it as gone.</summary>
    private static readonly TimeSpan StreamGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Server → client notifications: every overlay event as notifications/message — to the holder only; another
    /// session's stream stays open with keep-alives (it is how we know that client is still there).
    /// The stream is also the client's pulse: an MCP client keeps it open while it runs, and its operating system
    /// closes the connection when the client exits, even without a DELETE. So while the holder's stream is open the
    /// server stays its own however long the agent is quiet, and once it closes (and is not reopened within a few
    /// seconds) the server is free at once.
    /// </summary>
    private static async Task HandleSse(HttpContext ctx)
    {
        if (!ctx.Request.Headers.Accept.ToString().Contains("text/event-stream")) { ctx.Response.StatusCode = 405; return; }
        if (SessionOf(ctx) is not string session) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync(NoSession); return; }
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        var ch = Channel.CreateBounded<JsonObject>(new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest });
        void OnEvent(JsonObject ev) { if (Lease.Holder == session) ch.Writer.TryWrite(ev); }
        Events.Published += OnEvent;
        Lease.StreamOpened(session);
        Log.Info($"GET /mcp: event stream opened by session {Short(session)}");
        try
        {
            await ctx.Response.WriteAsync(": connected\n\n", ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            while (!ctx.RequestAborted.IsCancellationRequested)
            {
                using var keepAlive = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
                keepAlive.CancelAfter(TimeSpan.FromSeconds(15));
                JsonObject? ev = null;
                try { ev = await ch.Reader.ReadAsync(keepAlive.Token); }
                catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested) { }
                string chunk = ev == null
                    ? ": keep-alive\n\n"
                    : "event: message\ndata: " + new JsonObject
                    {
                        ["jsonrpc"] = "2.0", ["method"] = "notifications/message",
                        ["params"] = new JsonObject { ["level"] = "info", ["logger"] = "tutorial-overlay", ["data"] = ev.DeepClone() },
                    }.ToJsonString(J.Compact) + "\n\n";
                await ctx.Response.WriteAsync(chunk, ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        finally
        {
            Events.Published -= OnEvent;
            Lease.StreamClosed(session);
            Log.Info($"GET /mcp: event stream of session {Short(session)} closed");
            _ = ReleaseUnlessReconnected(session);
        }
    }

    private static async Task ReleaseUnlessReconnected(string session)
    {
        await Task.Delay(StreamGrace);
        Lease.ReleaseIfDisconnected(session, "client disconnected");
    }
}
