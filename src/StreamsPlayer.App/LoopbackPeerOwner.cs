using System.Net;
using System.Runtime.InteropServices;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0246 S13-4: who is on the other end of a loopback connection. The tunnel forwarder listens on 127.0.0.1, which any
/// process of any Windows user on the machine can reach; the only legitimate connector is this process's own player
/// engine. The Windows TCP table names the process that owns each connection, so a connector that is positively another
/// process can be turned away.
/// </summary>
/// <remarks>
/// It refuses only on a positive answer. A table that cannot be read, or a connection already gone from it, says nothing
/// about the connector, and playback is worth more than a guess: the cost of failing open is the exposure the forwarder
/// already had, the cost of failing closed is a broadcast that does not play.
/// </remarks>
internal static class LoopbackPeerOwner
{
    private const int AddressFamilyInet = 2;
    private const int TcpTableOwnerPidAll = 5;
    private const uint NoError = 0;
    private const uint InsufficientBuffer = 122;
    private const int RowBytes = 24; // MIB_TCPROW_OWNER_PID: state, local address, local port, remote address, remote port, pid.

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool sort,
        int family, int tableClass, uint reserved);

    /// <summary>The <see cref="StreamsPlayer.Core.ExchangeTunnelForwarder"/> admission: this process, or an owner that could not be told.</summary>
    internal static bool IsThisProcess(IPEndPoint peer, int listenerPort) =>
        OwnerOf(peer.Port, listenerPort) is not { } owner || owner == Environment.ProcessId;

    /// <summary>The process id that owns the connecting end (<paramref name="clientPort"/> to <paramref name="listenerPort"/>), or null when it is not in the table.</summary>
    internal static int? OwnerOf(int clientPort, int listenerPort)
    {
        var size = 0;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(Math.Max(size, 4));
            try
            {
                var result = GetExtendedTcpTable(buffer, ref size, false, AddressFamilyInet, TcpTableOwnerPidAll, 0);
                if (result == InsufficientBuffer)
                {
                    continue; // the table grew between the two calls; size now holds what it needs
                }

                return result == NoError ? Find(buffer, clientPort, listenerPort) : null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return null;
    }

    private static int? Find(IntPtr table, int clientPort, int listenerPort)
    {
        var rows = Marshal.ReadInt32(table);
        for (var index = 0; index < rows; index++)
        {
            var row = table + 4 + index * RowBytes;
            if (PortOf(Marshal.ReadInt32(row, 8)) == clientPort && PortOf(Marshal.ReadInt32(row, 16)) == listenerPort)
            {
                return Marshal.ReadInt32(row, 20);
            }
        }

        return null;
    }

    // The port sits in the low 16 bits of its DWORD in network byte order.
    private static int PortOf(int value) => ((value & 0xFF) << 8) | ((value >> 8) & 0xFF);
}
