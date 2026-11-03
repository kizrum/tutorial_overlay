using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;

namespace TutorialOverlay;

/// <summary>
/// The one server of this user: where it listens (server.json) and a polite "quit" signal (a named event), so
/// `--stop` closes it the normal way — with its tray icon removed. A process that is killed instead leaves a dead
/// icon in the tray until the mouse passes over it, and its server.json behind (removed on the next start).
/// </summary>
internal static class Instances
{
    public sealed record Info(int Port, int Pid, string? Version);

    public static string File => Path.Combine(Files.AppDir, "server.json");

    private const string QuitEventName = "TutorialOverlay.Quit";

    /// <summary>The running server, if any (a server.json of a process that is gone is removed on the way).</summary>
    public static Info? Running()
    {
        try
        {
            if (!System.IO.File.Exists(File)) return null;
            var n = JsonNode.Parse(System.IO.File.ReadAllText(File));
            int pid = J.Int(n, "pid") ?? 0;
            if (pid > 0 && Native.ProcessAlive(pid)) return new Info(J.Int(n, "port") ?? 0, pid, J.Str(n, "version"));
            // killed, or crashed: it could not clean up after itself
            System.IO.File.Delete(File);
            Log.Info($"removed server.json of a server that is gone (PID {pid})");
        }
        catch (Exception e) { Log.Debug("server.json: " + e.Message); }
        return null;
    }

    private static EventWaitHandle? _quit;

    /// <summary>Waits for a quit request in the background; <paramref name="onQuit"/> runs when one comes.</summary>
    public static void ListenForQuit(Action onQuit)
    {
        try { _quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName); }
        catch (Exception e) { Log.Warn("quit signal not available: " + e.Message); return; }
        var h = _quit;
        new Thread(() =>
        {
            h.WaitOne();
            Log.Info("asked to quit (--stop)");
            onQuit();
        }) { IsBackground = true, Name = "quit-listener" }.Start();
    }

    /// <summary>Asks the server to quit. False when nothing listens (not running, or a build without this).</summary>
    public static bool RequestQuit()
    {
        try
        {
            using var h = EventWaitHandle.OpenExisting(QuitEventName);
            return h.Set();
        }
        catch (WaitHandleCannotBeOpenedException) { return false; }
        catch (Exception e) { Log.Warn("quit request: " + e.Message); return false; }
    }

    // ---- tray guard: when the server is killed, take its icon (and server.json) away at once

    /// <summary>
    /// Starts the tray guard for this server. Two hops (server → launcher → guard, the launcher exiting at once)
    /// leave the guard outside the server's process tree, so "kill the tree" (taskkill /T, a tool stopping its
    /// background job) does not take it along; its standard streams are its own, so it never holds a pipe that
    /// an agent reads the server's events from.
    /// </summary>
    public static void StartTrayGuard()
    {
        try { Spawn("--tray-guard-spawn", Environment.ProcessId); }
        catch (Exception e) { Log.Warn("tray guard not started: " + e.Message); }
    }

    private static void Spawn(string mode, int pid)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        psi.ArgumentList.Add(mode);
        psi.ArgumentList.Add(pid.ToString());
        using var p = System.Diagnostics.Process.Start(psi);
    }

    /// <summary>--tray-guard-spawn PID: start the guard and leave (that cuts it off the server's tree).</summary>
    public static int GuardSpawn(int pid)
    {
        try { Spawn("--tray-guard", pid); return 0; }
        catch { return 1; }
    }

    /// <summary>--tray-guard PID: wait for the server to end; then remove its tray icon and server.json if it left them.</summary>
    public static int Guard(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            p.WaitForExit();
        }
        catch (ArgumentException) { }          // already gone
        // the icon has a fixed GUID, so this process (same exe) may remove it; after a normal exit it is gone already
        ShellTrayIcon.RemoveStale();
        try
        {
            if (System.IO.File.Exists(File) && J.Int(JsonNode.Parse(System.IO.File.ReadAllText(File)), "pid") == pid) System.IO.File.Delete(File);
        }
        catch { }
        return 0;
    }

    /// <summary>--stop: politely stop the running server and wait for it to go. Exit code 0 stopped, 2 none running, 3 still running.</summary>
    public static int StopCommand()
    {
        var target = Running();
        if (!RequestQuit())
        {
            Console.Error.WriteLine(target == null ? "TutorialOverlay is not running"
                : $"TutorialOverlay (PID {target.Pid}) does not listen for a stop request (an older build): close it from its tray icon");
            return target == null ? 2 : 3;
        }
        if (target == null) { Console.Error.WriteLine("asked TutorialOverlay to quit"); return 0; }
        for (int i = 0; i < 100 && Native.ProcessAlive(target.Pid); i++) Thread.Sleep(100);
        bool gone = !Native.ProcessAlive(target.Pid);
        Console.Error.WriteLine(gone ? $"TutorialOverlay (PID {target.Pid}) stopped" : $"TutorialOverlay (PID {target.Pid}) is still running after 10 s");
        return gone ? 0 : 3;
    }
}
