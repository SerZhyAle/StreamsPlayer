using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0203 requirement 7 and the seam tests of the acceptance criteria: a pinned relay endpoint
/// plays through the loopback proxy, a wrong fingerprint is refused (and never retried with
/// checking off), the proxy serves one connection and refuses a second, and a guessed path gets
/// nothing. The relay is a real in-process TLS server with a self-signed leaf, so the pin decision
/// runs the production path.
/// </summary>
public sealed class ExchangeRelayProxyTests
{
    private static X509Certificate2 SelfSignedLeaf()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=streamsplayer-relay-test", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        return X509CertificateLoader.LoadPkcs12(
            request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7)).Export(X509ContentType.Pfx), null);
    }

    /// <summary>A stand-in relay: TLS on loopback, one GET answered with the contract's live shape.</summary>
    private sealed class FakeRelay(X509Certificate2 certificate, byte[] body) : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        public int BoundPort { get; private set; }
        public int Requests;

        public void Start()
        {
            _listener.Start();
            BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Task.Run(async () =>
            {
                while (true)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(); }
                    catch { return; }

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate });
                            var head = new byte[4096];
                            int filled = 0;
                            while (filled < head.Length && !Encoding.ASCII.GetString(head, 0, filled).Contains("\r\n\r\n"))
                            {
                                int read = await ssl.ReadAsync(head.AsMemory(filled));
                                if (read == 0) return;
                                filled += read;
                            }

                            Interlocked.Increment(ref Requests);
                            await ssl.WriteAsync(Encoding.ASCII.GetBytes(
                                "HTTP/1.1 200 OK\r\nContent-Type: audio/aac\r\nTransfer-Encoding: chunked\r\nCache-Control: no-cache\r\n\r\n"));
                            await ssl.WriteAsync(Encoding.ASCII.GetBytes($"{body.Length:X}\r\n"));
                            await ssl.WriteAsync(body);
                            await ssl.WriteAsync("\r\n0\r\n\r\n"u8.ToArray());
                        }
                        catch (IOException)
                        {
                            // The client walked away mid-handshake - exactly what a refused pin does.
                        }
                        finally
                        {
                            client.Dispose();
                        }
                    });
                }
            });
        }

        public string Url => $"https://127.0.0.1:{BoundPort}/v2/b/b1/stream";

        public void Dispose() => _listener.Stop();
    }

    private static async Task<(int Status, byte[] Body)> GetAsync(string url)
    {
        using var client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                // The loopback URL is plain http; this callback would never fire. Kept off.
            }
        }) { Timeout = TimeSpan.FromSeconds(10) };
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseContentRead);
        return ((int)response.StatusCode, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task TheRightFingerprintPlaysThroughTheProxy()
    {
        using var leaf = SelfSignedLeaf();
        var body = "live-bytes"u8.ToArray();
        using var relay = new FakeRelay(leaf, body);
        relay.Start();
        string pin = ExchangeProtocol.Fingerprint(leaf.GetRawCertData());
        using var proxy = ExchangeRelayProxy.Start(relay.Url, pin);
        var (status, received) = await GetAsync(proxy.LoopbackUrl);
        Assert.Equal(200, status);
        Assert.Equal(body, received);
        Assert.Equal(1, proxy.ConnectedCount);
        Assert.Equal(1, relay.Requests);
    }

    [Fact]
    public async Task TheWrongFingerprintIsRefusedAndNeverPlayedUnchecked()
    {
        using var leaf = SelfSignedLeaf();
        using var other = SelfSignedLeaf();
        using var relay = new FakeRelay(leaf, "live-bytes"u8.ToArray());
        relay.Start();
        string wrongPin = ExchangeProtocol.Fingerprint(other.GetRawCertData());
        var failure = new string?[1];
        using var proxy = ExchangeRelayProxy.Start(relay.Url, wrongPin);
        proxy.Failed += reason => failure[0] = reason;
        var (status, _) = await GetAsync(proxy.LoopbackUrl);
        Assert.Equal(502, status);
        Assert.Equal("certificate-refused", failure[0]);
        Assert.Equal("certificate-refused", proxy.LastFailure);
        Assert.Equal(0, relay.Requests); // the TLS handshake never completed into a request
    }

    [Fact]
    public async Task ASecondConnectionIsRefused()
    {
        using var leaf = SelfSignedLeaf();
        using var relay = new FakeRelay(leaf, "live-bytes"u8.ToArray());
        relay.Start();
        using var proxy = ExchangeRelayProxy.Start(relay.Url, ExchangeProtocol.Fingerprint(leaf.GetRawCertData()));
        var (status, body) = await GetAsync(proxy.LoopbackUrl);
        Assert.Equal(200, status);
        Assert.Equal("live-bytes", Encoding.ASCII.GetString(body));
        // Refused outright: the connection is closed without an answer. The HTTP stack may knock
        // again after a refused connection, but only one connection is ever served.
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => GetAsync(proxy.LoopbackUrl));
        Assert.Equal(1, proxy.ConnectedCount);
        Assert.Equal(1, relay.Requests);
        Assert.True(proxy.RefusedCount >= 1);
    }

    [Fact]
    public async Task AGuessedPathIsAnswered404AndServesNothing()
    {
        using var leaf = SelfSignedLeaf();
        using var relay = new FakeRelay(leaf, "live-bytes"u8.ToArray());
        relay.Start();
        using var proxy = ExchangeRelayProxy.Start(relay.Url, ExchangeProtocol.Fingerprint(leaf.GetRawCertData()));
        var guess = $"http://127.0.0.1:{proxy.BoundPort}/not-the-token";
        var (status, _) = await GetAsync(guess);
        Assert.Equal(404, status);
        Assert.Equal(0, relay.Requests);
        Assert.Equal(0, proxy.ConnectedCount);
    }

    [Fact]
    public void APlainHttpRelayAddressIsRefusedAtStart()
    {
        Assert.Throws<ArgumentException>(() => ExchangeRelayProxy.Start("http://127.0.0.1:8443/v2/b/b1/stream", null));
    }

    [Fact]
    public async Task DisposalClosesTheListenerAndTheUrl()
    {
        using var leaf = SelfSignedLeaf();
        using var relay = new FakeRelay(leaf, "live-bytes"u8.ToArray());
        relay.Start();
        var proxy = ExchangeRelayProxy.Start(relay.Url, ExchangeProtocol.Fingerprint(leaf.GetRawCertData()));
        proxy.Dispose();
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => GetAsync(proxy.LoopbackUrl));
    }
}
