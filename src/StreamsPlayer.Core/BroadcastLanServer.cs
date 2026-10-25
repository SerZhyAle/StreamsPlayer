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
/// <para>SP-0223 (LIVE-BROADCAST items M and R): once <see cref="SetCredentials"/> holds a pair, both halves demand
/// HTTP Digest - the loopback half too, because the tunnel bridges there and no address may skip the check. With no
/// pair the server answers exactly as it did before.</para>
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
    private readonly BroadcastAttemptDamper _damper;
    private readonly CancellationTokenSource _stopping = new();
    private TcpListener? _loopbackListener;
    private TcpListener? _lanListener;
    private int _lanClients;
    private volatile bool _stopped;
    private int _port;

    /// <summary>The number of LAN listeners connected - the figure the producer answers for (item M).</summary>
    public event Action<int>? LanListenerCountChanged;

    public BroadcastLanServer(BroadcastLanServerOptions options)
        : this(options, new BroadcastAttemptDamper())
    {
    }

    internal BroadcastLanServer(BroadcastLanServerOptions options, BroadcastAttemptDamper damper)
    {
        _options = options;
        _damper = damper;
        Authenticator.PairsChanged += CloseListenersNoLongerAdmitted;
    }

    /// <summary>The Digest check both halves of this server answer to; empty means no authentication.</summary>
    public BroadcastDigestAuthenticator Authenticator { get; } = new();

    /// <summary>
    /// Replaces the credential pairs this server accepts; an empty list opens it. A listener already connected
    /// under a pair that is gone - or anonymous once a pair is set - is closed at once, the stricter reading of
    /// item R's "a new connection with the previous password is refused": a revoked password must not keep
    /// streaming. A listener whose own pair is unchanged keeps playing.
    /// </summary>
    public void SetCredentials(IReadOnlyList<ExchangeRelayCredentials> pairs) => Authenticator.SetPairs(pairs);

    /// <param name="lanAddress">The reachable LAN IPv4 address to serve on while the opt-in is on; null keeps the share on loopback only.</param>
    /// <exception cref="ArgumentException">A LAN address that is loopback or unspecified (producer rule 1).</exception>
    public void Start(string? lanAddress, int port)
    {
        _port = port;
        _loopbackListener = new TcpListener(IPAddress.Loopback, port);
        _loopbackListener.Start(backlog: 8);
        _ = AcceptAsync(_loopbackListener, countsTowardCap: false);
        SetLanAddress(lanAddress);
    }

    /// <summary>Changing the LAN opt-in closes LAN clients; loopback tunnels keep their independent connection.</summary>
    public void SetLanAddress(string? lanAddress)
    {
        _lanListener?.Stop();
        _lanListener = null;
        lock (_gate)
        {
            foreach (var client in _clients.Where(client => client.CountsTowardCap))
            {
                client.Queue.Writer.TryComplete();
                client.Tcp.Dispose();
            }
        }
        if (lanAddress is not null)
        {
            if (!IPAddress.TryParse(lanAddress, out var address) || IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any))
            {
                throw new ArgumentException("The LAN address must be a reachable IPv4 address.", nameof(lanAddress));
            }

            _lanListener = new TcpListener(address, _port);
            _lanListener.Start(backlog: 8);
            _ = AcceptAsync(_lanListener, countsTowardCap: true);
        }
    }

    /// <summary>Hands one whole frame to every connected listener; a listener that has fallen behind loses its oldest frames, never the live edge.</summary>
    public void Enqueue(ReadOnlyMemory<byte> frame, bool joinable = true)
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
                if (!client.Live || client.WaitingForKeyframe && !joinable) continue;
                client.WaitingForKeyframe = false;
                if (!client.Queue.Writer.TryWrite(copy) && _options.IsVideo)
                {
                    // Dropping a predicted frame would leave undecodable bytes until another IDR.
                    client.Queue.Writer.TryComplete();
                    client.Tcp.Dispose();
                }
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
        Authenticator.PairsChanged -= CloseListenersNoLongerAdmitted;
        await _stopping.CancelAsync().ConfigureAwait(false);
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

            var session = new ClientSession(tcp, countsTowardCap, _options.IsVideo);
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
        var (admitted, grant) = await AdmitAsync(session, request).ConfigureAwait(false);
        if (!admitted)
        {
            return;
        }

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

        // Admitted and made live under one lock: a pair swap that lands between the check above and here is seen by
        // this test, and one that lands after is seen by CloseListenersNoLongerAdmitted, which scans the same lock.
        bool stillAdmitted;
        lock (_gate)
        {
            stillAdmitted = IsAdmitted(grant);
            if (stillAdmitted)
            {
                session.Grant = grant;
                session.Live = true;
            }
        }

        if (!stillAdmitted)
        {
            await RefuseAsync(session, Authenticator.Check("GET", "", null)).ConfigureAwait(false);
            return;
        }

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

    /// <summary>
    /// The Digest gate of one request. With no pair set it admits at once and the request is served as it always was;
    /// with pairs, any request that does not prove one - a missing, wrong, unknown-user or replayed answer, a path this
    /// broadcast does not serve, a method other than GET - gets the same 401 and the stream's bytes never leave.
    /// </summary>
    /// <returns>Whether to go on, and the credential that admitted the request (null when none was asked for).</returns>
    private async Task<(bool Admitted, BroadcastAuthGrant? Grant)> AdmitAsync(ClientSession session, string? head)
    {
        if (!Authenticator.Required)
        {
            return (true, null);
        }

        var (method, target, authorization) = ReadRequestLine(head);
        var result = Authenticator.Check(method ?? "", target ?? "", authorization);
        if (result.Allowed)
        {
            _damper.RegisterSuccess(session.Source);
            return (true, result.Grant);
        }

        if (result.CredentialsRefused)
        {
            try
            {
                await Task.Delay(_damper.RegisterFailure(session.Source), _stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The server is stopping; the peer is owed no answer.
                return (false, null);
            }
        }

        await RefuseAsync(session, result).ConfigureAwait(false);
        return (false, null);
    }

    /// <summary>The same 401 for every refusal: both challenges, no body, and the connection closed.</summary>
    private static async Task RefuseAsync(ClientSession session, AuthResult challenge)
    {
        using var write = new CancellationTokenSource(RequestReadTimeout);
        await WriteSimpleResponseAsync(session.Tcp, "401 Unauthorized", close: true, write.Token, challenge.Challenge).ConfigureAwait(false);
    }

    /// <summary>True when a listener holding <paramref name="grant"/> (null: admitted while nothing was required) may keep listening.</summary>
    private bool IsAdmitted(BroadcastAuthGrant? grant) =>
        !Authenticator.Required || grant is not null && Authenticator.IsCurrent(grant);

    private void CloseListenersNoLongerAdmitted()
    {
        lock (_gate)
        {
            foreach (var client in _clients.Where(client => client.Live && !IsAdmitted(client.Grant)))
            {
                client.Queue.Writer.TryComplete();
                client.Tcp.Dispose();
            }
        }
    }

    /// <summary>The method, the request-target as received and the first <c>Authorization</c> value; nulls for a head that is not an HTTP request.</summary>
    private static (string? Method, string? Target, string? Authorization) ReadRequestLine(string? head)
    {
        if (head is null)
        {
            return default;
        }

        var lines = head.Split('\n');
        var parts = lines[0].Trim().Split(' ');
        if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0)
        {
            return default;
        }

        const string field = "Authorization:";
        var authorization = lines.Skip(1)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith(field, StringComparison.OrdinalIgnoreCase));
        return (parts[0], parts[1], authorization?[field.Length..].Trim());
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

    private static async Task WriteSimpleResponseAsync(
        TcpClient tcp, string status, bool close, CancellationToken cancellationToken, IReadOnlyList<string>? challenge = null)
    {
        var response = "HTTP/1.1 " + status + "\r\n"
            + string.Concat((challenge ?? []).Select(value => "WWW-Authenticate: " + value + "\r\n"))
            + "Content-Length: 0\r\n"
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

    private sealed class ClientSession(TcpClient tcp, bool countsTowardCap, bool video)
    {
        public TcpClient Tcp { get; } = tcp;

        /// <summary>Read once at accept: the socket's remote end is not asked again after it may have been closed.</summary>
        public IPAddress? Source { get; } = (tcp.Client.RemoteEndPoint as IPEndPoint)?.Address;
        public bool CountsTowardCap { get; } = countsTowardCap;
        public Channel<byte[]> Queue { get; } = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(video ? 6 : PerListenerQueueFrames)
            { FullMode = video ? BoundedChannelFullMode.Wait : BoundedChannelFullMode.DropOldest, SingleReader = true });
        public Task Session { get; set; } = Task.CompletedTask;
        public CancellationTokenSource? Cancellation { get; set; }
        public bool Live { get; set; }
        public BroadcastAuthGrant? Grant { get; set; }
        public bool WaitingForKeyframe { get; set; } = video;
    }
}
