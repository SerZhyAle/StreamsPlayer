using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;

namespace StreamsPlayer.Core;

/// <summary>
/// A protected producer's engine-independent Digest connection. Only the upstream sees an answer;
/// the player sees an opaque loopback path. A tunnel may supply a different socket address while
/// Digest and RTSP control still name the producer's original address.
/// </summary>
public sealed partial class BroadcastDigestProxy : IDisposable
{
    private readonly Uri _target;
    private readonly Uri _connect;
    private readonly ExchangeRelayCredentials _credentials;
    private readonly string? _pin;
    private readonly Func<IPEndPoint, int, bool>? _admitPeer;
    private readonly TimeSpan _connectTimeout;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly IDisposable _lease;
    private readonly Timer _idle;
    private readonly string _prefix = "/" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    private readonly Task _run;
    private int _disposed;

    public int BoundPort { get; }
    public string LoopbackUrl { get; }
    public string? LastFailure { get; private set; }
    public event Action<string>? Failed;
    internal Task Completion => _run;
    private string LocalOrigin => $"{_target.Scheme}://127.0.0.1:{BoundPort}";

    private BroadcastDigestProxy(Uri target, Uri connect, ExchangeRelayCredentials credentials, string? pin,
        Func<IPEndPoint, int, bool>? admitPeer, TimeSpan connectTimeout, IDisposable lease)
    {
        _target = target;
        _connect = connect;
        _credentials = credentials;
        _pin = pin;
        _admitPeer = admitPeer;
        _connectTimeout = connectTimeout;
        _lease = lease;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        // HTTP players do not need TLS for this process-local hop; upstream TLS is checked below.
        LoopbackUrl = $"{(target.Scheme == "rtsp" ? "rtsp" : "http")}://127.0.0.1:{BoundPort}{_prefix}{target.PathAndQuery}";
        _idle = new Timer(_ => Dispose(), null, ExchangeBroadcastResources.IdleTimeout, Timeout.InfiniteTimeSpan);
        _run = RunAsync(_cts.Token);
    }

    public static BroadcastDigestProxy Start(string targetUrl, ExchangeRelayCredentials credentials,
        string? connectUrl = null, string? certFingerprint = null, Func<IPEndPoint, int, bool>? admitPeer = null,
        TimeSpan? connectTimeout = null)
    {
        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var target) || target.Scheme is not ("rtsp" or "http" or "https")
            || target.UserInfo.Length != 0 || target.Fragment.Length != 0
            || !Uri.TryCreate(connectUrl ?? targetUrl, UriKind.Absolute, out var connect) || connect.Scheme != target.Scheme
            || connect.UserInfo.Length != 0)
            throw new ArgumentException("Unsupported protected broadcast address.");
        ArgumentNullException.ThrowIfNull(credentials);
        if (ExchangeRelayCredentials.Create(credentials.User, credentials.Password) is null || credentials.User.Any(char.IsControl))
            throw new ArgumentException("Invalid broadcast credentials.", nameof(credentials));
        return ExchangeBroadcastResources.Start(lease => new BroadcastDigestProxy(target, connect, credentials,
            certFingerprint, admitPeer, connectTimeout ?? TimeSpan.FromSeconds(1.5), lease));
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                if (_admitPeer is not null && (client.Client.RemoteEndPoint is not IPEndPoint peer || !_admitPeer(peer, BoundPort)))
                    continue;
                client.NoDelay = true;
                var local = client.GetStream();
                using var input = new BufferedStream(local, 16 * 1024);
                using var headTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                headTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                var first = await DigestRtspMessage.ReadAsync(input, headTimeout.Token).ConfigureAwait(false);
                if (first is null || !TryTarget(first.StartLine, out _)) continue;
                Touch();
                if (_target.Scheme == "rtsp")
                    await RunRtspAsync(input, local, first, token).ConfigureAwait(false);
                else
                    await RunHttpAsync(local, first, token).ConfigureAwait(false);
                break; // One media connection per leg; a reconnect gets a new proxy and challenge.
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException
            or ObjectDisposedException or HttpRequestException or AuthenticationException or InvalidOperationException
            or InvalidDataException) // S10-1: a malformed RTSP message is a failure to report, not an unobserved fault
        {
            if (!token.IsCancellationRequested)
                Fail(exception is ExchangeBroadcastCertificateException certificate ? certificate.Reason : ConnectionFailure(exception));
        }
        finally { Dispose(); }
    }

    private async Task RunHttpAsync(Stream local, DigestRtspMessage request, CancellationToken token)
    {
        if (request.StartLine.Split(' ')[0] != "GET") { Fail(ExchangeBroadcastAuth.UnsupportedEndpointReason); return; }
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = _connectTimeout
        };
        if (_connect.Scheme == "https")
        {
            handler.SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = ExchangeCertificatePin.ClientProtocols,
                RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                {
                    var accepted = ExchangeCertificatePin.Accepts(certificate, _pin, errors);
                    if (!accepted) Fail(string.IsNullOrWhiteSpace(_pin)
                        ? ExchangeBroadcastCertificateException.UntrustedWithoutFingerprint : ExchangeBroadcastCertificateException.Refused);
                    return accepted;
                }
            };
        }
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        BroadcastDigestClient? digest = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var get = new HttpRequestMessage(HttpMethod.Get, _connect);
            if (digest is not null) get.Headers.TryAddWithoutValidation("Authorization", digest.Answer(_credentials, "GET", _target.PathAndQuery));
            using var response = await client.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                var next = BroadcastDigestClient.Select(response.Headers.WwwAuthenticate.Select(value => value.ToString()));
                if (digest is not null && (next is null || !next.Stale || attempt == 2))
                { Fail(ExchangeBroadcastAuth.PasswordRefusedReason); return; }
                if (next is null) { Fail(ExchangeBroadcastAuth.UnsupportedEndpointReason); return; }
                digest = next;
                continue;
            }
            // A descriptor with auth must never become a clear route because its producer omitted a challenge.
            if (response.IsSuccessStatusCode && digest is null) { Fail(ExchangeBroadcastAuth.UnsupportedEndpointReason); return; }
            if (!response.IsSuccessStatusCode)
            {
                Fail($"broadcast-http-refused-{(int)response.StatusCode}");
                await local.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.0 {(int)response.StatusCode} Refused\r\nConnection: close\r\n\r\n"), token).ConfigureAwait(false);
                return;
            }
            var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
            await local.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.0 200 OK\r\nContent-Type: {contentType}\r\nConnection: close\r\n\r\n"), token).ConfigureAwait(false);
            await using var body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await body.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                await local.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                Touch();
            }
            return;
        }
        Fail(ExchangeBroadcastAuth.PasswordRefusedReason);
    }

    private bool TryTarget(string startLine, out string target)
    {
        target = "";
        var parts = startLine.Split(' ');
        if (parts.Length != 3) return false;
        if (parts[1] == "*" && _target.Scheme == "rtsp") { target = "*"; return true; }
        var path = Uri.TryCreate(parts[1], UriKind.Absolute, out var uri) && uri.Scheme == "rtsp"
            && uri.GetLeftPart(UriPartial.Authority) == LocalOrigin ? uri.PathAndQuery : parts[1];
        if (!path.StartsWith(_prefix + "/", StringComparison.Ordinal)) return false;
        target = _target.GetLeftPart(UriPartial.Authority) + path[_prefix.Length..];
        return true;
    }

    private void Touch()
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            try { _idle.Change(ExchangeBroadcastResources.IdleTimeout, Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { /* Disposal raced a completed read. */ }
        }
    }

    /// <summary>
    /// SP-0250: a producer's LAN port that refuses the connection is the answer item T reads as an ended broadcast. Only a
    /// connection made to the producer's own address says so; one made to a tunnel's loopback end says nothing about it.
    /// </summary>
    private string ConnectionFailure(Exception exception) =>
        _connect.Authority == _target.Authority && BroadcastEndAnswers.FromLanHttp(null, exception) == BroadcastEndAnswer.LanRefused
            ? BroadcastEndAnswers.LanRefusedReason
            : "broadcast-digest-connection-failed";

    private void Fail(string reason)
    {
        if (LastFailure is not null) return;
        LastFailure = reason;
        Failed?.Invoke(reason);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        _listener.Stop();
        _idle.Dispose();
        _lease.Dispose();
    }
}
