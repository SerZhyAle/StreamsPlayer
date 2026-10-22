using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace StreamsPlayer.Core;

/// <summary>The LAN share of one broadcast, as the section 2.1 / item C shapes fix it.</summary>
/// <param name="Title">`icy-name`; the video shape carries no ICY headers.</param>
/// <param name="BitrateKbps">`icy-br` for the audio shape.</param>
/// <param name="MaxListeners">Requirement 4: the cap on LAN listeners; 0 means no cap.</param>
/// <param name="Paths">The exact request paths this broadcast serves (audio: `/live-audio.aac` and `/live-audio`; video: `/&lt;token&gt;/live.ts`).</param>
/// <param name="ContentType">`audio/aac` or `video/mp2t`.</param>
/// <param name="IsVideo">Chooses the item C response shape (no ICY headers, `video/mp2t`).</param>
public sealed record BroadcastLanServerOptions(
    string Title,
    int BitrateKbps,
    int MaxListeners,
    IReadOnlyList<string> Paths,
    string ContentType,
    bool IsVideo);

/// <summary>
/// SP-0217: this PC's own LAN listener for one running broadcast - the section 2.1 audio shape over
/// raw TCP (the video shape of item C uses the same server with its own options). One writer of bytes
/// feeds every listener: whole frames only, no history (a connection starts at the next frame), a
/// bounded per-listener queue that drops to the live edge and never stalls the encoder, and a clean
/// chunked end. The listener is bound to loopback always - the tunnel bridge and this machine reach it
/// there - and to the chosen LAN address only while the Settings opt-in is on, so the service it
/// bridges to is reachable from this machine unless that opt-in is.
/// </summary>
/// <remarks>
/// The cap counts the clients of the LAN listener only: a loopback client is a tunnel the exchange
/// server counts itself (item M), or this machine. That count is what the producer lowers the
/// server-enforced `maxListeners` by while LAN listeners are connected.
/// </remarks>
public sealed class BroadcastLanServer : IAsyncDisposable
{
    /// <summary>The most a listener may fall behind before its oldest queued frames are dropped: about 1.5 s of 128 kbit/s audio.</summary>
    private const int PerListenerQueueFrames = 48;

    private const int RequestHeadLimit = 8192;
    private static readonly TimeSpan RequestReadTimeout = TimeSpan.FromSeconds(5);

    private readonly BroadcastLanServerOptions _options;
    private readonly List<ClientSession> _clients = [];
    private readonly object _gate = new();
    private TcpListener? _loopbackListener;
    private TcpListener? _lanListener;
    private int _lanClients;
    private volatile bool _stopped;

    /// <summary>The number of LAN listeners connected - the figure the producer answers for (item M).</summary>
    public event Action<int>? LanListenerCountChanged;

    public BroadcastLanServer(BroadcastLanServerOptions options) => _options = options;

    /// <param name="lanAddress">The reachable LAN IPv4 address to serve on while the opt-in is on; null keeps the share on loopback only.</param>
    /// <exception cref="ArgumentException">A LAN address that is loopback or unspecified (producer rule 1).</exception>
    public void Start(string? lanAddress, int port)
    {
        _loopbackListener = new TcpListener(IPAddress.Loopback, port);
        _loopbackListener.Start(backlog: 8);
        _ = AcceptAsync(_loopbackListener, countsTowardCap: false);
        if (lanAddress is not null)
        {
            if (!IPAddress.TryParse(lanAddress, out var address) || IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any))
            {
                throw new ArgumentException("The LAN address must be a reachable IPv4 address.", nameof(lanAddress));
            }

            _lanListener = new TcpListener(address, port);
            _lanListener.Start(backlog: 8);
            _ = AcceptAsync(_lanListener, countsTowardCap: true);
        }
    }

    /// <summary>Hands one whole frame to every connected listener; a listener that has fallen behind loses its oldest frames, never the live edge.</summary>
    public void Enqueue(ReadOnlyMemory<byte> frame)
    {
        if (_stopped)
        {
            return;
        }

        var copy = frame.ToArray();
        lock (_gate)
        {
            foreach (var client in _clients)
            {
                client.Queue.Writer.TryWrite(copy);
            }
        }
    }

    /// <summary>Ends every listener's body cleanly (the chunked terminator) and stops listening.</summary>
    public async Task StopAsync()
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        _loopbackListener?.Stop();
        _lanListener?.Stop();
        List<ClientSession> clients;
        lock (_gate)
        {
            clients = [.. _clients];
        }

        foreach (var client in clients)
        {
            client.Queue.Writer.TryComplete();
        }

        foreach (var client in clients)
        {
            try
            {
                await client.Session.ConfigureAwait(false);
            }
            catch
            {
                // A listener whose socket failed keeps its own end; stopping the rest is not optional.
            }
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task AcceptAsync(TcpListener listener, bool countsTowardCap)
    {
        while (!_stopped)
        {
            TcpClient tcp;
            try
            {
                tcp = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
            {
                return;
            }

            var session = new ClientSession(tcp, countsTowardCap);
            lock (_gate)
            {
                _clients.Add(session);
            }

            session.Session = ServeAsync(session);
            _ = ObserveAsync(session);
        }
    }

    private async Task ObserveAsync(ClientSession session)
    {
        try
        {
            await session.Session.ConfigureAwait(false);
        }
        catch
        {
            // A listener's socket failing is its own end; the broadcast keeps serving the others.
        }

        lock (_gate)
        {
            _clients.Remove(session);
            if (session.CountsTowardCap && session.Live)
            {
                _lanClients--;
                LanListenerCountChanged?.Invoke(_lanClients);
            }
        }

        session.Tcp.Dispose();
    }

    private async Task ServeAsync(ClientSession session)
    {
        using var timeout = new CancellationTokenSource(RequestReadTimeout);
        var request = await ReadRequestHeadAsync(session.Tcp, timeout.Token).ConfigureAwait(false);
        var path = RequestPath(request);
        if (path is null)
        {
            await WriteSimpleResponseAsync(session.Tcp, "404 Not Found", close: true, timeout.Token).ConfigureAwait(false);
            return;
        }

        if (session.CountsTowardCap && _options.MaxListeners > 0 && Volatile.Read(ref _lanClients) >= _options.MaxListeners)
        {
            await WriteSimpleResponseAsync(session.Tcp, "503 Service Unavailable", close: true, timeout.Token).ConfigureAwait(false);
            return;
        }

        session.Live = true;
        if (session.CountsTowardCap)
        {
            lock (_gate)
            {
                _lanClients++;
            }

            LanListenerCountChanged?.Invoke(Volatile.Read(ref _lanClients));
        }
        var head = new StringBuilder()
            .Append("HTTP/1.1 200 OK\r\n")
            .Append("Content-Type: ").Append(_options.ContentType).Append("\r\n")
            .Append("Transfer-Encoding: chunked\r\n")
            .Append("Accept-Ranges: none\r\n")
            .Append("Cache-Control: no-cache\r\n");
        if (!_options.IsVideo)
        {
            // Section 2.1: the ICY response headers a radio consumer expects, and never icy-metaint -
            // in-stream metadata is not offered, and a listener that asks for it gets none.
            head.Append("icy-name: ").Append(_options.Title).Append("\r\n")
                .Append("icy-genre: Live\r\n")
                .Append("icy-pub: 0\r\n")
                .Append("icy-br: ").Append(_options.BitrateKbps.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append("\r\n");
        }

        head.Append("\r\n");
        var bytes = Encoding.UTF8.GetBytes(head.ToString());
        var stream = session.Tcp.GetStream();
        await stream.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);

        // The live edge: only frames that arrive from here on. A dropped frame costs one frame of
        // silence; a held buffer would cost every listener their delay budget (consumer rule 2).
        using var cancellation = new CancellationTokenSource(RequestReadTimeout);
        session.Cancellation = cancellation;
        await foreach (var frame in session.Queue.Reader.ReadAllAsync(cancellation.Token).ConfigureAwait(false))
        {
            if (_stopped)
            {
                break;
            }

            var chunk = Encoding.ASCII.GetBytes(frame.Length.ToString("X", System.Globalization.CultureInfo.InvariantCulture) + "\r\n");
            await stream.WriteAsync(chunk, cancellation.Token).ConfigureAwait(false);
            await stream.WriteAsync(frame, cancellation.Token).ConfigureAwait(false);
            await stream.WriteAsync(new byte[] { 13, 10 }, cancellation.Token).ConfigureAwait(false);
            cancellation.CancelAfter(RequestReadTimeout);
        }

        // The terminal chunk of a cleanly ended body, also on the stop path: the listener sees the
        // chunked body end, never a bare close. A listener that already left is its own end, not a
        // failure that may reach the stop path.
        try
        {
            await stream.WriteAsync(new byte[] { 48, 13, 10, 13, 10 }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
        {
        }
    }

    /// <summary>The request line's path, or null when the head is not a GET of one of this broadcast's paths.</summary>
    private string? RequestPath(string? requestHead)
    {
        if (requestHead is null)
        {
            return null;
        }

        var firstLineEnd = requestHead.IndexOf('\n');
        var firstLine = (firstLineEnd >= 0 ? requestHead[..firstLineEnd] : requestHead).Trim();
        var parts = firstLine.Split(' ');
        if (parts.Length < 2 || !parts[0].Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var path = parts[1].Split('?')[0];
        return _options.Paths.Any(accepted => accepted.Equals(path, StringComparison.OrdinalIgnoreCase)) ? path : null;
    }

    private static async Task<string?> ReadRequestHeadAsync(TcpClient tcp, CancellationToken cancellationToken)
    {
        var buffer = new byte[RequestHeadLimit];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await tcp.GetStream().ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                return null;
            }

            length += read;
            var head = Encoding.UTF8.GetString(buffer, 0, length);
            if (head.Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                return head;
            }
        }

        return null;
    }

    private static async Task WriteSimpleResponseAsync(TcpClient tcp, string status, bool close, CancellationToken cancellationToken)
    {
        var response = "HTTP/1.1 " + status + "\r\nContent-Length: 0\r\n"
            + (close ? "Connection: close\r\n" : "") + "\r\n";
        try
        {
            await tcp.GetStream().WriteAsync(Encoding.UTF8.GetBytes(response), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException)
        {
            // The listener's socket is gone; nothing more is owed to it.
        }
    }

    private sealed class ClientSession(TcpClient tcp, bool countsTowardCap)
    {
        public TcpClient Tcp { get; } = tcp;
        public bool CountsTowardCap { get; } = countsTowardCap;
        public Channel<byte[]> Queue { get; } = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(PerListenerQueueFrames) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        public Task Session { get; set; } = Task.CompletedTask;
        public CancellationTokenSource? Cancellation { get; set; }
        public bool Live { get; set; }
    }
}
