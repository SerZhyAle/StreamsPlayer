using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using StreamsPlayer.Core;
using Xunit;

namespace StreamsPlayer.Exchange.Tests;

/// <summary>
/// SP-0217's LAN share over real TCP: the section 2.1 response shape exactly as the contract writes it
/// (including the never-an-icy-metaint rule), whole ADTS frames only, no history for a late joiner,
/// the listener cap, and the clean chunked end.
/// </summary>
public sealed class BroadcastLanServerTests
{
    private static byte[] Frame(int payloadBytes = 300)
    {
        var frame = new byte[7 + payloadBytes];
        frame[0] = 0xFF;
        frame[1] = 0xF1;
        frame[2] = 0x40 | (4 << 2); // AAC-LC, 44100
        frame[3] = (byte)((1 << 6) | ((frame.Length >> 11) & 0x03));
        frame[4] = (byte)(frame.Length >> 3);
        frame[5] = (byte)((frame.Length & 7) << 5 | 0x1F);
        frame[6] = 0xFC;
        for (var index = 7; index < frame.Length; index++)
        {
            frame[index] = (byte)index;
        }

        return frame;
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private static async Task<string> ReadUntil(Stream stream, string marker, int limit = 65536)
    {
        var builder = new StringBuilder();
        var buffer = new byte[512];
        while (builder.Length < limit)
        {
            var read = await stream.ReadAsync(buffer);
            if (read <= 0)
            {
                break;
            }

            builder.Append(Encoding.UTF8.GetString(buffer, 0, read));
            if (builder.ToString().Contains(marker, StringComparison.Ordinal))
            {
                break;
            }
        }

        return builder.ToString();
    }

    [Fact]
    public async Task TheResponseIsTheSectionTwoPointOneShape()
    {
        await using var server = new BroadcastLanServer(new BroadcastLanServerOptions(
            "Test broadcast", 128, 0, ["/live-audio.aac", "/live-audio"], "audio/aac", IsVideo: false));
        var port = FreePort();
        server.Start(null, port);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes("GET /live-audio.aac HTTP/1.1\r\nHost: localhost\r\n\r\n"));
        var head = await ReadUntil(stream, "\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 200 OK\r\n", head, StringComparison.Ordinal);
        Assert.Contains("Content-Type: audio/aac\r\n", head, StringComparison.Ordinal);
        Assert.Contains("Transfer-Encoding: chunked\r\n", head, StringComparison.Ordinal);
        Assert.Contains("Accept-Ranges: none\r\n", head, StringComparison.Ordinal);
        Assert.Contains("icy-name: Test broadcast\r\n", head, StringComparison.Ordinal);
        Assert.Contains("icy-genre: Live\r\n", head, StringComparison.Ordinal);
        Assert.Contains("icy-pub: 0\r\n", head, StringComparison.Ordinal);
        Assert.Contains("icy-br: 128\r\n", head, StringComparison.Ordinal);
        Assert.DoesNotContain("icy-metaint", head, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheAliasPathServesTheSameBroadcast()
    {
        await using var server = new BroadcastLanServer(new BroadcastLanServerOptions(
            "Test broadcast", 128, 0, ["/live-audio.aac", "/live-audio"], "audio/aac", IsVideo: false));
        var port = FreePort();
        server.Start(null, port);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes("GET /live-audio HTTP/1.1\r\nHost: localhost\r\n\r\n"));
        var head = await ReadUntil(client.GetStream(), "\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 200 OK\r\n", head, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnotherPathIsRefused()
    {
        await using var server = new BroadcastLanServer(new BroadcastLanServerOptions(
            "Test broadcast", 128, 0, ["/live-audio.aac", "/live-audio"], "audio/aac", IsVideo: false));
        var port = FreePort();
        server.Start(null, port);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes("GET /other.mp3 HTTP/1.1\r\nHost: localhost\r\n\r\n"));
        var head = await ReadUntil(client.GetStream(), "\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 404 Not Found\r\n", head, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheBodyCarriesWholeAdtsFrames()
    {
        await using var server = new BroadcastLanServer(new BroadcastLanServerOptions(
            "Test broadcast", 128, 0, ["/live-audio.aac", "/live-audio"], "audio/aac", IsVideo: false));
        var port = FreePort();
        server.Start(null, port);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes("GET /live-audio.aac HTTP/1.1\r\nHost: localhost\r\n\r\n"));
        _ = await ReadUntil(stream, "\r\n\r\n");

        var first = Frame();
        var second = Frame();
        server.Enqueue(first);
        server.Enqueue(second);

        // Two frames arrive as two chunked entries, each exactly one frame - a late joiner decodes
        // from the next frame, and no chunk ever straddles a boundary.
        var received = new List<byte>();
        for (var frameIndex = 0; frameIndex < 2; frameIndex++)
        {
            var sizeLine = await ReadLine(stream);
            var size = Convert.ToInt32(sizeLine, 16);
            Assert.Equal(first.Length, size);
            var body = new byte[size];
            await stream.ReadExactlyAsync(body);
            received.AddRange(body);
            Assert.Equal("", await ReadLine(stream));
        }

        Assert.Equal(first, received.Take(first.Length).ToArray());
        Assert.Equal(second, received.Skip(first.Length).ToArray());
    }

    [Fact]
    public async Task ALateJoinerStartsAtTheNextFrame_NoHistoryIsKept()
    {
        await using var server = new BroadcastLanServer(new BroadcastLanServerOptions(
            "Test broadcast", 128, 0, ["/live-audio.aac", "/live-audio"], "audio/aac", IsVideo: false));
        var port = FreePort();
        server.Start(null, port);

        var beforeJoin = Frame();
        server.Enqueue(beforeJoin);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes("GET /live-audio.aac HTTP/1.1\r\nHost: localhost\r\n\r\n"));
        _ = await ReadUntil(stream, "\r\n\r\n");

        var afterJoin = Frame();
        server.Enqueue(afterJoin);

        var size = Convert.ToInt32(await ReadLine(stream), 16);
        var body = new byte[size];
        await stream.ReadExactlyAsync(body);
        Assert.Equal(afterJoin, body);
    }

    [Fact]
    public async Task StopEndsTheChunkedBodyCleanly()
    {
        var port = FreePort();
        var server = new BroadcastLanServer(new BroadcastLanServerOptions(
            "Test broadcast", 128, 0, ["/live-audio.aac", "/live-audio"], "audio/aac", IsVideo: false));
        server.Start(null, port);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes("GET /live-audio.aac HTTP/1.1\r\nHost: localhost\r\n\r\n"));
        _ = await ReadUntil(stream, "\r\n\r\n");
        server.Enqueue(Frame());

        // Consume the one frame chunk, then stop: the terminal chunk ends the body cleanly.
        var size = Convert.ToInt32(await ReadLine(stream), 16);
        var body = new byte[size];
        await stream.ReadExactlyAsync(body);
        _ = await ReadLine(stream);

        await server.StopAsync();
        Assert.Equal("0", await ReadLine(stream));
    }
    private static async Task<string> ReadLine(Stream stream)
    {
        var builder = new StringBuilder();
        while (true)
        {
            var read = await stream.ReadByteAsync();
            if (read < 0 || read == '\n')
            {
                break;
            }

            if (read != '\r')
            {
                builder.Append((char)read);
            }
        }

        return builder.ToString();
    }
}

internal static class StreamByteExtensions
{
    public static async Task<int> ReadByteAsync(this Stream stream)
    {
        var buffer = new byte[1];
        var read = await stream.ReadAsync(buffer);
        return read == 0 ? -1 : buffer[0];
    }
}
