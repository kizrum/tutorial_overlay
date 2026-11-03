using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace TutorialOverlay;

/// <summary>Is a local port free, and if not — which program listens on it.</summary>
internal static class Ports
{
    public const int First = 7788, Last = 7899;

    /// <summary>The real test: can we bind 127.0.0.1:port right now (exclusively, as the server will).</summary>
    public static bool IsFree(int port)
    {
        try
        {
            var l = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
            l.Start();
            l.Stop();
            return true;
        }
        catch (SocketException) { return false; }
    }

    /// <summary>The first free port from <paramref name="from"/> up to <see cref="Last"/>, skipping <paramref name="except"/>.</summary>
    public static int? FindFree(int from = First, int except = -1)
    {
        for (int p = Math.Max(1024, from); p <= Last; p++)
            if (p != except && IsFree(p)) return p;
        return null;
    }

    /// <summary>"name (PID n)" of the program listening on the port, or null if unknown.</summary>
    public static string? Owner(int port)
    {
        int pid = ListenerPid(port, 2) ?? ListenerPid(port, 23) ?? 0;
        if (pid == 0) return null;
        if (pid == Environment.ProcessId) return "TutorialOverlay";
        try { return $"{Process.GetProcessById(pid).ProcessName} (PID {pid})"; }
        catch (ArgumentException) { return $"PID {pid}"; }
    }

    // ---- GetExtendedTcpTable (iphlpapi): listening sockets with their owner process

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int family, int tableClass, uint reserved);
    private const int TCP_TABLE_OWNER_PID_LISTENER = 3;

    /// <param name="family">2 = IPv4 (rows of 6 DWORDs), 23 = IPv6 (rows of 56 bytes)</param>
    private static int? ListenerPid(int port, int family)
    {
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TCP_TABLE_OWNER_PID_LISTENER, 0);
        if (size <= 0) return null;
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buf, ref size, false, family, TCP_TABLE_OWNER_PID_LISTENER, 0) != 0) return null;
            int n = Marshal.ReadInt32(buf);
            int rowSize = family == 2 ? 24 : 56, portOff = family == 2 ? 8 : 20, pidOff = family == 2 ? 20 : 52;
            for (int i = 0; i < n; i++)
            {
                var row = buf + 4 + i * rowSize;
                int raw = Marshal.ReadInt32(row + portOff);
                int p = ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);     // network byte order in the low word
                if (p == port) return Marshal.ReadInt32(row + pidOff);
            }
            return null;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>
    /// What to give the agent: the command that registers this server in Claude Code, for all projects. It runs this
    /// exe with --stdio, which finds the port and the key itself — so the line stays right after a port change.
    /// </summary>
    public static string ConnectCommand() =>
        $"claude mcp add --scope user tutorial-overlay -- \"{Environment.ProcessPath}\" --stdio";
}
