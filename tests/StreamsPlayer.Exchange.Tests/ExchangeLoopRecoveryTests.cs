using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using StreamsPlayer.App;
using StreamsPlayer.Core;

namespace StreamsPlayer.Exchange.Tests;

/// <summary>
/// The receiver loop and the account operations around it must survive what a real network does: a server
/// that sends a malformed frame (SP-0200), an enrollment that is refused or cannot connect, and a probe of
/// some other server's certificate (SP-0200 item D). Every test drives the shipping <c>ExchangeSourceService</c>
/// against scripted TLS servers.
/// </summary>
public sealed class ExchangeLoopRecoveryTests
{
    [Theory]
    [InlineData("schema")]
    [InlineData("zero-length")]
    [InlineData("not-an-object")]
    public async Task MalformedFrame_IsReconnectedAndNeverBlocksTeardown(string fault)
    {
        // expected: a protocol violation ends one connection and the loop reconnects with its backoff, the
        // status reports it, and Forget completes. Before the fix InvalidDataException escaped RunAsync, the
        // loop stayed dead and every later StopLoopAsync rethrew it.
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        await using var server = new ScriptedServer(certificate, async (index, tls, token) =>
        {
            await Handshake(tls, token);
            if (index == 0)
            {
                await SendFault(tls, fault, token);
                return;
            }

            await HoldOpen(tls, token);
        });
        scope.Save(Account(server.Port, certificate));
        var service = new ExchangeSourceService(scope.Path);
        var statuses = new ConcurrentQueue<string>();
        service.Changed += () => statuses.Enqueue(service.StatusKey);
        service.Start();
        try
        {
            await Until(() => server.Connections >= 2 && service.StatusKey == "ExchangeOnline");
            Assert.Contains("ExchangeUnavailable", statuses);
        }
        finally
        {
            await service.ForgetAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Null(service.Account.Token);
        server.ThrowIfScriptFailed();
    }

    [Fact]
    public async Task MalformedWelcome_IsReconnectedAndNeverBlocksTeardown()
    {
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        await using var server = new ScriptedServer(certificate, async (index, tls, token) =>
        {
            if (index == 0)
            {
                using var opening = await ExchangeProtocol.ReadAsync(tls, false, token);
                // No publicEndpoint: ExchangeHandshake.ReadInterval refuses the whole welcome.
                await ExchangeProtocol.WriteAsync(tls, new
                {
                    schemaVersion = 2, type = "welcome", account = "test-login",
                    serverVersion = "test", features = new[] { "directory" }, keepaliveSeconds = 30
                }, false, token);
                return;
            }

            await Handshake(tls, token);
            await HoldOpen(tls, token);
        });
        scope.Save(Account(server.Port, certificate));
        var service = new ExchangeSourceService(scope.Path);
        service.Start();
        try
        {
            await Until(() => server.Connections >= 2 && service.StatusKey == "ExchangeOnline");
        }
        finally
        {
            await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }

        server.ThrowIfScriptFailed();
    }

    [Fact]
    public async Task EnrollmentThatCannotConnect_PutsTheRunningReceiverBack()
    {
        // expected: the receiver that EnrollAsync stopped is running again after the failure, and the failure
        // stays on the status line until it is online | actual (before): nothing restarted it.
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        await using var server = new ScriptedServer(certificate, async (index, tls, token) =>
        {
            await Handshake(tls, token, welcomeDelay: index == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(2));
            await HoldOpen(tls, token);
        });
        scope.Save(Account(server.Port, certificate));
        var service = new ExchangeSourceService(scope.Path);
        service.Start();
        try
        {
            await Until(() => service.StatusKey == "ExchangeOnline");

            var closedPort = FreePort();
            await service.EnrollAsync("localhost", closedPort, "someone", false, ['p', 'w'], null)
                .WaitAsync(TimeSpan.FromSeconds(20));

            Assert.Equal("ExchangeUnavailable", service.StatusKey);
            await Until(() => server.Connections >= 2 && service.StatusKey == "ExchangeOnline");
            Assert.NotNull(service.Account.Token);
        }
        finally
        {
            await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }

        server.ThrowIfScriptFailed();
    }

    [Fact]
    public async Task ProbeOfAnotherServer_NeverBecomesTheAccountsPendingReplacement()
    {
        // expected: the other server's fingerprint is returned to the enrollment that asked and recorded
        // nowhere else, so Trust cannot pin it onto the account's own host | actual (before): it was stored
        // as the account's pending replacement and AcceptReplacementPinAsync pinned it.
        using var accountCertificate = Certificate();
        using var otherCertificate = Certificate();
        using var scope = new TestDirectory();
        await using var other = new ScriptedServer(otherCertificate, (_, tls, token) => HoldOpen(tls, token));
        var accountPin = ExchangeProtocol.Fingerprint(accountCertificate.RawData);
        scope.Save(new ExchangeAccount(ExchangeProtocol.NewDeviceId(), "localhost", FreePort(), "test-login",
            new string('t', 43), accountPin, Enabled: false));
        var service = new ExchangeSourceService(scope.Path);
        var statusBefore = service.StatusKey;

        var presented = await service.InspectCertificateAsync("localhost", other.Port);

        Assert.Equal(ExchangeProtocol.Fingerprint(otherCertificate.RawData), presented);
        Assert.Null(service.PresentedFingerprint);
        Assert.Null(service.PreviousFingerprint);
        Assert.Equal(statusBefore, service.StatusKey);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AcceptReplacementPinAsync(presented!));
        Assert.Equal(accountPin, service.Account.Pin);
    }

    [Fact]
    public async Task ChangedLeafOfTheAccountsOwnServer_IsAReplacementTheUserCanAccept()
    {
        using var oldCertificate = Certificate();
        using var newCertificate = Certificate();
        using var scope = new TestDirectory();
        await using var server = new ScriptedServer(newCertificate, (_, tls, token) => HoldOpen(tls, token));
        var oldPin = ExchangeProtocol.Fingerprint(oldCertificate.RawData);
        var newPin = ExchangeProtocol.Fingerprint(newCertificate.RawData);
        scope.Save(new ExchangeAccount(ExchangeProtocol.NewDeviceId(), "localhost", server.Port, "test-login",
            new string('t', 43), oldPin, Enabled: false));
        var service = new ExchangeSourceService(scope.Path);

        var presented = await service.InspectCertificateAsync("localhost", server.Port);

        Assert.Equal(newPin, presented);
        Assert.Equal(newPin, service.PresentedFingerprint);
        Assert.Equal(oldPin, service.PreviousFingerprint);
        Assert.Equal("ExchangeCertificate", service.StatusKey);

        await service.AcceptReplacementPinAsync(newPin);

        Assert.Equal(newPin, service.Account.Pin);
        Assert.Null(service.PresentedFingerprint);
    }

    [Fact]
    public async Task ReplacementPin_IsRefusedWithoutStoppingTheRunningReceiver()
    {
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        var closedConnections = 0;
        await using var server = new ScriptedServer(certificate, async (_, tls, token) =>
        {
            try
            {
                await Handshake(tls, token);
                await HoldOpen(tls, token);
            }
            finally
            {
                Interlocked.Increment(ref closedConnections);
            }
        });
        scope.Save(Account(server.Port, certificate));
        var service = new ExchangeSourceService(scope.Path);
        service.Start();
        try
        {
            await Until(() => service.StatusKey == "ExchangeOnline");

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.AcceptReplacementPinAsync("SHA256:nothing-pending"));
            await Task.Delay(300);

            Assert.Equal("ExchangeOnline", service.StatusKey);
            Assert.Equal(0, Volatile.Read(ref closedConnections));
        }
        finally
        {
            await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static ExchangeAccount Account(int port, X509Certificate2 certificate) =>
        new(ExchangeProtocol.NewDeviceId(), "localhost", port, "test-login", new string('t', 43),
            ExchangeProtocol.Fingerprint(certificate.RawData), Enabled: true);

    private static async Task Handshake(SslStream tls, CancellationToken token, TimeSpan? welcomeDelay = null)
    {
        using var opening = await ExchangeProtocol.ReadAsync(tls, false, token);
        Assert.Equal("hello", ExchangeProtocol.String(opening.RootElement, "type"));
        if (welcomeDelay is { } delay && delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, token);
        }

        await ExchangeProtocol.WriteAsync(tls, new
        {
            schemaVersion = 2, type = "welcome", account = "test-login", publicEndpoint = "localhost:44022",
            serverVersion = "test", features = new[] { "directory", "cast" }, keepaliveSeconds = 30
        }, false, token);
    }

    private static async Task HoldOpen(SslStream tls, CancellationToken token)
    {
        // Reads until the client ends the stream; the client's keepalive, list and bye are all just frames.
        while (true)
        {
            using var frame = await ExchangeProtocol.ReadAsync(tls, true, token);
        }
    }

    private static async Task SendFault(SslStream tls, string fault, CancellationToken token)
    {
        using var keepalive = await ExchangeProtocol.ReadAsync(tls, true, token);
        using var list = await ExchangeProtocol.ReadAsync(tls, true, token);
        switch (fault)
        {
            case "schema":
                await ExchangeProtocol.WriteAsync(tls, new { schemaVersion = 3, type = "directory" }, true, token);
                break;
            case "zero-length":
                await tls.WriteAsync(new byte[4], token);
                break;
            default:
                var body = "[1,2,3]"u8.ToArray();
                var header = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(header, (uint)body.Length);
                await tls.WriteAsync(header, token);
                await tls.WriteAsync(body, token);
                break;
        }

        await tls.FlushAsync(token);
        // Give the client the time to read the fault before this end closes the stream.
        await Task.Delay(200, token);
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition())
        {
            try
            {
                await Task.Delay(20, deadline.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("The expected state was not reached.");
            }
        }
    }

    private static X509Certificate2 Certificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        // Windows Schannel cannot use the ephemeral key returned by CreateSelfSigned directly.
        return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet);
    }

    private sealed class TestDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "StreamsPlayer.Exchange.Tests", Guid.NewGuid().ToString("N"));

        internal TestDirectory() => Directory.CreateDirectory(Path);

        internal void Save(ExchangeAccount account) => new ExchangeAccountStore(Path).Save(account);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    /// <summary>A TLS listener that runs one script per accepted connection, concurrently, until disposed.</summary>
    private sealed class ScriptedServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Func<int, SslStream, CancellationToken, Task> _script;
        private readonly X509Certificate2 _certificate;
        private readonly ConcurrentBag<Task> _connections = [];
        private readonly ConcurrentQueue<Exception> _failures = new();
        private readonly Task _accepting;
        private int _count;

        internal ScriptedServer(X509Certificate2 certificate, Func<int, SslStream, CancellationToken, Task> script)
        {
            _certificate = certificate;
            _script = script;
            _listener.Start();
            _accepting = AcceptAsync();
        }

        internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        /// <summary>Connections whose TLS handshake was completed, so a refused leaf never counts.</summary>
        internal int Connections => Volatile.Read(ref _count);

        internal void ThrowIfScriptFailed()
        {
            if (_failures.TryPeek(out var failure))
            {
                throw new InvalidOperationException("A server script failed.", failure);
            }
        }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }

                _connections.Add(ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            using (var tls = new SslStream(client.GetStream()))
            {
                try
                {
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _certificate }, _stop.Token);
                    var index = Interlocked.Increment(ref _count) - 1;
                    await _script(index, tls, _stop.Token);
                }
                catch (Exception exception) when (exception is AuthenticationException or IOException or OperationCanceledException
                    or ObjectDisposedException)
                {
                    // The client refused the leaf, ended the stream, or the test is over.
                }
                catch (Exception exception)
                {
                    _failures.Enqueue(exception);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _accepting;
            await Task.WhenAll(_connections);
            _stop.Dispose();
        }
    }
}
