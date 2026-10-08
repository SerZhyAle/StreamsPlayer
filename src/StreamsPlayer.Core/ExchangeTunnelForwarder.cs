using System.Net;
using System.Net.Security;
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

    private readonly TcpListener _listener;
    private readonly ExchangeTunnelUrl _tunnelUrl;
    private readonly string _innerUrl;
    private readonly string? _certFingerprint;
    private readonly int _maxConnections;
    private readonly Func<string, int, string?, CancellationToken, Task<Stream>> _streamConnector;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<IDisposable> _activeResources = [];
    private readonly object _lock = new();
    private int _activeConnections;
    private bool _disposed;

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
        Func<string, int, string?, CancellationToken, Task<Stream>>? streamConnector)
    {
        _tunnelUrl = tunnelUrl;
        _innerUrl = innerUrl;
        _certFingerprint = certFingerprint;
        _maxConnections = maxConnections > 0 ? maxConnections : DefaultMaxConnections;
        _streamConnector = streamConnector ?? DefaultConnectTlsAsync;

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        LoopbackUrl = ExchangeTunnelUrl.RewriteInner(_innerUrl, BoundPort);
    }

    public static ExchangeTunnelForwarder Start(
        string fmsxUrl,
        string innerUrl,
        string? certFingerprint = null,
        int maxConnections = DefaultMaxConnections,
        Func<string, int, string?, CancellationToken, Task<Stream>>? streamConnector = null)
    {
        if (!ExchangeTunnelUrl.TryParse(fmsxUrl, out var tunnelUrl))
        {
            throw new ArgumentException("Invalid fmsx tunnel URL.", nameof(fmsxUrl));
        }

        var forwarder = new ExchangeTunnelForwarder(tunnelUrl, innerUrl, certFingerprint, maxConnections, streamConnector);
        forwarder.StartAcceptLoop();
        return forwarder;
    }

    public static ExchangeTunnelForwarder Start(
        ExchangeTunnelUrl tunnelUrl,
        string innerUrl,
        string? certFingerprint = null,
        int maxConnections = DefaultMaxConnections,
        Func<string, int, string?, CancellationToken, Task<Stream>>? streamConnector = null)
    {
        var forwarder = new ExchangeTunnelForwarder(tunnelUrl, innerUrl, certFingerprint, maxConnections, streamConnector);
        forwarder.StartAcceptLoop();
        return forwarder;
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
                    lock (_lock)
                    {
                        if (_disposed || _cts.IsCancellationRequested)
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
        Stream? exchangeStream = null;
        try
        {
            ConnectAttempts++;
            exchangeStream = await _streamConnector(_tunnelUrl.Host, _tunnelUrl.Port, _certFingerprint, cancellationToken).ConfigureAwait(false);
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

            await ExchangeProtocol.WriteAsync(exchangeStream, connectEnvelope, authenticated: false, cancellationToken).ConfigureAwait(false);
            using var responseDoc = await ExchangeProtocol.ReadAsync(exchangeStream, authenticated: false, cancellationToken).ConfigureAwait(false);
            var responseType = ExchangeProtocol.String(responseDoc.RootElement, "type");
            if (responseType != "connected")
            {
                var reason = ExchangeProtocol.String(responseDoc.RootElement, "reason") ?? "unavailable";
                LastRefusalReason = reason;
                LastRefusalKey = ExchangeProtocol.RefusalKey(reason);
                Refused?.Invoke(reason);
                return;
            }

            ConnectedCount++;
            using var clientStream = client.GetStream();
            var forward = SpliceCopyAsync(clientStream, exchangeStream, cancellationToken);
            var backward = SpliceCopyAsync(exchangeStream, clientStream, cancellationToken);
            await Task.WhenAny(forward, backward).ConfigureAwait(false);
        }
        catch (Exception) when (!_disposed && !cancellationToken.IsCancellationRequested)
        {
            // Socket or handshake error; close both sides cleanly.
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

    private static async Task SpliceCopyAsync(Stream source, Stream destination, CancellationToken cancellationToken)
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

    private static async Task<Stream> DefaultConnectTlsAsync(string host, int port, string? certFingerprint, CancellationToken cancellationToken)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            string presentedFingerprint = "";
            var sslStream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false, (_, certificate, _, errors) =>
            {
                if (certificate is null)
                {
                    return false;
                }

                presentedFingerprint = ExchangeProtocol.Fingerprint(certificate.GetRawCertData());
                if (!string.IsNullOrWhiteSpace(certFingerprint))
                {
                    return string.Equals(certFingerprint, presentedFingerprint, StringComparison.Ordinal);
                }

                return errors == SslPolicyErrors.None;
            });

            await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host
            }, cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(certFingerprint) &&
                !string.Equals(certFingerprint, presentedFingerprint, StringComparison.Ordinal))
            {
                sslStream.Dispose();
                throw new AuthenticationException($"Certificate fingerprint mismatch. Expected '{certFingerprint}', presented '{presentedFingerprint}'.");
            }

            return sslStream;
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
            _activeConnections = 0;
        }

        _cts.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
