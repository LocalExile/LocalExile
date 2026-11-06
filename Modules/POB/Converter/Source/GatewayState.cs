using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

/// <summary>Reads Windows' TCP table to tell whether a Gateway runs and has a client. It opens no socket and sends nothing.</summary>
static class GatewayState
{
    public sealed record Snapshot(bool Running, bool ClientConnected, DateTimeOffset? Started);

    const int InternetV4 = 2, EveryConnectionWithProcess = 5, InsufficientBuffer = 122;
    const uint Listening = 2, Established = 5;
    const int RowSize = 24, StateOffset = 0, LocalPortOffset = 8, ProcessOffset = 20;

    [DllImport("iphlpapi.dll")]
    static extern uint GetExtendedTcpTable(nint table, ref int size, bool sorted, int family, int tableClass, uint reserved);

    public static int Port => int.TryParse(Environment.GetEnvironmentVariable("Game_GATEWAY_PORT"), out int port)
        && port is > 0 and < 65536 ? port : 6112;

    public static Snapshot Read(int port)
    {
        int size = 0; uint status = GetExtendedTcpTable(0, ref size, false, InternetV4, EveryConnectionWithProcess, 0);
        if (status != InsufficientBuffer) throw new InvalidOperationException($"Windows did not report TCP connections (error {status}).");
        // Connections can open between the two calls, so the second one may need a larger buffer.
        for (int attempt = 0; attempt < 4; attempt++)
        {
            size += 16 * RowSize;
            nint table = Marshal.AllocHGlobal(size);
            try
            {
                status = GetExtendedTcpTable(table, ref size, false, InternetV4, EveryConnectionWithProcess, 0);
                if (status == InsufficientBuffer) continue;
                if (status != 0) throw new InvalidOperationException($"Windows did not report TCP connections (error {status}).");
                return Parse(table, port);
            }
            finally { Marshal.FreeHGlobal(table); }
        }
        throw new InvalidOperationException("Windows' TCP table kept growing; try again.");
    }

    static Snapshot Parse(nint table, int port)
    {
        int rows = Marshal.ReadInt32(table); int? process = null; bool connected = false;
        for (int i = 0; i < rows; i++)
        {
            nint row = table + sizeof(uint) + i * RowSize;
            uint raw = (uint)Marshal.ReadInt32(row, LocalPortOffset);
            int local = (int)((raw & 0xff) << 8 | raw >> 8 & 0xff); // The port is in network byte order.
            if (local != port) continue;
            uint state = (uint)Marshal.ReadInt32(row, StateOffset);
            if (state == Listening) process ??= Marshal.ReadInt32(row, ProcessOffset);
            else if (state == Established) connected = true;
        }
        return new(process is not null, connected, process is { } id ? StartOf(id) : null);
    }

    static DateTimeOffset? StartOf(int id)
    {
        try { using var process = Process.GetProcessById(id); return new DateTimeOffset(process.StartTime); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { return null; }
    }
}
