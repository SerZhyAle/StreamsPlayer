using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.Exchange.Tests;

public sealed class ExchangeTunnelForwarderTests
{
    [Fact]
    public async Task ForwarderConnectsAndSplicesBidirectionalBytePattern()
    {
        var exchangeServerStream = new DuplexPipeStream();
        var forwarderExchangeStream = exchangeServerStream.CreatePaired();

        await using var forwarder = ExchangeTunnelForwarder.Start(
            "fmsx://exchange.example.net:44022/b/b1/http",
            "http://192.168.1.97:8768/live-audio.aac",
            maxConnections: 2,
            streamConnector: (_, _, _, _) => Task.FromResult<Stream>(forwarderExchangeStream));

        // Connect local client to the forwarder's loopback port.
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, forwarder.BoundPort);
        using var clientStream = client.GetStream();

        // Server side: read the connect envelope.
        using var connectDoc = await ExchangeProtocol.ReadAsync(exchangeServerStream, authenticated: false, CancellationToken.None);
        Assert.Equal("connect", ExchangeProtocol.String(connectDoc.RootElement, "type"));
        Assert.Equal("b1", ExchangeProtocol.String(connectDoc.RootElement, "broadcastId"));
        Assert.Equal("http", ExchangeProtocol.String(connectDoc.RootElement, "scheme"));

        // Server side: send connected response.
        await ExchangeProtocol.WriteAsync(exchangeServerStream, new { schemaVersion = 2, type = "connected" }, authenticated: false, CancellationToken.None);

        // Client -> Server byte transfer.
        var clientBytes = Encoding.UTF8.GetBytes("GET /live-audio.aac HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
        await clientStream.WriteAsync(clientBytes);
        await clientStream.FlushAsync();

        var serverReadBuffer = new byte[clientBytes.Length];
        await exchangeServerStream.ReadExactlyAsync(serverReadBuffer);
        Assert.Equal("GET /live-audio.aac HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n", Encoding.UTF8.GetString(serverReadBuffer));

        // Server -> Client byte transfer.
        var serverResponseBytes = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: audio/aac\r\n\r\nAUDIO_DATA_PAYLOAD");
        await exchangeServerStream.WriteAsync(serverResponseBytes);
        await exchangeServerStream.FlushAsync();

        var clientReadBuffer = new byte[serverResponseBytes.Length];
        await clientStream.ReadExactlyAsync(clientReadBuffer);
        Assert.Equal("HTTP/1.1 200 OK\r\nContent-Type: audio/aac\r\n\r\nAUDIO_DATA_PAYLOAD", Encoding.UTF8.GetString(clientReadBuffer));

        Assert.Equal(1, forwarder.ConnectedCount);
    }

    [Fact]
    public async Task ClosingEitherSideClosesBothSides()
    {
        var exchangeServerStream = new DuplexPipeStream();
        var forwarderExchangeStream = exchangeServerStream.CreatePaired();

        await using var forwarder = ExchangeTunnelForwarder.Start(
            "fmsx://exchange.example.net:44022/b/b1/http",
            "http://192.168.1.97:8768/live-audio.aac",
            streamConnector: (_, _, _, _) => Task.FromResult<Stream>(forwarderExchangeStream));

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, forwarder.BoundPort);

        using var connectDoc = await ExchangeProtocol.ReadAsync(exchangeServerStream, authenticated: false, CancellationToken.None);
        await ExchangeProtocol.WriteAsync(exchangeServerStream, new { schemaVersion = 2, type = "connected" }, authenticated: false, CancellationToken.None);

        // Close exchange side.
        exchangeServerStream.Dispose();

        // Client side should observe EOF / disconnection.
        var clientStream = client.GetStream();
        var buffer = new byte[16];
        var read = await clientStream.ReadAsync(buffer);
        Assert.Equal(0, read);
    }

    [Fact]
    public async Task ConnectionsBeyondBoundAreRefused()
    {
        var exchangeServerStream1 = new DuplexPipeStream();
        var forwarderExchangeStream1 = exchangeServerStream1.CreatePaired();

        await using var forwarder = ExchangeTunnelForwarder.Start(
            "fmsx://exchange.example.net:44022/b/b1/http",
            "http://192.168.1.97:8768/live-audio.aac",
            maxConnections: 1,
            streamConnector: (_, _, _, _) => Task.FromResult<Stream>(forwarderExchangeStream1));

        // First connection connects successfully.
        using var client1 = new TcpClient();
        await client1.ConnectAsync(IPAddress.Loopback, forwarder.BoundPort);

        using var connectDoc = await ExchangeProtocol.ReadAsync(exchangeServerStream1, authenticated: false, CancellationToken.None);
        await ExchangeProtocol.WriteAsync(exchangeServerStream1, new { schemaVersion = 2, type = "connected" }, authenticated: false, CancellationToken.None);

        // Second connection is beyond maxConnections (1) and should be rejected/closed.
        using var client2 = new TcpClient();
        await client2.ConnectAsync(IPAddress.Loopback, forwarder.BoundPort);
        var clientStream2 = client2.GetStream();
        var buffer = new byte[16];
        var read = await clientStream2.ReadAsync(buffer);
        Assert.Equal(0, read);
    }

    [Fact]
    public async Task UnavailableRefusalClosesClientAndRecordsReason()
    {
        var exchangeServerStream = new DuplexPipeStream();
        var forwarderExchangeStream = exchangeServerStream.CreatePaired();
        string? observedRefusal = null;

        await using var forwarder = ExchangeTunnelForwarder.Start(
            "fmsx://exchange.example.net:44022/b/b1/http",
            "http://192.168.1.97:8768/live-audio.aac",
            streamConnector: (_, _, _, _) => Task.FromResult<Stream>(forwarderExchangeStream));

        forwarder.Refused += reason => observedRefusal = reason;

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, forwarder.BoundPort);

        using var connectDoc = await ExchangeProtocol.ReadAsync(exchangeServerStream, authenticated: false, CancellationToken.None);
        Assert.Equal("connect", ExchangeProtocol.String(connectDoc.RootElement, "type"));

        // Server answers refused with reason "unavailable"
        await ExchangeProtocol.WriteAsync(exchangeServerStream, new { schemaVersion = 2, type = "refused", reason = "unavailable" }, authenticated: false, CancellationToken.None);

        var clientStream = client.GetStream();
        var buffer = new byte[16];
        var read = await clientStream.ReadAsync(buffer);
        Assert.Equal(0, read);

        Assert.Equal("unavailable", forwarder.LastRefusalReason);
        Assert.Equal("ExchangeUnavailable", forwarder.LastRefusalKey);
        Assert.Equal("unavailable", observedRefusal);
    }

    [Fact]
    public async Task StoppingForwarderClosesPortAndReleasesSocket()
    {
        var forwarder = ExchangeTunnelForwarder.Start(
            "fmsx://exchange.example.net:44022/b/b1/http",
            "http://192.168.1.97:8768/live-audio.aac");

        var port = forwarder.BoundPort;
        Assert.True(port > 0);
        Assert.True(IsPortListening(port));

        await forwarder.DisposeAsync();

        Assert.False(IsPortListening(port));
    }

    [Fact]
    public async Task TlsPinningVerificationWithRealCertificate()
    {
        using var cert = GenerateSelfSignedCert();
        var correctFingerprint = ExchangeProtocol.Fingerprint(cert.RawData);
        var wrongFingerprint = "SHA256:0000000000000000000000000000000000000000000";

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            try
            {
                using var serverClient = await listener.AcceptTcpClientAsync();
                using var sslStream = new System.Net.Security.SslStream(serverClient.GetStream(), leaveInnerStreamOpen: false);
                await sslStream.AuthenticateAsServerAsync(cert);
                using var envelope = await ExchangeProtocol.ReadAsync(sslStream, authenticated: false, CancellationToken.None);
                await ExchangeProtocol.WriteAsync(sslStream, new { schemaVersion = 2, type = "connected" }, authenticated: false, CancellationToken.None);
                var buf = new byte[4];
                await sslStream.ReadExactlyAsync(buf);
            }
            catch
            {
                // Ignored.
            }
        });

        try
        {
            // Connect with correct fingerprint -> succeeds.
            await using var forwarder = ExchangeTunnelForwarder.Start(
                $"fmsx://localhost:{port}/b/b1/http",
                "http://192.168.1.97:8768/live-audio.aac",
                certFingerprint: correctFingerprint);

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, forwarder.BoundPort);
            using var clientStream = client.GetStream();
            await clientStream.WriteAsync(new byte[] { 1, 2, 3, 4 });
            await clientStream.FlushAsync();

            await serverTask;
            Assert.Equal(1, forwarder.ConnectedCount);
        }
        finally
        {
            listener.Stop();
        }

        // Connect with wrong fingerprint -> rejected.
        var listener2 = new TcpListener(IPAddress.Loopback, 0);
        listener2.Start();
        var port2 = ((IPEndPoint)listener2.LocalEndpoint).Port;
        var serverTask2 = Task.Run(async () =>
        {
            try
            {
                using var serverClient = await listener2.AcceptTcpClientAsync();
                using var sslStream = new System.Net.Security.SslStream(serverClient.GetStream(), leaveInnerStreamOpen: false);
                await sslStream.AuthenticateAsServerAsync(cert);
            }
            catch
            {
                // Ignored.
            }
        });

        try
        {
            await using var forwarder2 = ExchangeTunnelForwarder.Start(
                $"fmsx://localhost:{port2}/b/b1/http",
                "http://192.168.1.97:8768/live-audio.aac",
                certFingerprint: wrongFingerprint);

            using var client2 = new TcpClient();
            await client2.ConnectAsync(IPAddress.Loopback, forwarder2.BoundPort);
            var clientStream2 = client2.GetStream();
            var buf = new byte[16];
            var read = await clientStream2.ReadAsync(buf);
            Assert.Equal(0, read);
            Assert.Equal(0, forwarder2.ConnectedCount);
            await serverTask2;
        }
        finally
        {
            listener2.Stop();
        }
    }

    private static bool IsPortListening(int port) =>
        IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(ep => ep.Port == port);

    private static X509Certificate2 GenerateSelfSignedCert()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var generated = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(2));
        return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet);
    }

    private sealed class DuplexPipeStream : Stream
    {
        private readonly System.IO.Pipelines.Pipe _incoming = new();
        private readonly System.IO.Pipelines.Pipe _outgoing = new();

        public DuplexPipeStream() { }

        private DuplexPipeStream(System.IO.Pipelines.Pipe incoming, System.IO.Pipelines.Pipe outgoing)
        {
            _incoming = incoming;
            _outgoing = outgoing;
        }

        public DuplexPipeStream CreatePaired() => new(_outgoing, _incoming);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Flush() { }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await _outgoing.Writer.FlushAsync(cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var result = await _incoming.Reader.ReadAsync(cancellationToken);
            var bytesToCopy = (int)Math.Min(buffer.Length, result.Buffer.Length);
            if (bytesToCopy > 0)
            {
                System.Buffers.BuffersExtensions.CopyTo(result.Buffer.Slice(0, bytesToCopy), buffer.Span);
                _incoming.Reader.AdvanceTo(result.Buffer.GetPosition(bytesToCopy));
                return bytesToCopy;
            }

            if (result.IsCompleted)
            {
                return 0;
            }

            _incoming.Reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
            return 0;
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _outgoing.Writer.WriteAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            _outgoing.Writer.Complete();
            _incoming.Reader.Complete();
            base.Dispose(disposing);
        }
    }
}
