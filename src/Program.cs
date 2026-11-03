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
    public const string Version = "0.1.0";
    private static System.Windows.Forms.Control? _ui;

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
        // one server per user: let go of the instance lock first, or the new process would find "us" still running
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

    // ---- one running server per user (per --instance name, for side-by-side development builds)

    private static Mutex? _single;
    private static string _instance = "";

    private static string InstanceFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TutorialOverlay",
        _instance.Length == 0 ? "server.json" : $"server.{_instance}.json");

    private static bool ClaimInstance()
    {
        _single = new Mutex(true, "TutorialOverlay.Server" + (_instance.Length == 0 ? "" : "." + _instance), out bool first);
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
            File.WriteAllText(InstanceFile, new JsonObject
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
        JsonNode? info = null;
        try { if (File.Exists(InstanceFile)) info = JsonNode.Parse(File.ReadAllText(InstanceFile)); } catch { }
        int? port = J.Int(info, "port"), pid = J.Int(info, "pid");
        Log.Warn($"TutorialOverlay is already running (port {port?.ToString() ?? "?"}, PID {pid?.ToString() ?? "?"}); not starting a second one");
        Events.Emit("already_running", new JsonObject { ["port"] = port, ["pid"] = pid, ["url"] = J.Str(info, "url") });
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
            foreach (System.Windows.Forms.Form f in System.Windows.Forms.Application.OpenForms) handles.Add(f.Handle);
            foreach (var h in handles) Native.SetWindowDisplayAffinity(h, capturable ? 0u : Native.WDA_EXCLUDEFROMCAPTURE);
        });
    }

    [STAThread]
    private static int Main(string[] args)
    {
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
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port" when i + 1 < args.Length: port = int.Parse(args[++i]); PortFromArgs = true; break;
                case "--key" when i + 1 < args.Length: key = args[++i]; break;
                case "--no-anim": Engine.Animate = false; break;
                case "--show-inactive": Engine.HideWhenInactiveDefault = false; break;
                case "--hide-from-capture": Settings.Global.HideFromCapture = true; break;
                case "--verbose": Log.Verbose = true; break;
                case "--instance" when i + 1 < args.Length: _instance = Tutorials.Slug(args[++i]); break;
                // optional tab name: --settings look | keys
                case "--settings": openSettings = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "server"; break;
                case "--help" or "-h" or "/?":
                    Console.Error.WriteLine("TutorialOverlay [--port 7788] [--key KEY] [--no-anim] [--show-inactive] [--verbose] [--settings [server|look|keys]] [--instance NAME]\n" +
                                            "  --settings: open the settings window right away (on that tab)\n" +
                                            "  one server per user; --instance NAME runs a separate one beside it (development)\n" +
                                            "  key: --key, env TUTORIAL_OVERLAY_KEY, or %LOCALAPPDATA%\\TutorialOverlay\\key.txt (created on first run)\n" +
                                            "  stdout: one JSON event per line; stderr: diagnostics");
                    return 0;
            }
        }
        key ??= LoadOrCreateKey();
        Port = port;
        Key = key;
        KeyFrom = KeySource(args);

        if (!ClaimInstance()) return AlreadyRunning();

        var overlay = new OverlayWindow();
        var card = new CardWindow();
        var buttons = new ButtonHost();
        _ = overlay.Handle;
        _ = card.Handle;
        _ui = overlay;

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
            }
            catch (Exception e) { Log.Warn("frame: " + e); }
        };
        timer.Start();

        Application.Run();

        timer.Stop();
        InputHook.Stop();
        Resolver.Stop();
        Finder.Stop();
        try { server.StopAsync().Wait(2000); } catch { }
        DeleteInstanceFile();
        ReleaseInstance();
        Events.Emit("exit");
        return 0;
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
            Directory.CreateDirectory(Path.GetDirectoryName(KeyFile)!);
            File.WriteAllText(KeyFile, key);
            Log.Info($"generated a new key in {KeyFile}");
            return key;
        }
        catch (Exception e)
        {
            throw new InvalidOperationException("cannot read or create " + KeyFile + ": " + e.Message);
        }
    }
}
