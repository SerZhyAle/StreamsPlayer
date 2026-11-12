using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace StreamsPlayer.Core.Tests;

public sealed class BroadcastDigestProxyTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(8);

    [Theory]
    [InlineData(false, "SHA-256", true)]
    [InlineData(false, "MD5", false)]
    [InlineData(true, "SHA-256", true)]
    public async Task HttpAnswersDigestAndNeverSendsBasic(bool wrongPassword, string algorithm, bool qop)
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var url = $"http://127.0.0.1:{((IPEndPoint)server.LocalEndpoint).Port}/secret/live.aac";
        var serving = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var peer = await server.AcceptTcpClientAsync();
                var stream = peer.GetStream();
                var request = await DigestRtspMessage.ReadAsync(stream, CancellationToken.None);
                Assert.NotNull(request);
                if (attempt == 0)
                {
                    Assert.Null(request.Header("Authorization"));
                    await Reply(stream, "HTTP/1.1 401 Unauthorized", [("WWW-Authenticate", Challenge(algorithm, qop)), ("Connection", "close")], []);
                }
                else
                {
                    Assert.True(Valid(request.Header("Authorization"), "GET", "/secret/live.aac", "password", algorithm, qop) == !wrongPassword);
                    await Reply(stream, wrongPassword ? "HTTP/1.1 401 Unauthorized" : "HTTP/1.1 200 OK",
                        wrongPassword ? [("WWW-Authenticate", Challenge(algorithm, qop))] : [("Content-Type", "audio/aac")], wrongPassword ? [] : [1, 2, 3]);
                }
            }
        });
        using var proxy = BroadcastDigestProxy.Start(url, new("listener", wrongPassword ? "wrong" : "password"));
        using var client = new HttpClient();
        var failure = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.Failed += reason => failure.TrySetResult(reason);
        if (wrongPassword)
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetByteArrayAsync(proxy.LoopbackUrl));
            Assert.Equal(ExchangeBroadcastAuth.PasswordRefusedReason, await failure.Task.WaitAsync(Budget));
        }
        else Assert.Equal(new byte[] { 1, 2, 3 }, await client.GetByteArrayAsync(proxy.LoopbackUrl).WaitAsync(Budget));
        await serving.WaitAsync(Budget);
        await proxy.Completion.WaitAsync(Budget);
        Assert.DoesNotContain("password", proxy.LoopbackUrl);
    }

    [Theory]
    [InlineData("SHA-256", true, false)]
    [InlineData("MD5", false, false)]
    [InlineData("MD5", true, true)]
    public async Task RtspAuthenticatesRewritesControlAndPreservesInterleavedFrames(string algorithm, bool qop, bool wrongPassword)
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var connect = $"rtsp://127.0.0.1:{((IPEndPoint)server.LocalEndpoint).Port}/live";
        const string original = "rtsp://phone.test:8554/live";
        var serving = Task.Run(async () =>
        {
            using var peer = await server.AcceptTcpClientAsync();
            using var input = new BufferedStream(peer.GetStream());
            var output = peer.GetStream();
            var request = await DigestRtspMessage.ReadAsync(input, CancellationToken.None);
            Assert.Equal("DESCRIBE " + original + " RTSP/1.0", request!.StartLine);
            Assert.Null(request.Header("Authorization"));
            await Reply(output, "RTSP/1.0 401 Unauthorized", [("CSeq", request.Header("CSeq")!), ("WWW-Authenticate", Challenge(algorithm, qop))], []);
            request = await DigestRtspMessage.ReadAsync(input, CancellationToken.None);
            Assert.Equal("2", request!.Header("CSeq"));
            Assert.Equal(!wrongPassword, Valid(request.Header("Authorization"), "DESCRIBE", original, "password", algorithm, qop));
            if (wrongPassword)
            {
                await Reply(output, "RTSP/1.0 401 Unauthorized", [("CSeq", "2"), ("WWW-Authenticate", Challenge(algorithm, qop))], []);
                return;
            }
            await Reply(output, "RTSP/1.0 200 OK", [("CSeq", "2"), ("Content-Base", original + "/"), ("Content-Type", "application/sdp")],
                Encoding.UTF8.GetBytes($"v=0\r\na=control:{original}/trackID=1\r\n"));
            request = await DigestRtspMessage.ReadAsync(input, CancellationToken.None);
            Assert.Equal($"SETUP {original}/trackID=1 RTSP/1.0", request!.StartLine);
            Assert.True(Valid(request.Header("Authorization"), "SETUP", original + "/trackID=1", "password", algorithm, qop));
            await Reply(output, "RTSP/1.0 200 OK", [("CSeq", request.Header("CSeq")!)], []);
            await output.WriteAsync(new byte[] { 36, 0, 0, 4, 1, 2, 3, 4 });
            var rtcp = await DigestRtspMessage.ReadAsync(input, CancellationToken.None);
            Assert.Equal(new byte[] { 36, 1, 0, 2, 5, 6 }, rtcp!.Frame);
            // Keep the producer open until its listener has consumed the interleaved frame.
            Assert.Equal(0, await input.ReadAsync(new byte[1]));
        });
        using var proxy = BroadcastDigestProxy.Start(original, new("listener", wrongPassword ? "wrong" : "password"), connectUrl: connect);
        var failure = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.Failed += reason => failure.TrySetResult(reason);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxy.BoundPort);
        var local = client.GetStream();
        using var reader = new BufferedStream(local);
        await Reply(local, $"DESCRIBE {proxy.LoopbackUrl} RTSP/1.0", [("CSeq", "91")], []);
        if (wrongPassword)
        {
            Assert.Equal(ExchangeBroadcastAuth.PasswordRefusedReason, await failure.Task.WaitAsync(Budget));
        }
        else
        {
            var response = await DigestRtspMessage.ReadAsync(reader, CancellationToken.None).WaitAsync(Budget);
            Assert.Equal("91", response!.Header("CSeq"));
            Assert.Equal(proxy.LoopbackUrl + "/", response.Header("Content-Base"));
            Assert.Contains(proxy.LoopbackUrl + "/trackID=1", Encoding.UTF8.GetString(response.Body));
            Assert.DoesNotContain("phone.test", Encoding.UTF8.GetString(response.Body));
            await Reply(local, $"SETUP {proxy.LoopbackUrl}/trackID=1 RTSP/1.0", [("CSeq", "92"), ("Transport", "RTP/AVP/TCP;unicast;interleaved=0-1")], []);
            response = await DigestRtspMessage.ReadAsync(reader, CancellationToken.None).WaitAsync(Budget);
            Assert.Equal("92", response!.Header("CSeq"));
            response = await DigestRtspMessage.ReadAsync(reader, CancellationToken.None).WaitAsync(Budget);
            Assert.Equal(new byte[] { 36, 0, 0, 4, 1, 2, 3, 4 }, response!.Frame);
            await local.WriteAsync(new byte[] { 36, 1, 0, 2, 5, 6 });
        }
        client.Close();
        await serving.WaitAsync(Budget);
        await proxy.Completion.WaitAsync(Budget);
    }

    [Fact]
    public async Task DisposeCancelsAnIdleListenerAndDoesNotContactProducer()
    {
        for (var index = 0; index < ExchangeBroadcastResources.MaximumForwarders + 1; index++)
        {
            using var proxy = BroadcastDigestProxy.Start("rtsp://127.0.0.1:1/live", new("u", "p"));
            proxy.Dispose();
            await proxy.Completion.WaitAsync(Budget);
            Assert.Null(proxy.LastFailure);
        }
    }

    [Theory]
    [InlineData("200 OK", null)]
    [InlineData("401 Unauthorized", "Basic realm=\"fixture\"")]
    [InlineData("401 Unauthorized", "Digest realm=\"fixture\", nonce=\"n\", algorithm=SHA-512")]
    public async Task NeverPlaysClearBytesOrAnswersBasicInsteadOfDigest(string status, string? challenge)
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var serving = Task.Run(async () =>
        {
            using var peer = await server.AcceptTcpClientAsync();
            var request = await DigestRtspMessage.ReadAsync(peer.GetStream(), CancellationToken.None);
            Assert.Null(request!.Header("Authorization"));
            await Reply(peer.GetStream(), "HTTP/1.1 " + status, challenge is null ? [] : [("WWW-Authenticate", challenge)], [1, 2, 3]);
        });
        using var proxy = BroadcastDigestProxy.Start($"http://127.0.0.1:{((IPEndPoint)server.LocalEndpoint).Port}/live", new("u", "p"));
        using var client = new HttpClient();
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetByteArrayAsync(proxy.LoopbackUrl));
        await serving.WaitAsync(Budget);
        Assert.Equal(ExchangeBroadcastAuth.UnsupportedEndpointReason, proxy.LastFailure);
    }

    [Fact]
    public async Task RedirectNeverReceivesCredentialsOrGetsFollowed()
    {
        using var destination = new TcpListener(IPAddress.Loopback, 0);
        destination.Start();
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var serving = Task.Run(async () =>
        {
            using var peer = await server.AcceptTcpClientAsync();
            await DigestRtspMessage.ReadAsync(peer.GetStream(), CancellationToken.None);
            await Reply(peer.GetStream(), "HTTP/1.1 302 Found", [("Location", $"http://127.0.0.1:{((IPEndPoint)destination.LocalEndpoint).Port}/steal")], []);
        });
        using var proxy = BroadcastDigestProxy.Start($"http://127.0.0.1:{((IPEndPoint)server.LocalEndpoint).Port}/live", new("u", "p"));
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        using var response = await client.GetAsync(proxy.LoopbackUrl).WaitAsync(Budget);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.False(destination.Pending());
        await serving.WaitAsync(Budget);
    }

    [Theory]
    [InlineData("RTSP/1.0 200 OK\r\nContent-Length: 999999\r\n\r\n")]
    [InlineData("RTSP/1.0 200 OK\r\nContent-Length: -1\r\n\r\n")]
    [InlineData("RTSP/1.0 200 OK\r\nContent-Length: 1\r\nContent-Length: 1\r\n\r\n")]
    public async Task RefusesUnboundedOrAmbiguousBodyLengths(string text)
    {
        using var input = new MemoryStream(Encoding.ASCII.GetBytes(text));
        await Assert.ThrowsAsync<InvalidDataException>(() => DigestRtspMessage.ReadAsync(input, CancellationToken.None));
    }

    [Fact]
    public async Task ForeignLoopbackPathCannotOpenProducer()
    {
        using var proxy = BroadcastDigestProxy.Start("http://127.0.0.1:1/live", new("u", "p"));
        using var client = new HttpClient();
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetByteArrayAsync($"http://127.0.0.1:{proxy.BoundPort}/wrong"));
        Assert.Null(proxy.LastFailure);
    }

    private static string Challenge(string algorithm, bool qop) => $"Digest realm=\"fixture\", nonce=\"0123456789\", algorithm={algorithm}" + (qop ? ", qop=\"auth\"" : "");

    // Independent arithmetic, not the implementation's Response/Answer helpers.
    private static bool Valid(string? answer, string method, string uri, string password, string algorithm, bool qop)
    {
        var fields = BroadcastDigest.ParseFields(answer ?? "");
        if (fields is null || fields.GetValueOrDefault("uri") != uri || fields.GetValueOrDefault("username") != "listener") return false;
        string Hash(string text) => Convert.ToHexStringLower(algorithm == "MD5" ? MD5.HashData(Encoding.UTF8.GetBytes(text)) : SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        var a1 = Hash("listener:fixture:" + password);
        var a2 = Hash(method + ":" + uri);
        var expected = qop ? Hash($"{a1}:0123456789:{fields["nc"]}:{fields["cnonce"]}:auth:{a2}") : Hash($"{a1}:0123456789:{a2}");
        return expected == fields.GetValueOrDefault("response");
    }

    private static Task Reply(Stream stream, string start, (string, string)[] fields, byte[] body) =>
        new DigestRtspMessage(start, fields.Select(pair => new KeyValuePair<string, string>(pair.Item1, pair.Item2)).ToList(), body)
            .WriteAsync(stream, CancellationToken.None);
}
