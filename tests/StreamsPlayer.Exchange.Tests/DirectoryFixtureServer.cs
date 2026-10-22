using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using StreamsPlayer.Core;

namespace StreamsPlayer.Exchange.Tests;

/// <summary>
/// SP-0222: one scripted exchange server for the directory tests. It answers the first connection's
/// <c>hello</c> with a <c>welcome</c> (of the kind the test names), records the type of every frame the client
/// sends afterwards - the request log the acceptance criteria are read from - and writes whatever frames the
/// test hands it, when the test hands them. It answers nothing on its own: a list is answered only when the
/// test says so, so what the client asked for is exactly what the log shows.
/// </summary>
internal sealed class DirectoryFixtureServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _lifetime = new(TimeSpan.FromMinutes(2));
    private readonly Channel<object> _outgoing = Channel.CreateUnbounded<object>();
    private readonly Task _serve;

    internal DirectoryFixtureServer(X509Certificate2 certificate, string accountKind = "member")
    {
        Certificate = certificate;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _serve = ServeAsync(accountKind);
    }

    internal X509Certificate2 Certificate { get; }
    internal int Port { get; }

    /// <summary>The type of each frame the client sent after the <c>welcome</c>, in order.</summary>
    internal ConcurrentQueue<string> Frames { get; } = new();

    /// <summary>The <c>deviceId</c> and whether a token was presented, as the client's <c>hello</c> said them.</summary>
    internal (string? DeviceId, bool HadToken)? Hello { get; private set; }

    internal int Connections { get; private set; }

    internal string Pin => ExchangeProtocol.Fingerprint(Certificate.RawData);

    internal ValueTask SendAsync(object frame) => _outgoing.Writer.WriteAsync(frame);

    internal string[] Seen() => [.. Frames];

    internal static X509Certificate2 NewCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        // Windows Schannel cannot use the ephemeral key returned by CreateSelfSigned directly.
        return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet);
    }

    internal static async Task UntilAsync(Func<bool> condition, string? context = null)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            try
            {
                await Task.Delay(20, deadline.Token);
            }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException($"Timed out: {context}");
            }
        }
    }

    private async Task ServeAsync(string accountKind)
    {
        var token = _lifetime.Token;
        try
        {
            using var client = await _listener.AcceptTcpClientAsync(token);
            Connections++;
            using var tls = new SslStream(client.GetStream());
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = Certificate }, token);
            using (var opening = await ExchangeProtocol.ReadAsync(tls, false, token))
            {
                var root = opening.RootElement;
                Hello = (ExchangeProtocol.String(root, "deviceId"), ExchangeProtocol.String(root, "deviceToken") is { Length: > 0 });
            }

            await ExchangeProtocol.WriteAsync(tls, new
            {
                schemaVersion = 2, type = "welcome", account = "test-login", accountKind,
                publicEndpoint = "localhost:44022", serverVersion = "test",
                features = new[] { "directory" }, keepaliveSeconds = 30
            }, false, token);

            _ = Task.Run(async () =>
            {
                try
                {
                    await foreach (var frame in _outgoing.Reader.ReadAllAsync(token))
                    {
                        await ExchangeProtocol.WriteAsync(tls, frame, true, token);
                    }
                }
                catch (Exception exception) when (exception is IOException or OperationCanceledException
                    or ObjectDisposedException)
                {
                    // The client or the test ended the stream; the reader loop below ends with it.
                }
            }, token);
            while (true)
            {
                using var frame = await ExchangeProtocol.ReadAsync(tls, true, token);
                Frames.Enqueue(ExchangeProtocol.String(frame.RootElement, "type") ?? "?");
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException
            or System.Security.Authentication.AuthenticationException or ObjectDisposedException
            or InvalidDataException or SocketException)
        {
            // Teardown of either end: nothing is owed here.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        _listener.Stop();
        await _serve;
        _lifetime.Dispose();
    }
}
