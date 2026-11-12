using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using StreamsPlayer.App;

namespace StreamsPlayer.Exchange.Tests;

/// <summary>
/// SP-0246 S13-4: the tunnel forwarder's loopback port is reachable by every process on the machine. The Windows TCP
/// table names the owner of each connection; a connector that is positively another process is turned away, and one the
/// table cannot name is not.
/// </summary>
public sealed class LoopbackPeerOwnerTests
{
    [Fact]
    public async Task AConnectionThisProcessMakesIsOwnedByThisProcessAndAdmitted()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            using var accepted = await listener.AcceptTcpClientAsync();
            var peer = (IPEndPoint)accepted.Client.RemoteEndPoint!;

            Assert.Equal(Environment.ProcessId, LoopbackPeerOwner.OwnerOf(peer.Port, port));
            Assert.True(LoopbackPeerOwner.IsThisProcess(peer, port));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task AConnectionAnotherProcessMakesIsNamedAndTurnedAway()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var stranger = Process.Start(new ProcessStartInfo(
            "powershell.exe",
            $"-NoProfile -NonInteractive -Command \"$c = New-Object Net.Sockets.TcpClient('127.0.0.1', {((IPEndPoint)listener.LocalEndpoint).Port}); Start-Sleep -Seconds 30\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        })!;
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var accepted = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var peer = (IPEndPoint)accepted.Client.RemoteEndPoint!;

            Assert.Equal(stranger.Id, LoopbackPeerOwner.OwnerOf(peer.Port, port));
            Assert.False(LoopbackPeerOwner.IsThisProcess(peer, port));
        }
        finally
        {
            listener.Stop();
            stranger.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void AConnectionTheTableDoesNotListHasNoOwnerAndIsNotTurnedAway()
    {
        Assert.Null(LoopbackPeerOwner.OwnerOf(1, 1));
        Assert.True(LoopbackPeerOwner.IsThisProcess(new IPEndPoint(IPAddress.Loopback, 1), 1));
    }
}
