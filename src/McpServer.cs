using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
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

    private bool Authorized(HttpContext ctx)
    {
        string? got = ctx.Request.Query["key"];
        var auth = ctx.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(got) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) got = auth[7..].Trim();
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
        if (!Authorized(ctx)) { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("bad or missing key"); return; }
        switch (ctx.Request.Method)
        {
            case "POST": await HandlePost(ctx); break;
            case "GET": await HandleSse(ctx); break;
            case "DELETE": ctx.Response.StatusCode = 200; break;
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
        if (body is JsonArray batch)
        {
            var responses = new JsonArray();
            foreach (var msg in batch)
                if (await Dispatch(msg, ctx) is JsonObject r) responses.Add(r);
            if (responses.Count == 0) { ctx.Response.StatusCode = 202; return; }
            await WriteJson(ctx, responses);
            return;
        }
        var resp = await Dispatch(body, ctx);
        if (resp == null) { ctx.Response.StatusCode = 202; return; }
        await WriteJson(ctx, resp);
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
        if (id == null) return null;                // a notification (initialized, cancelled, ...)
        var p = J.Get(msg, "params");
        try
        {
            JsonNode result = method switch
            {
                "initialize" => Initialize(p, ctx),
                "ping" => new JsonObject(),
                "tools/list" => new JsonObject { ["tools"] = Tools.List() },
                "tools/call" => await Tools.Call(J.Str(p, "name") ?? "", J.Unwrap(J.Get(p, "arguments")) ?? new JsonObject(), ctx.RequestAborted),
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

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    private static JsonObject Initialize(JsonNode? p, HttpContext ctx)
    {
        ctx.Response.Headers["Mcp-Session-Id"] = Guid.NewGuid().ToString("N");
        var version = J.Str(p, "protocolVersion") ?? "2025-06-18";
        return new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false }, ["logging"] = new JsonObject() },
            ["serverInfo"] = new JsonObject { ["name"] = "tutorial-overlay", ["version"] = Program.Version },
            ["instructions"] = Tools.Instructions,
        };
    }

    /// <summary>Server → client notifications: every overlay event as notifications/message.</summary>
    private static async Task HandleSse(HttpContext ctx)
    {
        if (!ctx.Request.Headers.Accept.ToString().Contains("text/event-stream")) { ctx.Response.StatusCode = 405; return; }
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        var ch = Channel.CreateBounded<JsonObject>(new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest });
        void OnEvent(JsonObject ev) => ch.Writer.TryWrite(ev);
        Events.Published += OnEvent;
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
        finally { Events.Published -= OnEvent; }
    }
}
