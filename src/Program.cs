using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows.Forms;

namespace TutorialOverlay;

internal static class Program
{
    public const string Version = "0.2.0";
    private static System.Windows.Forms.Control? _ui;
    private static MascotWindow? _mascot;

    // what this instance runs with — shown in the tray and the settings window
    public static int Port { get; private set; }
    public static bool PortFromArgs { get; private set; }
    public static string Key { get; private set; } = "";
    public static string KeyFrom { get; private set; } = "";
    public static DateTime StartedAt { get; } = DateTime.Now;
    private static string[] _args = Array.Empty<string>();

    /// <summary>Run on the UI thread (windows are created and closed there).</summary>
    public static void OnUi(Action a)
    {
        var ui = _ui;
        if (ui == null || ui.IsDisposed) return;
        try { ui.BeginInvoke(a); } catch (InvalidOperationException) { }
    }

    /// <summary>Global defaults → engine (after start-up and after the settings window saves).</summary>
    public static void ApplyGlobal(bool tutorialRunning)
    {
        var g = Settings.Global;
        Engine.Animate = g.Animate ?? true;
        Engine.HideWhenInactiveDefault = g.HideWhenInactive ?? true;
        // a running tutorial keeps its own lang/accent/hotkey overrides until it ends
        if (!tutorialRunning) Engine.ApplySettings(g);
    }

    /// <summary>Start a fresh instance (it reads the port from config.json) and quit this one.</summary>
    public static void Restart()
    {
        // the new instance must be able to listen before this one gives its port up — else no server at all
        int next = Settings.Global.Port ?? 7788;
        if (next != Port && !Ports.IsFree(next))
        {
            var t = Settings.Global.Text;
            MessageBox.Show(string.Format(t.RestartBusy, next, Ports.Owner(next) ?? "?", Port), "TutorialOverlay", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        // one server per user: let go of the instance lock first, or the new process would find "us" still running.
        // Our tray icon goes now too — otherwise both icons sit in the tray while this one shuts its server down
        TrayIcon.Current?.HideNow();
        ReleaseInstance();
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        for (int i = 0; i < _args.Length; i++)
        {
            if (_args[i] == "--port") { i++; continue; }
            if (_args[i] == "--settings") { if (i + 1 < _args.Length && !_args[i + 1].StartsWith("--")) i++; continue; }
            psi.ArgumentList.Add(_args[i]);
        }
        Process.Start(psi);
        Application.Exit();
    }

    // ---- exactly one running server per user: one tray icon, one client at a time (see ClientLease)

    private static Mutex? _single;

    private static string InstanceFile => Instances.File;

    private static bool ClaimInstance()
    {
        _single = new Mutex(true, "TutorialOverlay.Server", out bool first);
        if (first) return true;
        _single.Dispose();
        _single = null;
        return false;
    }

    private static void ReleaseInstance()
    {
        if (_single == null) return;
        try { _single.ReleaseMutex(); } catch (ApplicationException) { }
        _single.Dispose();
        _single = null;
    }

    /// <summary>Where the running server is: for a second launch, scripts and agents (port, pid).</summary>
    private static void WriteInstanceFile()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(InstanceFile)!);
            Files.WriteAtomic(InstanceFile, new JsonObject
            {
                ["port"] = Port, ["pid"] = Environment.ProcessId, ["version"] = Version,
                ["url"] = $"http://127.0.0.1:{Port}/mcp", ["startedAt"] = StartedAt.ToString("s"),
            }.ToJsonString(J.Pretty));
        }
        catch (Exception e) { Log.Warn("server.json not written: " + e.Message); }
    }

    private static void DeleteInstanceFile()
    {
        try
        {
            // only our own: after a restart the new instance has already written its file
            if (File.Exists(InstanceFile) && J.Int(JsonNode.Parse(File.ReadAllText(InstanceFile)), "pid") == Environment.ProcessId) File.Delete(InstanceFile);
        }
        catch (Exception e) { Log.Debug("server.json not removed: " + e.Message); }
    }

    /// <summary>A second launch: say where the running one is (event for agents, a message for people) and quit.</summary>
    private static int AlreadyRunning()
    {
        // two launches at the same moment: the other one holds the lock but may not have written where it listens
        // yet (it does so once its server is up) — wait for that, or the agent would not know where to connect
        JsonNode? info = null;
        for (int i = 0; i < 100; i++)
        {
            try
            {
                if (File.Exists(InstanceFile) && JsonNode.Parse(File.ReadAllText(InstanceFile)) is JsonNode n
                    && J.Int(n, "pid") is int p && Native.ProcessAlive(p)) { info = n; break; }
            }
            catch { }
            Thread.Sleep(100);
        }
        int? port = J.Int(info, "port"), pid = J.Int(info, "pid");
        Log.Warn($"TutorialOverlay is already running (port {port?.ToString() ?? "?"}, PID {pid?.ToString() ?? "?"}); not starting a second one");
        Events.Emit("already_running", new JsonObject
        {
            ["port"] = port, ["pid"] = pid, ["url"] = J.Str(info, "url"),
            ["advice"] = "connect to this server (same key); there is exactly one per user, and it serves one agent at a time. " +
                         "If a tool answers 'busy', another agent is using it: call wait_for_turn.",
        });
        // started by hand (no one reads stdout): tell the person
        if (!Console.IsOutputRedirected)
            MessageBox.Show(string.Format(Settings.Global.Text.AlreadyRunning, port?.ToString() ?? "?", pid?.ToString() ?? "?"),
                            "TutorialOverlay", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 2;
    }

    /// <summary>Switches capture exclusion of all our windows (screenshot() with/without the overlay).</summary>
    public static void SetCapturable(bool capturable)
    {
        var ui = _ui;
        if (ui == null) return;
        ui.Invoke(() =>
        {
            // the canvas is shown with ShowWindow, so it is not in OpenForms
            var handles = new System.Collections.Generic.HashSet<IntPtr> { ui.Handle };
            if (_mascot is { IsHandleCreated: true } m) handles.Add(m.Handle);
            foreach (System.Windows.Forms.Form f in System.Windows.Forms.Application.OpenForms) handles.Add(f.Handle);
            foreach (var h in handles) Native.SetWindowDisplayAffinity(h, capturable ? 0u : Native.WDA_EXCLUDEFROMCAPTURE);
        });
    }

    [STAThread]
    private static int Main(string[] args)
    {
        // the tray guard (started by the server itself): no windows, no settings, no server — see Instances.Guard
        if (args.Length == 2 && int.TryParse(args[1], out int guarded))
        {
            if (args[0] == "--tray-guard-spawn") return Instances.GuardSpawn(guarded);
            if (args[0] == "--tray-guard") return Instances.Guard(guarded);
        }
        Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Events.Init();
        Settings.LoadGlobal();
        ApplyGlobal(false);
        _args = args;

        int port = Settings.Global.Port ?? 7788;
        string? key = Environment.GetEnvironmentVariable("TUTORIAL_OVERLAY_KEY");
        string? openSettings = null;
        bool stop = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out port) || port is < 1 or > 65535)
                    {
                        Console.Error.WriteLine($"--port: \"{args[i]}\" is not a port number (1–65535)");
                        return 1;
                    }
                    PortFromArgs = true;
                    break;
                case "--port":
                    Console.Error.WriteLine("--port needs a number, e.g. --port 7788");
                    return 1;
                case "--key" when i + 1 < args.Length: key = args[++i]; break;
                case "--no-anim": Engine.Animate = false; break;
                case "--show-inactive": Engine.HideWhenInactiveDefault = false; break;
                case "--hide-from-capture": Settings.Global.HideFromCapture = true; break;
                case "--verbose": Log.Verbose = true; break;
                case "--instance":
                    // a second server beside the first meant a second tray icon and two overlays fighting over the screen
                    Console.Error.WriteLine("--instance was removed: there is exactly one TutorialOverlay per user. " +
                                            "Connect to the running one; another agent waits for its turn (wait_for_turn).");
                    return 1;
                // politely stop the running server (its tray icon goes with it)
                case "--stop": stop = true; break;
                // optional tab name: --settings look | keys
                case "--settings": openSettings = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "server"; break;
                case "--help" or "-h" or "/?":
                    Console.Error.WriteLine("TutorialOverlay [--port 7788] [--key KEY] [--no-anim] [--show-inactive] [--verbose] [--settings [server|look|keys]] [--stop]\n" +
                                            "  --settings: open the settings window right away (on that tab)\n" +
                                            "  one server per user (a second launch only reports where it runs) and one agent at a time\n" +
                                            "  --stop: ask the running server to quit, as its tray menu would\n" +
                                            "  key: --key, env TUTORIAL_OVERLAY_KEY, or %LOCALAPPDATA%\\TutorialOverlay\\key.txt (created on first run)\n" +
                                            "  stdout: one JSON event per line; stderr: diagnostics");
                    return 0;
            }
        }
        if (stop) return Instances.StopCommand();
        key ??= LoadOrCreateKey();
        Port = port;
        Key = key;
        KeyFrom = KeySource(args);

        if (!ClaimInstance()) return AlreadyRunning();
        Log.OpenFile("");
        CleanUpOldFiles();
        _ = Instances.Running();     // removes a server.json left behind by a killed server

        var overlay = new OverlayWindow();
        var card = new CardWindow();
        var mascot = new MascotWindow();
        _ = mascot.Handle;
        _mascot = mascot;
        var buttons = new ButtonHost();
        _ = overlay.Handle;
        _ = card.Handle;
        _ui = overlay;
        Instances.ListenForQuit(() => OnUi(Application.Exit));

        Resolver.Start();
        Finder.Start();
        InputHook.Start();
        var consumer = new Thread(() =>
        {
            foreach (var e in InputHook.Queue.GetConsumingEnumerable())
            {
                try { Engine.HandleInput(e); }
                catch (Exception ex) { Log.Warn("input: " + ex.Message); }
            }
        }) { IsBackground = true, Name = "input-consumer", Priority = ThreadPriority.AboveNormal };
        consumer.Start();

        var server = new McpServer(port, key);
        try { server.StartAsync().GetAwaiter().GetResult(); }
        catch (Exception e)
        {
            Log.Warn($"cannot listen on 127.0.0.1:{port}: {e.Message}");
            ReleaseInstance();
            return 3;
        }
        server.Stopping += () => { try { overlay.BeginInvoke(Application.Exit); } catch { } };
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; try { overlay.BeginInvoke(Application.Exit); } catch { } };

        Log.Info($"TutorialOverlay {Version} listening on http://127.0.0.1:{port}/mcp (key from {KeyFrom})");
        using var tray = new TrayIcon();
        // if this process is killed, the guard takes its tray icon away (a killed process cannot do it itself)
        Instances.StartTrayGuard();
        if (openSettings != null) SettingsWindow.Open(openSettings);
        Events.Emit("ready", new JsonObject { ["port"] = port, ["version"] = Version, ["pid"] = Environment.ProcessId });
        WriteInstanceFile();

        var timer = new System.Windows.Forms.Timer { Interval = 30 };
        timer.Tick += (_, _) =>
        {
            try
            {
                var frame = Engine.Tick();
                if (frame.Changed) overlay.Present(frame.Scene);
                buttons.Update(frame.Buttons);
                card.UpdateModel(frame.Card);
                mascot.Update(frame.Mascot);
                // the canvas goes on top (it appeared, or its once-a-second re-raise) in one batch with our other
                // windows above it: the marks never cover the card, the buttons or the mascot, not even for a moment
                if (overlay.RaisePending)
                {
                    var order = new System.Collections.Generic.List<(IntPtr, uint)> { (overlay.Handle, overlay.TakeRaise()) };
                    foreach (var h in buttons.Handles) order.Add((h, 0));
                    order.Add((mascot.Handle, 0));
                    order.Add((card.Handle, 0));
                    Native.RaiseTogether(order);
                }
            }
            catch (Exception e) { Log.Warn("frame: " + e); }
        };
        timer.Start();

        Application.Run();

        timer.Stop();
        // the icon goes first: the server below may take up to 2 s to stop
        tray.HideNow();
        InputHook.Stop();
        Resolver.Stop();
        Finder.Stop();
        try { server.StopAsync().Wait(2000); } catch { }
        DeleteInstanceFile();
        ReleaseInstance();
        Events.Emit("exit");
        Events.Flush();
        Log.CloseFile();
        return 0;
    }

    /// <summary>Help sketches pile up with every "circle it": keep the last 50, none older than 14 days.</summary>
    private static void CleanUpOldFiles()
    {
        try
        {
            int n = Files.Prune(Path.Combine(Files.AppDir, "help"), "sketch-*.png", 50, TimeSpan.FromDays(14));
            if (n > 0) Log.Info($"removed {n} old help sketch image(s)");
        }
        catch (Exception e) { Log.Debug("help cleanup: " + e.Message); }
    }

    private static string KeyFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TutorialOverlay", "key.txt");

    private static string KeySource(string[] args) =>
        Array.IndexOf(args, "--key") >= 0 ? "--key" :
        Environment.GetEnvironmentVariable("TUTORIAL_OVERLAY_KEY") != null ? "TUTORIAL_OVERLAY_KEY" : KeyFile;

    private static string LoadOrCreateKey()
    {
        try
        {
            if (File.Exists(KeyFile))
            {
                var k = File.ReadAllText(KeyFile).Trim();
                if (k.Length >= 16) return k;
            }
            var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
            Files.WriteAtomic(KeyFile, key);
            Log.Info($"generated a new key in {KeyFile}");
            return key;
        }
        catch (Exception e)
        {
            throw new InvalidOperationException("cannot read or create " + KeyFile + ": " + e.Message);
        }
    }
}
