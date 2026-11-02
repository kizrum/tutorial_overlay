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
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        for (int i = 0; i < _args.Length; i++)
        {
            if (_args[i] == "--port") { i++; continue; }
            psi.ArgumentList.Add(_args[i]);
        }
        Process.Start(psi);
        Application.Exit();
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
                case "--help" or "-h" or "/?":
                    Console.Error.WriteLine("TutorialOverlay [--port 7788] [--key KEY] [--no-anim] [--show-inactive] [--verbose]\n" +
                                            "  key: --key, env TUTORIAL_OVERLAY_KEY, or %LOCALAPPDATA%\\TutorialOverlay\\key.txt (created on first run)\n" +
                                            "  stdout: one JSON event per line; stderr: diagnostics");
                    return 0;
            }
        }
        key ??= LoadOrCreateKey();
        Port = port;
        Key = key;
        KeyFrom = KeySource(args);

        using var single = new Mutex(true, $"TutorialOverlay.{port}", out bool first);
        if (!first) { Log.Warn($"another TutorialOverlay is already serving port {port}"); return 2; }

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
            return 3;
        }
        server.Stopping += () => { try { overlay.BeginInvoke(Application.Exit); } catch { } };
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; try { overlay.BeginInvoke(Application.Exit); } catch { } };

        Log.Info($"TutorialOverlay {Version} listening on http://127.0.0.1:{port}/mcp (key from {KeyFrom})");
        using var tray = new TrayIcon();
        Events.Emit("ready", new JsonObject { ["port"] = port, ["version"] = Version, ["pid"] = Environment.ProcessId });

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
