using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class ExchangeProtocolTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(16385)]
    [InlineData(uint.MaxValue)]
    public async Task InvalidOpeningLength_ClosesWithoutAllocating(uint length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, length);
        using var stream = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() => ExchangeProtocol.ReadAsync(stream, false, default));
        Assert.False(stream.CanRead);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("1")]
    [InlineData("{}{}")]
    [InlineData("{invalid}")]
    public async Task NonObjectOrInvalidBody_Closes(string body)
    {
        using var stream = Frame(body);
        await Assert.ThrowsAnyAsync<Exception>(() => ExchangeProtocol.ReadAsync(stream, false, default));
        Assert.False(stream.CanRead);
    }

    [Fact]
    public async Task OpeningBoundary_IsIncludedButLargerFrameNeedsAuthentication()
    {
        using var exact = Frame("{\"padding\":\"" + new string('x', ExchangeProtocol.OpeningLimit - 14) + "\"}");
        using var opening = await ExchangeProtocol.ReadAsync(exact, false, default);
        Assert.Equal(ExchangeProtocol.OpeningLimit - 14, opening.RootElement.GetProperty("padding").GetString()!.Length);
        using var large = Frame("{\"padding\":\"" + new string('x', 20000) + "\"}");
        using var authenticated = await ExchangeProtocol.ReadAsync(large, true, default);
        Assert.Equal(20000, authenticated.RootElement.GetProperty("padding").GetString()!.Length);
        using var oversize = Frame("{\"padding\":\"" + new string('x', ExchangeProtocol.AuthenticatedLimit) + "\"}");
        await Assert.ThrowsAsync<InvalidDataException>(() => ExchangeProtocol.ReadAsync(oversize, true, default));
        Assert.False(oversize.CanRead);
    }

    [Fact]
    public async Task Envelope_RoundTripsUnknownMembersAndType()
    {
        using var stream = new MemoryStream();
        await ExchangeProtocol.WriteAsync(stream, new { schemaVersion = 2, type = "future", extra = new { value = "привет" } }, false, default);
        stream.Position = 0;
        using var frame = await ExchangeProtocol.ReadAsync(stream, false, default);
        Assert.Equal("future", ExchangeProtocol.String(frame.RootElement, "type"));
        Assert.Equal("привет", frame.RootElement.GetProperty("extra").GetProperty("value").GetString());
    }

    [Theory]
    [InlineData("password")]
    [InlineData("pairingCode")]
    public async Task Enrollment_WritesOnlySelectedProofAndWipesMutableSecret(string member)
    {
        var secret = "Aa secret 1234".ToCharArray();
        using var stream = new MemoryStream();
        await ExchangeProtocol.WriteEnrollmentAsync(stream, new { schemaVersion = 2, type = "enroll" }, member, secret, default);
        Assert.All(secret, character => Assert.Equal('\0', character));
        stream.Position = 0;
        using var frame = await ExchangeProtocol.ReadAsync(stream, false, default);
        Assert.Equal("Aa secret 1234", frame.RootElement.GetProperty(member).GetString());
        Assert.False(frame.RootElement.TryGetProperty(member == "password" ? "pairingCode" : "password", out _));
    }

    [Fact]
    public async Task Enrollment_WriteFailureAlsoWipesSecret()
    {
        var secret = "secret".ToCharArray();
        using var stream = new MemoryStream(new byte[1], writable: false);
        await Assert.ThrowsAsync<NotSupportedException>(() => ExchangeProtocol.WriteEnrollmentAsync(stream,
            new { schemaVersion = 2, type = "enroll" }, "password", secret, default));
        Assert.All(secret, character => Assert.Equal('\0', character));
    }

    [Theory]
    [InlineData("bad-credentials", "ExchangeBadCredentials")]
    [InlineData("device-revoked", "ExchangeDeviceRevoked")]
    [InlineData("version-unsupported", "ExchangeVersionUnsupported")]
    [InlineData("tls-required", "ExchangeTlsRequired")]
    [InlineData("id-collision", "ExchangeIdCollision")]
    [InlineData("capacity", "ExchangeCapacity")]
    [InlineData("port-unavailable", "ExchangePortUnavailable")]
    [InlineData("unavailable", "ExchangeUnavailable")]
    [InlineData("rate-limited", "ExchangeRateLimited")]
    [InlineData("future", "ExchangeRefused")]
    [InlineData(null, "ExchangeRefused")]
    public void AllRefusals_MapToDistinctKeysWithGenericFallback(string? reason, string key) =>
        Assert.Equal(key, ExchangeProtocol.RefusalKey(reason));

    [Fact]
    public void Capabilities_AdvertiseOnlyExistingPlaybackPaths()
    {
        // SP-0203 requirement 6: exactly the pairs a playback path opens. HTTP video is the MPEG-TS
        // shape in the player window; RELAY audio rides the pinned HTTP client, RELAY video the
        // engine (through the pin proxy when the descriptor pins the certificate); TUNNEL rides the
        // SP-0204 forwarder; P2P stays reserved and unimplemented.
        Assert.Equal(new[]
        {
            "AUDIO_ONLY:HTTP", "VIDEO_ONLY:HTTP", "VIDEO_AUDIO:HTTP",
            "VIDEO_ONLY:RTSP", "VIDEO_AUDIO:RTSP",
            "AUDIO_ONLY:RELAY", "VIDEO_ONLY:RELAY", "VIDEO_AUDIO:RELAY",
            "AUDIO_ONLY:TUNNEL", "VIDEO_ONLY:TUNNEL", "VIDEO_AUDIO:TUNNEL"
        },
            ExchangeCapabilities.Plays.Select(pair => $"{pair.Mode}:{pair.Transport}"));
        foreach (var pair in ExchangeCapabilities.Plays)
        {
            var url = pair.Transport switch
            {
                "HTTP" when pair.Mode == FastMediaSorterBroadcastDescriptor.AudioOnlyMode => "http://localhost/audio",
                "HTTP" => "http://localhost/live.ts",
                "RTSP" => "rtsp://localhost/video",
                "RELAY" => "https://relay.example.net:44022/v2/b/b1/stream",
                "TUNNEL" when pair.Mode == FastMediaSorterBroadcastDescriptor.AudioOnlyMode => "fmsx://localhost:44022/b/b1/http",
                _ => "fmsx://localhost:44022/b/b1/rtsp"
            };
            var inner = pair.Transport == "TUNNEL"
                ? (pair.Mode == FastMediaSorterBroadcastDescriptor.AudioOnlyMode ? "http://127.0.0.1/audio" : "rtsp://127.0.0.1/video")
                : null;
            var endpoint = new FastMediaSorterBroadcastEndpoint(
                url,
                pair.Transport, pair.Mode, null, null, null, null, true, 500, null, inner);
            var descriptor = new FastMediaSorterBroadcast("http://localhost/fallback", pair.Mode, "", null, true, 500, [endpoint]);
            Assert.Equal(endpoint, descriptor.SelectPlaybackEndpoint());
            // And the persisted form a reconnect leg reads offers the same pair as an attempt.
            var info = new FastMediaSorterBroadcastInfo { Mode = pair.Mode, Endpoints = [endpoint] };
            Assert.Equal([endpoint], info.PlaybackAttemptEndpoints());
        }

        using var receiver = JsonSerializer.SerializeToDocument(ExchangeCapabilities.Receiver);
        Assert.Equal(11, receiver.RootElement.GetProperty("plays").GetArrayLength());
    }

    [Fact]
    public void DeviceIdentity_IsShareIdFormAndRandom()
    {
        var id = ExchangeProtocol.NewDeviceId();
        Assert.Matches("^[A-Za-z0-9_-]{22}$", id);
        Assert.NotEqual(id, ExchangeProtocol.NewDeviceId());
        Assert.Equal(16, Convert.FromBase64String(id.Replace('-', '+').Replace('_', '/') + "==").Length);
    }

    [Fact]
    public void DiagnosticRedaction_CoversAccountFieldsAndBareFingerprint()
    {
        var fingerprint = ExchangeProtocol.Fingerprint([1, 2, 3]);
        var text = "{\"login\":\"private-user\",\"deviceToken\":\"private-token\",\"pairingCode\":\"ABCD1234\",\"exchangeAddress\":\"server.local\",\"password\":\"a\\\"b\"} " + fingerprint;
        var redacted = ExchangeDiagnosticRedactor.Redact(text);
        foreach (var secret in new[] { "private-user", "private-token", "ABCD1234", "server.local", "a\\\"b", fingerprint })
        {
            Assert.DoesNotContain(secret, redacted);
        }

        Assert.Contains("[REDACTED]", redacted);
    }

    [Fact]
    public void Refusals_ArePresentInEveryShippedLanguage()
    {
        string[] reasons = ["bad-credentials", "device-revoked", "version-unsupported", "tls-required",
            "id-collision", "capacity", "port-unavailable", "unavailable", "rate-limited", "future"];
        foreach (var dictionary in LocalizationDictionary.LoadAll())
        {
            foreach (var reason in reasons)
            {
                var key = ExchangeProtocol.RefusalKey(reason);
                Assert.True(dictionary.Values.TryGetValue(key, out var text) && !string.IsNullOrWhiteSpace(text), $"{dictionary.Code}:{key}");
            }
        }
    }

    [Fact]
    public async Task Opening_IgnoresFutureTypeThenAcceptsWelcome()
    {
        using var stream = new MemoryStream();
        await ExchangeProtocol.WriteAsync(stream, new { schemaVersion = 2, type = "future", member = "ignored" }, false, default);
        await ExchangeProtocol.WriteAsync(stream, new
        {
            schemaVersion = 2, type = "welcome", account = "test", publicEndpoint = "localhost:1234",
            serverVersion = "fixture", keepaliveSeconds = 30, features = new[] { "future-feature" }
        }, false, default);
        stream.Position = 0;
        using var opening = await ExchangeHandshake.ReadAsync(stream, "welcome", default);
        Assert.Equal("welcome", ExchangeProtocol.String(opening.RootElement, "type"));
        Assert.Equal(30, ExchangeHandshake.ReadInterval(opening.RootElement));
        Assert.True(stream.CanRead);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":2,\"type\":\"enroll\"}")]
    [InlineData("{\"schemaVersion\":1,\"type\":\"welcome\"}")]
    [InlineData("{\"schemaVersion\":\"2\",\"type\":\"welcome\"}")]
    [InlineData("{\"schemaVersion\":2,\"type\":1}")]
    public async Task Opening_InvalidSchemaOrKnownWrongStateCloses(string envelope)
    {
        using var stream = Frame(envelope);
        await Assert.ThrowsAsync<InvalidDataException>(() => ExchangeHandshake.ReadAsync(stream, "welcome", default));
        Assert.False(stream.CanRead);
    }

    [Fact]
    public void Welcome_RequiresMetadataAndABoundedNegotiatedKeepalive()
    {
        using var absent = JsonDocument.Parse("{\"schemaVersion\":2,\"type\":\"welcome\",\"keepaliveSeconds\":30}");
        Assert.Throws<InvalidDataException>(() => ExchangeHandshake.ReadInterval(absent.RootElement));
        using var invalidInterval = JsonSerializer.SerializeToDocument(new
        {
            account = "test", publicEndpoint = "localhost:1234", serverVersion = "fixture",
            keepaliveSeconds = 121, features = Array.Empty<string>()
        });
        Assert.Throws<InvalidDataException>(() => ExchangeHandshake.ReadInterval(invalidInterval.RootElement));
    }

    private static MemoryStream Frame(string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var stream = new MemoryStream();
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)bytes.Length);
        stream.Write(header);
        stream.Write(bytes);
        stream.Position = 0;
        return stream;
    }
}
