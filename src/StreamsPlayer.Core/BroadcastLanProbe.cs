using System.Net.Sockets;
using System.Text;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0250 requirement 4: what a producer's LAN address answers to the reconnect that follows the end of a stream
/// (<c>LIVE-BROADCAST</c> item T). The engine reports a refused connection, a <c>404</c> and an unreachable host all as
/// one error, so this asks the three apart before the engine gets the address: a closed port is
/// <see cref="BroadcastEndAnswer.LanRefused"/>, the first status line of an RTSP <c>DESCRIBE</c> or an HTTP <c>GET</c>
/// that reads <c>404</c> or <c>410</c> is the ended broadcast's answer, and everything else - a status that says the
/// server is alive (<c>200</c>, <c>401</c>), a reset, a timeout, no answer - is <see cref="BroadcastEndAnswer.None"/>,
/// a lost connection under section 5.2 item 3. An <c>https</c> address is only connected to: the leaf is the endpoint's own
/// pin to check and this asks nothing through it.
/// </summary>
public static class BroadcastLanProbe
{
    /// <summary>The LAN connect bound of file 12 section 6.2, the only number on record.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(1.5);

    /// <summary>How long a server that accepted the connection may take to write its first line.</summary>
    public static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(1.5);

    private const int StatusLineLimit = 256;

    public static async Task<BroadcastEndAnswer> ProbeAsync(
        Uri address,
        CancellationToken cancellationToken,
        TimeSpan? connectTimeout = null,
        TimeSpan? answerTimeout = null)
    {
        var scheme = address.Scheme.ToLowerInvariant();
        if (scheme is not ("rtsp" or "http" or "https"))
        {
            return BroadcastEndAnswer.None;
        }

        var port = address.Port > 0 ? address.Port : scheme switch { "rtsp" => 554, "https" => 443, _ => 80 };
        using var client = new TcpClient();
        try
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connect.CancelAfter(connectTimeout ?? ConnectTimeout);
            await client.ConnectAsync(address.Host, port, connect.Token).ConfigureAwait(false);
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return BroadcastEndAnswer.LanRefused;
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException or ObjectDisposedException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return BroadcastEndAnswer.None;
        }

        if (scheme == "https")
        {
            return BroadcastEndAnswer.None;
        }

        try
        {
            using var answer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            answer.CancelAfter(answerTimeout ?? AnswerTimeout);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(Request(address, scheme)), answer.Token).ConfigureAwait(false);
            var line = await ReadStatusLineAsync(stream, answer.Token).ConfigureAwait(false);
            return StatusOf(line) switch
            {
                404 => BroadcastEndAnswer.LanNotFound,
                410 => BroadcastEndAnswer.LanGone,
                _ => BroadcastEndAnswer.None
            };
        }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return BroadcastEndAnswer.None;
        }
    }

    /// <summary>
    /// The request is the one the engine would send first: nothing is set up and no session is opened. The path is the
    /// descriptor's own address and is never logged.
    /// </summary>
    private static string Request(Uri address, string scheme)
    {
        var target = address.GetComponents(UriComponents.PathAndQuery, UriFormat.UriEscaped);
        return scheme == "rtsp"
            ? $"DESCRIBE {address.GetComponents(UriComponents.SchemeAndServer | UriComponents.PathAndQuery, UriFormat.UriEscaped)} RTSP/1.0\r\n" +
              "CSeq: 1\r\nUser-Agent: StreamsPlayer/0.1\r\nAccept: application/sdp\r\n\r\n"
            : $"GET {(target.Length == 0 ? "/" : target)} HTTP/1.1\r\nHost: {address.Authority}\r\n" +
              "User-Agent: StreamsPlayer/0.1\r\nConnection: close\r\n\r\n";
    }

    private static async Task<string> ReadStatusLineAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[StatusLineLimit];
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(filled), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            filled += read;
            if (Array.IndexOf(buffer, (byte)'\n', 0, filled) >= 0)
            {
                break;
            }
        }

        var text = Encoding.ASCII.GetString(buffer, 0, filled);
        var end = text.IndexOf('\n');
        return (end >= 0 ? text[..end] : text).TrimEnd('\r');
    }

    /// <summary>The three-digit code of <c>RTSP/1.0 404 Not Found</c> or <c>HTTP/1.1 404 Not Found</c>; null for anything else.</summary>
    internal static int? StatusOf(string line)
    {
        var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2
            && (parts[0].StartsWith("HTTP/", StringComparison.Ordinal) || parts[0].StartsWith("RTSP/", StringComparison.Ordinal))
            && parts[1].Length == 3 && int.TryParse(parts[1], out var code) ? code : null;
    }
}
