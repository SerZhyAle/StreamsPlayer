using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using StreamsPlayer.App;
using StreamsPlayer.Core;

namespace StreamsPlayer.Exchange.Tests;

[Collection("Foreign broadcast resources")]
public sealed class ForeignBroadcastTests
{
    private const string RelayId = "foreign-relay-capability";
    private const string TunnelId = "foreign-tunnel-capability";
    private const string TunnelPath = "/private-foreign-audio.aac";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwoServersPlayWithoutTouchingAThirdServersAccountOrLeakingCapabilities(bool configured)
    {
        var directory = Path.Combine(Path.GetTempPath(), "StreamsPlayer-SP0215-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new ExchangeAccountStore(directory);
            var accountPath = Path.Combine(directory, "exchange-account.bin");
            if (configured)
                store.Save(new ExchangeAccount(ExchangeProtocol.NewDeviceId(), "third.example", 44022,
                    "owner", new string('t', 43), "SHA256:" + new string('a', 43), Enabled: false));
            var before = configured ? File.ReadAllBytes(accountPath) : null;
            using var relay = new TlsFixture(tunnel: false);
            using var tunnel = new TlsFixture(tunnel: true);
            using var log = new CurrentLog(directory);

            // More sequential openings than either transport's connection ceiling; each retired port closes.
            for (var index = 0; index < 6; index++)
            {
                var proxy = ExchangeRelayProxy.Start(relay.RelayUrl, relay.Pin);
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
                    Assert.Equal("media-body", await http.GetStringAsync(proxy.LoopbackUrl));
                var relayPort = proxy.BoundPort;
                proxy.Dispose();
                await AssertPortClosed(relayPort);
                log.Event("FOREIGN RELAY", $"url={relay.RelayUrl}");

                var forwarder = ExchangeTunnelForwarder.Start(tunnel.TunnelUrl,
                    "http://192.0.2.1" + TunnelPath, tunnel.Pin);
                using (var client = new TcpClient())
                {
                    await client.ConnectAsync(IPAddress.Loopback, forwarder.BoundPort);
                    var bytes = new byte[10];
                    await client.GetStream().ReadExactlyAsync(bytes).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.Equal("media-body", Encoding.ASCII.GetString(bytes));
                }
                var tunnelPort = forwarder.BoundPort;
                forwarder.Dispose();
                await AssertPortClosed(tunnelPort);
                log.Event("FOREIGN TUNNEL", $"url={tunnel.TunnelUrl}");
            }

            Assert.All(tunnel.Envelopes, envelope => Assert.Equal("connect", envelope));
            Assert.Equal(6, tunnel.Envelopes.Count);
            Assert.Equal(6, relay.Requests);
            if (configured)
            {
                Assert.Equal(before, File.ReadAllBytes(accountPath));
                Assert.Equal("SHA256:" + new string('a', 43), store.Load().Pin);
            }
            else
                Assert.False(File.Exists(accountPath));

            log.Dispose();
            AssertRedacted(File.ReadAllText(Path.Combine(directory, DiagnosticLogFiles.CurrentLogName)));
            var archivePath = DiagnosticArchiveBuilder.Build(directory, directory, "foreign broadcasts", DateTimeOffset.UtcNow);
            using var archive = ZipFile.OpenRead(archivePath);
            foreach (var entry in archive.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                AssertRedacted(await reader.ReadToEndAsync());
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TunnelCertificateRefusalNamesTheCauseAndNeverSendsConnect(bool wrongPin)
    {
        using var server = new TlsFixture(tunnel: true);
        var refusal = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var forwarder = ExchangeTunnelForwarder.Start(server.TunnelUrl, "http://192.0.2.1" + TunnelPath,
            wrongPin ? "SHA256:" + new string('a', 43) : null);
        forwarder.Refused += reason => refusal.TrySetResult(reason);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, forwarder.BoundPort);
        Assert.Equal(wrongPin ? ExchangeBroadcastCertificateException.Refused
            : ExchangeBroadcastCertificateException.UntrustedWithoutFingerprint,
            await refusal.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(server.Envelopes);
        Assert.Equal(1, forwarder.ConnectAttempts);
    }

    [Fact]
    public async Task AudioTransportPreservesTheMissingFingerprintReason()
    {
        using var server = new TlsFixture(tunnel: false);
        using var response = await new FastMediaSorterPlaybackTransport().OpenAsync(new Uri(server.RelayUrl), CancellationToken.None);
        var exception = Assert.IsType<ExchangeBroadcastCertificateException>(response.TransportError);
        Assert.Equal(ExchangeBroadcastCertificateException.UntrustedWithoutFingerprint, exception.Reason);
        Assert.Equal(0, server.Requests);
    }

    [Fact]
    public async Task PlatformChainPolicyAcceptsAnUnpinnedTrustedFixture()
    {
        using var server = new TlsFixture(tunnel: false);
        using var proxy = ExchangeRelayProxy.Start(server.RelayUrl, null, async (host, port, pin, token) =>
        {
            Assert.Null(pin);
            var client = new TcpClient();
            await client.ConnectAsync(host, port, token);
            var ssl = new SslStream(client.GetStream(), false,
                (_, leaf, _, errors) => ExchangeCertificatePin.Accepts(leaf, pin, errors));
            var chainPolicy = new X509ChainPolicy
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust,
                RevocationMode = X509RevocationMode.NoCheck
            };
            chainPolicy.CustomTrustStore.Add(server.Certificate);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                { TargetHost = host, CertificateChainPolicy = chainPolicy }, token);
            return ssl;
        });
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        Assert.Equal("media-body", await http.GetStringAsync(proxy.LoopbackUrl));
        Assert.Null(proxy.LastFailure);
    }

    [Fact]
    public void SimultaneousListenersShareABoundAndStoppingReturnsTheSlot()
    {
        var listeners = new List<IDisposable>();
        try
        {
            for (var index = 0; index < ExchangeBroadcastResources.MaximumForwarders; index++)
                listeners.Add(index % 2 == 0
                    ? ExchangeRelayProxy.Start("https://127.0.0.1:1/v2/b/unused/stream", null)
                    : ExchangeTunnelForwarder.Start("fmsx://127.0.0.1:1/b/unused/http", "http://192.0.2.1/live.aac"));
            // SP-0222: the refusal is a type of its own, so the windows tell the bound from any other failed start.
            var refusal = Assert.Throws<ExchangeBroadcastCapacityException>(() =>
                ExchangeRelayProxy.Start("https://127.0.0.1:1/v2/b/unused/stream", null));
            Assert.Equal(ExchangeBroadcastResources.CapacityReason, refusal.Message);
            listeners[0].Dispose();
            using var replacement = ExchangeTunnelForwarder.Start("fmsx://127.0.0.1:1/b/unused/http", "http://192.0.2.1/live.aac");
        }
        finally
        {
            foreach (var listener in listeners)
                listener.Dispose();
        }
    }

    [Fact]
    public async Task IdleListenersExpireWithoutAPlayer()
    {
        using var relay = ExchangeRelayProxy.Start("https://127.0.0.1:1/v2/b/unused/stream", null);
        using var tunnel = ExchangeTunnelForwarder.Start("fmsx://127.0.0.1:1/b/unused/http", "http://192.0.2.1/live.aac");
        await Task.Delay(ExchangeRelayProxy.IdleTimeout + TimeSpan.FromSeconds(1));
        await AssertPortClosed(relay.BoundPort);
        await AssertPortClosed(tunnel.BoundPort);
    }

    /// <summary>
    /// SP-0215 requirement 7: the fixture of the acceptance - a protected relay plays after the right
    /// password, refuses a wrong one with one named refusal (never a retry loop: the fixture sees one
    /// request), and names the requirement when the leg held no password at all.
    /// </summary>
    [Fact]
    public async Task AProtectedRelayPlaysAfterTheRightPassword()
    {
        using var relay = new TlsFixture(tunnel: false, "listener", "the-link-password");
        using var proxy = ExchangeRelayProxy.Start(relay.RelayUrl, relay.Pin,
            credentials: new ExchangeRelayCredentials("listener", "the-link-password"));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        Assert.Equal("media-body", await http.GetStringAsync(proxy.LoopbackUrl));
        Assert.Null(proxy.LastFailure);
        Assert.Equal(1, relay.Requests);
    }

    [Fact]
    public async Task AProtectedRelayRefusesAWrongPasswordWithOneNamedRefusalAndNoRetry()
    {
        using var relay = new TlsFixture(tunnel: false, "listener", "the-link-password");
        using var proxy = ExchangeRelayProxy.Start(relay.RelayUrl, relay.Pin,
            credentials: new ExchangeRelayCredentials("listener", "a-wrong-password"));
        var refusal = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.Failed += reason => refusal.TrySetResult(reason);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        // The player sees the proxy's own 502; the named refusal is what the leg reports.
        using var refusedAnswer = await http.GetAsync(proxy.LoopbackUrl);
        Assert.Equal(HttpStatusCode.BadGateway, refusedAnswer.StatusCode);
        Assert.Equal(ExchangeBroadcastAuth.PasswordRefusedReason, await refusal.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        // The explicit deny: one request, one refusal - nothing retries in the background.
        Assert.Equal(1, relay.Requests);
    }

    [Fact]
    public async Task AProtectedRelayWithoutAPasswordNamesTheRequirement()
    {
        using var relay = new TlsFixture(tunnel: false, "listener", "the-link-password");
        using var proxy = ExchangeRelayProxy.Start(relay.RelayUrl, relay.Pin);
        var refusal = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.Failed += reason => refusal.TrySetResult(reason);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var refusedAnswer = await http.GetAsync(proxy.LoopbackUrl);
        Assert.Equal(HttpStatusCode.BadGateway, refusedAnswer.StatusCode);
        Assert.Equal(ExchangeBroadcastAuth.PasswordRequiredReason, await refusal.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, relay.Requests);
    }

    /// <summary>Requirement 1 beside requirement 7: the remembered password never touches the account store.</summary>
    [Fact]
    public void ABroadcastPasswordLivesOnlyUnderDpapiAndNeverInTheAccount()
    {
        var directory = Path.Combine(Path.GetTempPath(), "StreamsPlayer-SP0215-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new ExchangeBroadcastPasswordStore(directory);
            var accountStore = new ExchangeAccountStore(directory);
            var accountPath = Path.Combine(directory, "exchange-account.bin");
            accountStore.Save(new ExchangeAccount(ExchangeProtocol.NewDeviceId(), "third.example", 44022,
                "owner", new string('t', 43), "SHA256:" + new string('a', 43), Enabled: false));
            var before = File.ReadAllBytes(accountPath);

            var channelId = Guid.NewGuid();
            Assert.Null(store.Load(channelId));
            store.Save(channelId, "the-link-password");
            Assert.Equal("the-link-password", store.Load(channelId));

            // DPAPI, current user: the file at rest holds no password a reader could take.
            var atRest = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(directory, "broadcast-passwords.bin")));
            Assert.DoesNotContain("the-link-password", atRest);
            Assert.Equal(before, File.ReadAllBytes(accountPath));

            // A refusal forgets the stale one, so the next explicit play is the one new prompt.
            store.Delete(channelId);
            Assert.Null(store.Load(channelId));
            Assert.Equal(before, File.ReadAllBytes(accountPath));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>Requirement 5 beside requirement 7: the pair a foreign relay demanded is in no log and no bundle.</summary>
    [Fact]
    public async Task AProtectedRelayPasswordReachesNoLogAndNoDiagnosticBundle()
    {
        var directory = Path.Combine(Path.GetTempPath(), "StreamsPlayer-SP0215-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var relay = new TlsFixture(tunnel: false, "listener", "secret-link-password");
            using var log = new CurrentLog(directory);
            using var proxy = ExchangeRelayProxy.Start(relay.RelayUrl, relay.Pin,
                credentials: new ExchangeRelayCredentials("listener", "secret-link-password"));
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            Assert.Equal("media-body", await http.GetStringAsync(proxy.LoopbackUrl));
            log.Event("FOREIGN RELAY PROTECTED", $"url={relay.RelayUrl}");
            log.Dispose();
            Assert.DoesNotContain("secret-link-password",
                File.ReadAllText(Path.Combine(directory, DiagnosticLogFiles.CurrentLogName)));
            var archivePath = DiagnosticArchiveBuilder.Build(directory, directory, "protected foreign broadcast", DateTimeOffset.UtcNow);
            using var archive = ZipFile.OpenRead(archivePath);
            foreach (var entry in archive.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                Assert.DoesNotContain("secret-link-password", await reader.ReadToEndAsync());
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>The audio transport carries the pair on the relay's own request and keeps the 401 of a wrong one.</summary>
    [Theory]
    [InlineData("the-link-password", 200)]
    [InlineData("a-wrong-password", 401)]
    public async Task TheAudioTransportAnswersTheRelaysChallengeOnItsOwnRequest(string password, int expectedStatus)
    {
        using var relay = new TlsFixture(tunnel: false, "listener", "the-link-password");
        using var connection = await new FastMediaSorterPlaybackTransport().OpenAsync(
            new Uri(relay.RelayUrl), null, relay.Pin, CancellationToken.None,
            new ExchangeRelayCredentials("listener", password));
        Assert.Equal(expectedStatus, connection.StatusCode);
        if (expectedStatus == 200)
        {
            Assert.NotNull(connection.Stream);
        }
        else
        {
            Assert.Null(connection.Stream);
        }

        Assert.Equal(1, relay.Requests);
    }

    private static void AssertRedacted(string text)
    {
        Assert.DoesNotContain(RelayId, text);
        Assert.DoesNotContain(TunnelId, text);
        Assert.DoesNotContain(TunnelPath, text);
        Assert.DoesNotContain("/b/" + TunnelId + "/http", text);
    }

    private static async Task AssertPortClosed(int port)
    {
        using var client = new TcpClient();
        await Assert.ThrowsAsync<SocketException>(() => client.ConnectAsync(IPAddress.Loopback, port));
    }

    private sealed class TlsFixture : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _server;
        public X509Certificate2 Certificate { get; }
        public string Pin => ExchangeProtocol.Fingerprint(Certificate.RawData);
        public string RelayUrl => $"https://127.0.0.1:{Port}/v2/b/{RelayId}/stream";
        public string TunnelUrl => $"fmsx://127.0.0.1:{Port}/b/{TunnelId}/http";
        public int Port { get; }
        private readonly string? _relayUser;
        private readonly string? _relayPassword;
        public int Requests { get; private set; }
        public List<string?> Envelopes { get; } = [];

        public TlsFixture(bool tunnel, string? relayUser = null, string? relayPassword = null)
        {
            _relayUser = relayUser;
            _relayPassword = relayPassword;
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256);
            var san = new SubjectAlternativeNameBuilder();
            san.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(san.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            Certificate = X509CertificateLoader.LoadPkcs12(request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1)).Export(X509ContentType.Pfx), null);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _server = Task.Run(() => ServeAsync(tunnel));
        }

        private async Task ServeAsync(bool tunnel)
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    await using var ssl = new SslStream(client.GetStream());
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                        { ServerCertificate = Certificate }, _stop.Token);
                    if (tunnel)
                    {
                        using var envelope = await ExchangeProtocol.ReadAsync(ssl, false, _stop.Token);
                        Envelopes.Add(ExchangeProtocol.String(envelope.RootElement, "type"));
                        Assert.Equal(TunnelId, ExchangeProtocol.String(envelope.RootElement, "broadcastId"));
                        Assert.False(envelope.RootElement.TryGetProperty("deviceToken", out _));
                        await ExchangeProtocol.WriteAsync(ssl, new { schemaVersion = 2, type = "connected" }, false, _stop.Token);
                        await ssl.WriteAsync("media-body"u8.ToArray(), _stop.Token);
                    }
                    else
                    {
                        var head = new StringBuilder();
                        var buffer = new byte[1];
                        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                        {
                            await ssl.ReadExactlyAsync(buffer, _stop.Token);
                            head.Append((char)buffer[0]);
                        }
                        Requests++;
                        Assert.StartsWith($"GET /v2/b/{RelayId}/stream ", head.ToString());
                        if (_relayUser is not null)
                        {
                            // Item BF: the relay checks Basic against the broadcaster's verifiers
                            // and answers the challenge, nothing finer - one check per connection.
                            var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes(_relayUser + ":" + _relayPassword));
                            if (head.ToString().Contains($"Authorization: Basic {expected}", StringComparison.Ordinal))
                            {
                                await ssl.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 10\r\nConnection: close\r\n\r\nmedia-body"u8.ToArray(), _stop.Token);
                            }
                            else
                            {
                                await ssl.WriteAsync("HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: Basic realm=\"fms-broadcast\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), _stop.Token);
                            }
                        }
                        else
                        {
                            Assert.DoesNotContain("Authorization", head.ToString());
                            await ssl.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 10\r\nConnection: close\r\n\r\nmedia-body"u8.ToArray(), _stop.Token);
                        }
                    }
                }
                catch (Exception exception) when (exception is AuthenticationException or IOException or OperationCanceledException)
                {
                    // Refused TLS handshakes are expected fixture inputs; cancellation ends the listener.
                }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _server.GetAwaiter().GetResult();
            _listener.Stop();
            Certificate.Dispose();
            _stop.Dispose();
        }
    }
}

[CollectionDefinition("Foreign broadcast resources", DisableParallelization = true)]
public sealed class ForeignBroadcastResourceCollection;
