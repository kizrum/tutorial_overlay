using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TutorialOverlay;

/// <summary>
/// `TutorialOverlay.exe --stdio`: MCP over stdin/stdout, the way MCP clients start a local server themselves
/// (claude mcp add, a Claude Code plugin). It is a thin bridge to the one real server of this user: it
/// starts that server if it is not running (detached, so it outlives the agent), takes the port from server.json
/// and the key from key.txt, and forwards every message — so nobody has to copy a key or know a port.
/// stdout carries only JSON-RPC lines; diagnostics go to stderr.
/// </summary>
internal static class StdioBridge
{
    private static readonly object WriteLock = new();
    private static StreamWriter _out = null!;
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static string? _session;
    private static string _url = "";
    private static string _key = "";
    private static readonly SemaphoreSlim Locating = new(1, 1);
    private static readonly CancellationTokenSource Closing = new();
    private static Task? _stream;

    public static int Run()
    {
        _out = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { NewLine = "\n" };
        try { Locate(false).GetAwaiter().GetResult(); }
        catch (Exception e) { Log.Warn("--stdio: " + e.Message); }    // answered per request below; the client sees why

        using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var pending = new System.Collections.Concurrent.ConcurrentDictionary<Task, byte>();
        string? line;
        while ((line = stdin.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var msg = line;
            // each message on its own: a tool that waits for the user (ask, pick) must not hold up the others
            var t = Task.Run(() => Forward(msg));
            pending[t] = 0;
            _ = t.ContinueWith(done => pending.TryRemove(done, out _));
        }

        // the client closed stdin: it is leaving. Let the server go for the next agent
        Closing.Cancel();
        Task.WaitAll(pending.Keys.ToArray(), TimeSpan.FromSeconds(2));
        if (_session != null)
        {
            try
            {
                using var del = Request(HttpMethod.Delete);
                Http.Send(del, new CancellationTokenSource(2000).Token);
            }
            catch { }
        }
        return 0;
    }

    // ---- finding (or starting) the server

    /// <summary>Port and key of the running server; starts it first when none runs. <paramref name="again"/>: the last one went away.</summary>
    private static async Task Locate(bool again)
    {
        await Locating.WaitAsync();
        try
        {
            var running = Instances.Running();
            if (again && running != null && _url == UrlOf(running.Port)) return;   // another request found it already
            if (running == null)
            {
                // every open session has a bridge, and all of them find no server at once: one starts it, the others
                // wait for that one instead of starting their own (a semaphore: released after an await, on any thread)
                using var gate = new Semaphore(1, 1, @"Local\TutorialOverlay.Launch");
                bool held = gate.WaitOne(TimeSpan.FromSeconds(40));
                try
                {
                    running = Instances.Running();
                    if (running == null)
                    {
                        Log.Info("--stdio: TutorialOverlay is not running, starting it");
                        StartServer();
                        for (int i = 0; i < 150 && running == null; i++)
                        {
                            await Task.Delay(200);
                            running = Instances.Running();
                        }
                    }
                }
                finally { if (held) gate.Release(); }
                if (running == null) throw new InvalidOperationException("TutorialOverlay did not start within 30 s: see its log in %LOCALAPPDATA%\\TutorialOverlay");
            }
            _url = UrlOf(running.Port);
            _key = Environment.GetEnvironmentVariable("TUTORIAL_OVERLAY_KEY") is { Length: > 0 } k ? k : ReadKeyFile();
        }
        finally { Locating.Release(); }
    }

    private static string UrlOf(int port) => $"http://127.0.0.1:{port}/mcp";

    private static string ReadKeyFile()
    {
        var f = Path.Combine(Files.AppDir, "key.txt");
        // the server writes it on its first start: give it a moment
        for (int i = 0; i < 50; i++)
        {
            try { if (File.Exists(f) && File.ReadAllText(f).Trim() is { Length: >= 16 } k) return k; } catch (IOException) { }
            Thread.Sleep(100);
        }
        throw new InvalidOperationException("no key in " + f);
    }

    /// <summary>
    /// Two hops (this → launcher → server, the launcher leaving at once), as for the tray guard: the server ends up
    /// outside this process tree, so a client that kills its MCP server's tree on exit does not take the overlay along,
    /// and it holds none of our pipes. Shell-started, it gets its own hidden console for its event lines.
    /// </summary>
    private static void StartServer()
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        psi.ArgumentList.Add("--launch");
        using var p = Process.Start(psi);
        p?.WaitForExit(10000);
    }

    /// <summary>--launch: start the server on its own, then leave.</summary>
    public static int Launch()
    {
        // an exe unpacked from a downloaded zip (winget) carries the internet mark: started through the shell, Windows
        // asks "publisher could not be verified" every time, and each MCP request that finds no server asks again.
        // This process already runs from that file, so the mark guards nothing: drop it.
        try { File.Delete(Environment.ProcessPath! + ":Zone.Identifier"); } catch { }
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--background")
            {
                UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden,
            })?.Dispose();
            return 0;
        }
        catch { return 1; }
    }

    // ---- forwarding

    private static HttpRequestMessage Request(HttpMethod method, string? body = null)
    {
        var r = new HttpRequestMessage(method, _url);
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        r.Headers.Accept.ParseAdd("application/json");
        r.Headers.Accept.ParseAdd("text/event-stream");
        if (_session != null) r.Headers.Add("Mcp-Session-Id", _session);
        if (body != null) r.Content = new StringContent(body, new UTF8Encoding(false), "application/json");
        return r;
    }

    private static async Task Forward(string line)
    {
        JsonNode? id = null;
        try { id = J.Get(JsonNode.Parse(line), "id")?.DeepClone(); } catch { }
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (_url == "") await Locate(false);
                using var req = Request(HttpMethod.Post, line);
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, Closing.Token);
                if (resp.StatusCode == HttpStatusCode.Unauthorized)
                {
                    Reply(id, "TutorialOverlay refused the key: the server was started with another one (--key or TUTORIAL_OVERLAY_KEY). " +
                              "Set the same TUTORIAL_OVERLAY_KEY for this MCP server, or restart TutorialOverlay without --key.");
                    return;
                }
                if (resp.Headers.TryGetValues("Mcp-Session-Id", out var sid) && sid.FirstOrDefault() is { Length: > 0 } s && _session == null)
                {
                    _session = s;
                    _stream ??= Task.Run(Notifications);
                }
                if (resp.StatusCode == HttpStatusCode.Accepted) return;
                if (resp.Content.Headers.ContentType?.MediaType == "text/event-stream")
                    await ReadEvents(await resp.Content.ReadAsStreamAsync(Closing.Token));
                else
                {
                    var text = (await resp.Content.ReadAsStringAsync(Closing.Token)).Trim();
                    if (text.StartsWith("{") || text.StartsWith("[")) Write(text);
                    else if (!resp.IsSuccessStatusCode) Reply(id, $"TutorialOverlay answered {(int)resp.StatusCode}: {text}");
                }
                return;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException && attempt == 0 && !Closing.IsCancellationRequested)
            {
                // the server went away (quit from the tray, restarted on another port): find or start it, then try once more.
                // The session id stays valid: the server tells agents apart by it, it keeps no other state per session
                Log.Info("--stdio: server not reachable (" + e.Message + "), looking for it again");
                try { await Locate(true); }
                catch (Exception le) { Reply(id, le.Message); return; }
            }
            catch (Exception e)
            {
                Reply(id, "TutorialOverlay is not reachable: " + e.Message);
                return;
            }
        }
    }

    /// <summary>The standing event stream (GET): overlay events as notifications, and the sign that this agent is still here.</summary>
    private static async Task Notifications()
    {
        while (!Closing.IsCancellationRequested)
        {
            try
            {
                using var req = Request(HttpMethod.Get);
                req.Headers.Accept.Clear();
                req.Headers.Accept.ParseAdd("text/event-stream");
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, Closing.Token);
                if (resp.IsSuccessStatusCode) await ReadEvents(await resp.Content.ReadAsStreamAsync(Closing.Token));
                else if (resp.StatusCode is HttpStatusCode.MethodNotAllowed) return;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception) { }
            // dropped (the server restarted): reopen soon, within the server's grace time, so the agent keeps its turn
            try { await Task.Delay(1000, Closing.Token); } catch (OperationCanceledException) { return; }
            try { await Locate(true); } catch { }
        }
    }

    /// <summary>Server-sent events → one JSON line each on stdout (comments and keep-alives are dropped).</summary>
    private static async Task ReadEvents(Stream s)
    {
        using var r = new StreamReader(s, Encoding.UTF8);
        var data = new StringBuilder();
        string? l;
        while ((l = await r.ReadLineAsync(Closing.Token)) != null)
        {
            if (l.Length == 0)
            {
                if (data.Length > 0) { Write(data.ToString()); data.Clear(); }
            }
            else if (l.StartsWith("data:")) data.Append(l.AsSpan(l.StartsWith("data: ") ? 6 : 5));
        }
        if (data.Length > 0) Write(data.ToString());
    }

    private static void Reply(JsonNode? id, string message)
    {
        Log.Warn("--stdio: " + message);
        if (id == null) return;     // a notification: nobody waits for an answer
        Write(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(),
            ["error"] = new JsonObject { ["code"] = -32603, ["message"] = message },
        }.ToJsonString(J.Compact));
    }

    private static void Write(string json)
    {
        lock (WriteLock)
        {
            _out.Write(json.Replace("\r", "").Replace("\n", ""));
            _out.Write('\n');
            _out.Flush();
        }
    }
}
