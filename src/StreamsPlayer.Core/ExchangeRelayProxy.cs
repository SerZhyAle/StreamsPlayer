using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0203 requirement 7: the loopback proxy that pins a relay endpoint's certificate for a playback
/// engine that cannot pin it itself (the engine spike verdict). It binds 127.0.0.1 on an ephemeral
/// port, takes a request path no other program can guess, serves exactly one connection - the
/// player's - dials the relay over TLS where <see cref="ExchangeCertificatePin.Accepts"/> is the
/// whole decision, and splices the relay's live body to that connection. It exists for one leg and
/// is closed with it: while nothing plays, nothing listens.
/// </summary>
public sealed class ExchangeRelayProxy : IDisposable
{
    /// <summary>Requirement 7: one leg, one connection. A second connection is refused outright.</summary>
    public const int MaximumConnections = 1;

    private const int PlayerHeadLimit = 8 * 1024;
    private const int RelayHeadLimit = 16 * 1024;
    private static readonly TimeSpan PlayerHeadTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RelayHeadTimeout = TimeSpan.FromSeconds(15);

    private readonly Uri _relayUrl;
    private readonly string? _certFingerprint;
    private readonly Func<string, int, string?, CancellationToken, Task<Stream>> _streamConnector;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    private readonly object _lock = new();
    private readonly List<IDisposable> _activeResources = [];
    private int _claimed;
    private bool _disposed;

    public int BoundPort { get; }
    public string LoopbackUrl { get; }
    public int ConnectAttempts { get; private set; }
    public int ConnectedCount { get; private set; }
    public int RefusedCount { get; private set; }
    public string? LastFailure { get; private set; }

    /// <summary>Raised when the one connection's upstream failed (TLS, pin, relay status); the message names the cause, never the pin itself.</summary>
    public event Action<string>? Failed;

    private ExchangeRelayProxy(
        Uri relayUrl,
        string? certFingerprint,
        Func<string, int, string?, CancellationToken, Task<Stream>>? streamConnector)
    {
        _relayUrl = relayUrl;
        _certFingerprint = certFingerprint;
        _streamConnector = streamConnector ?? DefaultConnectAsync;

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        LoopbackUrl = $"http://127.0.0.1:{BoundPort}/{_token}";
    }

    public static ExchangeRelayProxy Start(
        string relayUrl,
        string? certFingerprint,
        Func<string, int, string?, CancellationToken, Task<Stream>>? streamConnector = null)
    {
        if (!Uri.TryCreate(relayUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("The relay listen URL must be an absolute https address.", nameof(relayUrl));
        }

        var proxy = new ExchangeRelayProxy(url, certFingerprint, streamConnector);
        proxy.StartAcceptLoop();
        return proxy;
    }

    private void StartAcceptLoop()
    {
        Task.Run(async () =>
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                    _ = Task.Run(() => HandleConnectionAsync(client, _cts.Token));
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
            }
            catch (ObjectDisposedException)
            {
                // Listener stopped.
            }
            catch (SocketException)
            {
                // Listener closed.
            }
        });
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        Stream? relayStream = null;
        bool claimed = false;
        try
        {
            var (valid, request) = await ReadPlayerRequestAsync(client, cancellationToken).ConfigureAwait(false);
            if (!valid)
            {
                // A wrong path is no listener: answer and go. The one real connection stays unserved.
                await WriteClientHeadAsync(client, "HTTP/1.0 404 Not Found\r\nConnection: close\r\n\r\n").ConfigureAwait(false);
                return;
            }

            lock (_lock)
            {
                if (_disposed || _claimed >= MaximumConnections)
                {
                    RefusedCount++;
                    return; // refuse outright: close without an answer (requirement 7)
                }

                _claimed++;
                claimed = true;
                _activeResources.Add(client);
            }

            ConnectAttempts++;
            relayStream = await _streamConnector(_relayUrl.Host, _relayUrl.Port, _certFingerprint, cancellationToken).ConfigureAwait(false);
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _activeResources.Add(relayStream);
            }

            await relayStream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
            await relayStream.FlushAsync(cancellationToken).ConfigureAwait(false);

            var (head, leftover) = await ReadRelayHeadAsync(relayStream, cancellationToken).ConfigureAwait(false);
            await WriteClientHeadAsync(client, RewriteHead(head)).ConfigureAwait(false);
            ConnectedCount++;
            await SpliceAsync(relayStream, leftover, client, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException ||
                                          exception is SocketException or AuthenticationException)
        {
            LastFailure = exception switch
            {
                AuthenticationException => "certificate-refused",
                OperationCanceledException => "cancelled",
                _ => exception.GetType().Name
            };
            Failed?.Invoke(LastFailure);
            if (claimed)
            {
                await WriteClientHeadAsync(client, "HTTP/1.0 502 Bad Gateway\r\nConnection: close\r\n\r\n").ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_lock)
            {
                _activeResources.Remove(client);
                if (relayStream is not null)
                {
                    _activeResources.Remove(relayStream);
                }
            }

            client.Dispose();
            relayStream?.Dispose();
        }
    }

    /// <summary>Reads the player's request head and answers whether the unguessable path was used.</summary>
    private async Task<(bool Valid, byte[] Request)> ReadPlayerRequestAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var head = new byte[PlayerHeadLimit];
        int filled = 0;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PlayerHeadTimeout);
        var stream = client.GetStream();
        while (filled < head.Length)
        {
            int read = await stream.ReadAsync(head.AsMemory(filled), timeout.Token).ConfigureAwait(false);
            if (read == 0)
            {
                return (false, []);
            }

            filled += read;
            if (TryFindHeadEnd(head, filled, out _))
            {
                break;
            }
        }

        // The token is the whole access rule: anything else is answered 404 and never claimed.
        var line = Encoding.ASCII.GetString(head, 0, Math.Min(filled, 2048)).Split("\r\n")[0];
        return (line.Contains($"/{_token} ", StringComparison.Ordinal), BuildRelayRequest());
    }

    private byte[] BuildRelayRequest()
    {
        var builder = new StringBuilder();
        builder.Append("GET ").Append(_relayUrl.PathAndQuery).Append(" HTTP/1.1\r\n");
        builder.Append("Host: ").Append(_relayUrl.Authority).Append("\r\n");
        builder.Append("User-Agent: StreamsPlayer/0.1\r\n");
        builder.Append("Accept: */*\r\n");
        builder.Append("Connection: close\r\n\r\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    private static async Task<(byte[] Head, byte[] Leftover)> ReadRelayHeadAsync(Stream relay, CancellationToken cancellationToken)
    {
        var buffer = new byte[RelayHeadLimit];
        int filled = 0;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RelayHeadTimeout);
        while (filled < buffer.Length)
        {
            int read = await relay.ReadAsync(buffer.AsMemory(filled), timeout.Token).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("The relay closed before sending a response head.");
            }

            filled += read;
            if (TryFindHeadEnd(buffer, filled, out var headEnd))
            {
                var leftover = new byte[filled - headEnd];
                Array.Copy(buffer, headEnd, leftover, 0, leftover.Length);
                var head = new byte[headEnd];
                Array.Copy(buffer, head, headEnd);
                return (head, leftover);
            }
        }

        throw new IOException("The relay response head exceeded the bound.");
    }

    private static bool TryFindHeadEnd(byte[] buffer, int filled, out int headEnd)
    {
        headEnd = 0;
        var limit = Math.Min(filled, buffer.Length - 3);
        for (int i = 0; i < limit; i++)
        {
            if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
            {
                headEnd = i + 4;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The relay's own head passes through so the player sees the producer's Content-Type and the
    /// chunked framing the contract states - only the connection lifetime is rewritten, so the body
    /// ends exactly when the relay's does.
    /// </summary>
    private static string RewriteHead(byte[] head)
    {
        var text = Encoding.ASCII.GetString(head);
        var lines = text.Split("\r\n");
        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            if (line.Length == 0 || line.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Keep-Alive:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            builder.Append(line).Append("\r\n");
        }

        builder.Append("Connection: close\r\n\r\n");
        return builder.ToString();
    }

    private static async Task WriteClientHeadAsync(TcpClient client, string head)
    {
        try
        {
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head)).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or SocketException)
        {
            // The player is already gone; the refusal would have nowhere to land.
        }
    }

    private static async Task SpliceAsync(Stream relay, byte[] leftover, TcpClient client, CancellationToken cancellationToken)
    {
        var playerStream = client.GetStream();
        if (leftover.Length > 0)
        {
            await playerStream.WriteAsync(leftover, cancellationToken).ConfigureAwait(false);
            await playerStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        var buffer = new byte[16384];
        while (!cancellationToken.IsCancellationRequested)
        {
            int read = await relay.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break; // the broadcast ended; the client's Connection: close makes this the end too
            }

            await playerStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            await playerStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<Stream> DefaultConnectAsync(string host, int port, string? certFingerprint, CancellationToken cancellationToken)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false,
                (_, certificate, _, chainErrors) => ExchangeCertificatePin.Accepts(certificate, certFingerprint, chainErrors));
            try
            {
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                ssl.Dispose();
                throw;
            }

            return ssl;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _cts.Cancel();
        try
        {
            _listener.Stop();
        }
        catch
        {
            // Listener stop exception ignored.
        }

        lock (_lock)
        {
            foreach (var resource in _activeResources)
            {
                try
                {
                    resource.Dispose();
                }
                catch
                {
                    // Ignore disposal errors.
                }
            }

            _activeResources.Clear();
            _claimed = 0;
        }

        _cts.Dispose();
    }
}
