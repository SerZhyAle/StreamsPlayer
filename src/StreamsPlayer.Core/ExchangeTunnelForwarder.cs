using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;

namespace StreamsPlayer.Core;

/// <summary>
/// A single-leg loopback TCP forwarder for DEVICE-EXCHANGE broadcast tunnels (SP-0204).
/// Listens on an ephemeral port on 127.0.0.1, converts each accepted connection into one
/// TLS stream with <c>connect {broadcastId, scheme}</c>, splices the data streams on <c>connected</c>,
/// and refuses connections beyond the configured limit.
/// </summary>
public sealed class ExchangeTunnelForwarder : IAsyncDisposable, IDisposable
{
    public const int DefaultMaxConnections = 2;

    // SP-0212 (L26): the connect phase - the TLS authenticate, the connect envelope, its answer - ends
    // after this on its own, because the caller's token outlives it by design and a stalled server would
    // otherwise hold the leg's watcher on a connect that never finishes. The same bound the enrollment
    // probe and the relay head use. Settable so the test project can prove the deadline with a short one;
    // production never sets it.
    internal static TimeSpan ConnectDeadline { get; set; } = TimeSpan.FromSeconds(15);

    private readonly TcpListener _listener;
    private readonly ExchangeTunnelUrl _tunnelUrl;
    private readonly string _innerUrl;
    private readonly string? _certFingerprint;
    private readonly string? _tunnelPin;
    private readonly int _maxConnections;
    private readonly Func<string, int, string?, CancellationToken, Task<Stream>> _streamConnector;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<IDisposable> _activeResources = [];
    private readonly object _lock = new();
    private int _activeConnections;
    private bool _disposed;
    public static readonly TimeSpan IdleTimeout = ExchangeBroadcastResources.IdleTimeout;
    private readonly Timer _idleTimer;
    private readonly IDisposable _lease;

    public int BoundPort { get; }
    public string LoopbackUrl { get; }
    public string? LastRefusalReason { get; private set; }
    public string? LastRefusalKey { get; private set; }
    public int ConnectedCount { get; private set; }
    public int ConnectAttempts { get; private set; }

    public event Action<string>? Refused;

    private ExchangeTunnelForwarder(
        ExchangeTunnelUrl tunnelUrl,
        string innerUrl,
        string? certFingerprint,
        int maxConnections,
        Func<string, int, string?, CancellationToken, Task<Stream>>? streamConnector,
        string? tunnelPin,
        IDisposable lease)
    {
        _lease = lease;
        _tunnelPin = tunnelPin;
        _tunnelUrl = tunnelUrl;
        _innerUrl = innerUrl;
        _certFingerprint = certFingerprint;
        _maxConnections = maxConnections > 0 ? maxConnections : DefaultMaxConnections;
        _streamConnector = streamConnector ?? ExchangeBroadcastCertificateException.ConnectAsync;

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        LoopbackUrl = ExchangeTunnelUrl.RewriteInner(_innerUrl, BoundPort);
        _idleTimer = new Timer(_ => Dispose(), null, IdleTimeout, Timeout.InfiniteTimeSpan);
    }

    public static ExchangeTunnelForwarder Start(
        string fmsxUrl,
        string innerUrl,
        string? certFingerprint = null,
        int maxConnections = DefaultMaxConnections,
        Func<string, int, string?, CancellationToken, Task<Stream>>? streamConnector = null,
        string? tunnelPin = null)
    {
        if (!ExchangeTunnelUrl.TryParse(fmsxUrl, out var tunnelUrl))
        {
            throw new ArgumentException("Invalid fmsx tunnel URL.", nameof(fmsxUrl));
        }

        return Start(tunnelUrl, innerUrl, certFingerprint, maxConnections, streamConnector, tunnelPin);
    }

    public static ExchangeTunnelForwarder Start(
        ExchangeTunnelUrl tunnelUrl,
        string innerUrl,
        string? certFingerprint = null,
        int maxConnections = DefaultMaxConnections,
        Func<string, int, string?, CancellationToken, Task<Stream>>? streamConnector = null,
        string? tunnelPin = null)
    {
        var forwarder = ExchangeBroadcastResources.Start(lease =>
            new ExchangeTunnelForwarder(tunnelUrl, innerUrl, certFingerprint, maxConnections, streamConnector, tunnelPin, lease));
        forwarder.StartAcceptLoop();
        return forwarder;
    }

    private void StartAcceptLoop()
    {
        var token = _cts.Token;
        Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                    lock (_lock)
                    {
                        if (_disposed || token.IsCancellationRequested)
                        {
                            client.Dispose();
                            break;
                        }

                        if (_activeConnections >= _maxConnections)
                        {
                            // SP-0204 acceptance: refuse connection beyond the bound.
                            client.Dispose();
                            continue;
                        }

                        _activeConnections++;
                        _activeResources.Add(client);
                    }

                    _ = Task.Run(() => HandleConnectionAsync(client, token));
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
        Stream? exchangeStream = null;
        try
        {
            ConnectAttempts++;
            // SP-0212 (L26): the deadline covers the connect phase only; once the exchange answers, the
            // splices run on the caller's token, because a live body has no deadline of the tunnel's.
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connect.CancelAfter(ConnectDeadline);
            exchangeStream = await _streamConnector(_tunnelUrl.Host, _tunnelUrl.Port, _certFingerprint, connect.Token).ConfigureAwait(false);
            lock (_lock)
            {
                if (_disposed || cancellationToken.IsCancellationRequested)
                {
                    client.Dispose();
                    exchangeStream.Dispose();
                    return;
                }

                _activeResources.Add(exchangeStream);
            }

            var connectEnvelope = new
            {
                schemaVersion = 2,
                type = "connect",
                broadcastId = _tunnelUrl.BroadcastId,
                scheme = _tunnelUrl.Scheme
            };

            await ExchangeProtocol.WriteAsync(exchangeStream, connectEnvelope, authenticated: false, connect.Token).ConfigureAwait(false);
            using var responseDoc = await ExchangeProtocol.ReadAsync(exchangeStream, authenticated: false, connect.Token).ConfigureAwait(false);
            var responseType = ExchangeProtocol.String(responseDoc.RootElement, "type");
            if (responseType != "connected")
            {
                var reason = ExchangeProtocol.String(responseDoc.RootElement, "reason") ?? "unavailable";
                LastRefusalReason = reason;
                LastRefusalKey = ExchangeProtocol.RefusalKey(reason);
                Refused?.Invoke(reason);
                return;
            }

            // SP-0226 / item DM: a sealed tunnel runs TLS inside, accepted only on the leaf the broadcast's pin names;
            // the bytes the server relays are TLS records and nothing else.
            Stream innerStream = exchangeStream;
            if (_tunnelPin is not null)
            {
                innerStream = await ExchangeTunnelTls.ConnectAsync(exchangeStream, _tunnelPin, connect.Token).ConfigureAwait(false);
                lock (_lock)
                {
                    _activeResources.Add(innerStream);
                }
            }

            ConnectedCount++;
            using var clientStream = client.GetStream();
            var forward = SpliceCopyAsync(clientStream, innerStream, cancellationToken);
            var backward = SpliceCopyAsync(innerStream, clientStream, cancellationToken);
            await Task.WhenAny(forward, backward).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or SocketException or AuthenticationException
                                          or OperationCanceledException or ObjectDisposedException or System.Text.Json.JsonException)
        {
            if (!_disposed && !cancellationToken.IsCancellationRequested)
            {
                LastRefusalReason = exception is ExchangeBroadcastCertificateException certificate
                    ? certificate.Reason : exception.GetType().Name;
                LastRefusalKey = ExchangeBroadcastCertificateException.MessageKey(LastRefusalReason)
                    ?? ExchangeProtocol.RefusalKey(LastRefusalReason);
                Refused?.Invoke(LastRefusalReason);
            }
        }
        finally
        {
            lock (_lock)
            {
                _activeConnections = Math.Max(0, _activeConnections - 1);
                _activeResources.Remove(client);
                if (exchangeStream is not null)
                {
                    _activeResources.Remove(exchangeStream);
                }
            }

            client.Dispose();
            exchangeStream?.Dispose();
        }
    }

    private async Task SpliceCopyAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[16384];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var bytesRead = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                NoteActivity();
                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation.
        }
        catch (IOException)
        {
            // Connection closed by peer.
        }
        catch (ObjectDisposedException)
        {
            // Socket/stream closed.
        }
    }

    private void NoteActivity()
    {
        lock (_lock)
        {
            if (!_disposed)
                _idleTimer.Change(IdleTimeout, Timeout.InfiniteTimeSpan);
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

        _idleTimer.Dispose();
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
            _activeConnections = 0;
        }

        _cts.Dispose();
        _lease.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
