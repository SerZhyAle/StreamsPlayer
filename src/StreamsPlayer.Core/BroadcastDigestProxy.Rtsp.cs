using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace StreamsPlayer.Core;

public sealed partial class BroadcastDigestProxy
{
    private async Task RunRtspAsync(Stream input, Stream local, DigestRtspMessage first, CancellationToken token)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var connectDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        connectDeadline.CancelAfter(_connectTimeout);
        using var upstream = new TcpClient { NoDelay = true };
        await upstream.ConnectAsync(_connect.Host, _connect.IsDefaultPort ? 554 : _connect.Port, connectDeadline.Token).ConfigureAwait(false);
        var remote = upstream.GetStream();
        using var reader = new BufferedStream(remote, 16 * 1024);
        using var writes = new SemaphoreSlim(1);
        using var responseWritten = new SemaphoreSlim(0, 1);
        var replies = Channel.CreateBounded<DigestRtspMessage>(new BoundedChannelOptions(4) { SingleReader = true, SingleWriter = true });
        var admitted = 0;
        var receive = ReceiveAsync();
        try
        {
            BroadcastDigestClient? digest = null;
            var sequence = 0;
            var request = first;
            while (request is not null && !session.IsCancellationRequested)
            {
                if (request.Frame is not null)
                {
                    await request.WriteAsync(remote, session.Token).ConfigureAwait(false);
                    request = await DigestRtspMessage.ReadAsync(input, session.Token).ConfigureAwait(false);
                    continue;
                }
                if (!TryTarget(request.StartLine, out var target)) throw new InvalidDataException("Invalid RTSP route.");
                var method = request.StartLine.Split(' ')[0];
                var cseq = request.Header("CSeq");
                if (cseq is null) throw new InvalidDataException("Missing RTSP sequence.");
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(15));
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    var headers = request.Headers.Where(pair => !pair.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                        && !pair.Key.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
                        && !pair.Key.Equals("CSeq", StringComparison.OrdinalIgnoreCase)).ToList();
                    var sentSequence = (++sequence).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    headers.Add(new("CSeq", sentSequence));
                    if (digest is not null) headers.Add(new("Authorization", digest.Answer(_credentials, method, target)));
                    await new DigestRtspMessage($"{method} {target} RTSP/1.0", headers, request.Body).WriteAsync(remote, deadline.Token).ConfigureAwait(false);
                    var response = await replies.Reader.ReadAsync(deadline.Token).ConfigureAwait(false);
                    try
                    {
                        if (response.Header("CSeq") != sentSequence) throw new InvalidDataException("Mismatched RTSP sequence.");
                        var status = response.StartLine.Split(' ');
                        if (status.Length < 2 || status[0] != "RTSP/1.0") throw new InvalidDataException("Invalid RTSP response.");
                        if (status[1] == "401")
                        {
                            var next = BroadcastDigestClient.Select(response.Values("WWW-Authenticate"));
                            if (digest is not null && (next is null || !next.Stale || attempt == 2))
                            { Fail(ExchangeBroadcastAuth.PasswordRefusedReason); return; }
                            if (next is null) { Fail(ExchangeBroadcastAuth.UnsupportedEndpointReason); return; }
                            digest = next;
                            continue;
                        }
                        if (status[1].StartsWith('2') && digest is null && method != "OPTIONS")
                        { Fail(ExchangeBroadcastAuth.UnsupportedEndpointReason); return; }
                        if (status[1].StartsWith('3'))
                        { Fail(ExchangeBroadcastAuth.UnsupportedEndpointReason); return; }
                        if (status[1].StartsWith('2') && digest is not null) Volatile.Write(ref admitted, 1);
                        var rewritten = RewriteResponse(response, cseq);
                        await writes.WaitAsync(session.Token).ConfigureAwait(false);
                        try { await rewritten.WriteAsync(local, session.Token).ConfigureAwait(false); }
                        finally { writes.Release(); }
                        break;
                    }
                    finally { responseWritten.Release(); }
                }
                request = await DigestRtspMessage.ReadAsync(input, session.Token).ConfigureAwait(false);
                Touch();
            }
        }
        finally
        {
            session.Cancel();
            await receive.ConfigureAwait(false);
        }

        async Task ReceiveAsync()
        {
            try
            {
                while (await DigestRtspMessage.ReadAsync(reader, session.Token).ConfigureAwait(false) is { } response)
                {
                    Touch();
                    if (response.Frame is not null)
                    {
                        if (Volatile.Read(ref admitted) == 0)
                        { Fail(ExchangeBroadcastAuth.UnsupportedEndpointReason); return; }
                        await writes.WaitAsync(session.Token).ConfigureAwait(false);
                        try { await response.WriteAsync(local, session.Token).ConfigureAwait(false); }
                        finally { writes.Release(); }
                    }
                    else
                    {
                        await replies.Writer.WriteAsync(response, session.Token).ConfigureAwait(false);
                        // Preserve wire order: following media cannot overtake this control response.
                        await responseWritten.WaitAsync(session.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                if (!session.IsCancellationRequested) Fail("broadcast-digest-connection-failed");
            }
            finally { replies.Writer.TryComplete(); session.Cancel(); }
        }
    }

    private DigestRtspMessage RewriteResponse(DigestRtspMessage response, string cseq)
    {
        var origin = _target.GetLeftPart(UriPartial.Authority);
        string Rewrite(string value) => value.Replace(origin + "/", LocalOrigin + _prefix + "/", StringComparison.Ordinal);
        string Control(string value)
        {
            if (value == "*") return value;
            if (value.StartsWith('/')) return LocalOrigin + _prefix + value;
            if (!Uri.TryCreate(value, UriKind.Absolute, out var address)) return value;
            if (address.Scheme != "rtsp" || address.Host != _target.Host || address.Port != _target.Port || address.UserInfo.Length != 0)
            {
                Fail(ExchangeBroadcastAuth.UnsupportedEndpointReason);
                throw new InvalidDataException("RTSP control leaves the protected route.");
            }
            return LocalOrigin + _prefix + address.PathAndQuery;
        }
        var headers = response.Headers.Where(pair => !pair.Key.Equals("WWW-Authenticate", StringComparison.OrdinalIgnoreCase)
                && !pair.Key.Equals("Authentication-Info", StringComparison.OrdinalIgnoreCase))
            .Select(pair => new KeyValuePair<string, string>(pair.Key,
                pair.Key.Equals("CSeq", StringComparison.OrdinalIgnoreCase) ? cseq
                    : pair.Key.Equals("Content-Base", StringComparison.OrdinalIgnoreCase) || pair.Key.Equals("Content-Location", StringComparison.OrdinalIgnoreCase)
                        ? Control(pair.Value) : Rewrite(pair.Value))).ToList();
        var body = response.Header("Content-Type")?.StartsWith("application/sdp", StringComparison.OrdinalIgnoreCase) == true
            ? Encoding.UTF8.GetBytes(string.Join("\r\n", Encoding.UTF8.GetString(response.Body).Split("\r\n")
                .Select(line => line.StartsWith("a=control:", StringComparison.Ordinal) ? "a=control:" + Control(line[10..]) : line))) : response.Body;
        return new(response.StartLine, headers, body);
    }
}
