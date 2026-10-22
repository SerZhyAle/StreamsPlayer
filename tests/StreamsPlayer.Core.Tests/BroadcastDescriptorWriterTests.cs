using System.Text.Json;
using StreamsPlayer.Core;
using Xunit;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0217's description tests, as the acceptance criteria state them: the endpoint order is the
/// producer's list, no loopback address is ever written, `TUNNEL.inner` keeps the scheme and the path
/// on the documentation host when the LAN opt-in is off, a video LAN URL carries the opaque segment,
/// the same description is produced for a link, a file and a barcode, and `sourceId` is stable.
/// </summary>
public sealed class BroadcastDescriptorWriterTests
{
    private static BroadcastWirePlan Plan(
        string? lan = "192.168.1.5", bool relay = true, bool tunnel = true, string? token = null,
        BroadcastSource source = BroadcastSource.Microphone, string? pin = "SHA256:abcdefghij") => new(
        "bcast-id", "device-id", "Test microphone", source, lan, 8768, token, "example.test:44022",
        relay, tunnel, pin, 44100, 1, 128);

    private static FastMediaSorterBroadcast ReadBack(string descriptor)
    {
        var read = FastMediaSorterBroadcastDescriptor.Read(descriptor);
        Assert.True(read.IsAccepted, read.Status.ToString());
        return read.Broadcast!;
    }

    [Fact]
    public void Endpoints_AreWrittenInTheProducersOrder()
    {
        var read = ReadBack(BroadcastDescriptorWriter.Write(Plan()));
        var transports = read.Endpoints.Select(endpoint => endpoint.Transport).ToArray();
        Assert.Equal(new[] { "HTTP", "RELAY", "TUNNEL" }, transports);
    }

    [Fact]
    public void WithoutLan_TheExchangeEndpointsStillComeInOrder()
    {
        var read = ReadBack(BroadcastDescriptorWriter.Write(Plan(lan: null)));
        Assert.Equal(new[] { "RELAY", "TUNNEL" }, read.Endpoints.Select(endpoint => endpoint.Transport));
        // Producer rule 1 with item D: a broadcast without a LAN address starts; the top-level address
        // is the relay's, a plain HTTPS stream any player can open.
        Assert.StartsWith("https://example.test:44022/v2/b/", read.Url, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("::1")]
    [InlineData("localhost")]
    public void NeverALoopbackOrUnspecifiedAddress(string address)
    {
        Assert.Throws<ArgumentException>(() => BroadcastDescriptorWriter.Write(Plan(lan: address)));
    }

    [Fact]
    public void WithoutLan_TheTunnelInnerKeepsSchemeAndPathOnTheDocumentationHost()
    {
        var read = ReadBack(BroadcastDescriptorWriter.Write(Plan(lan: null)));
        var tunnel = read.Endpoints.Single(endpoint => endpoint.Transport == "TUNNEL");
        Assert.Equal("http://192.0.2.1:8768/live-audio.aac", tunnel.Inner);
    }

    [Fact]
    public void WithLan_TheTunnelInnerPointsAtTheLanShape()
    {
        var read = ReadBack(BroadcastDescriptorWriter.Write(Plan()));
        var tunnel = read.Endpoints.Single(endpoint => endpoint.Transport == "TUNNEL");
        Assert.Equal("http://192.168.1.5:8768/live-audio.aac", tunnel.Inner);
    }

    [Fact]
    public void AVideoLanUrlCarriesTheOpaqueSegment()
    {
        var descriptor = BroadcastDescriptorWriter.Write(Plan(
            source: BroadcastSource.Screen, token: "opaque-token-1"));
        var read = ReadBack(descriptor);
        var lan = read.Endpoints.Single(endpoint => endpoint.Transport == "HTTP");
        Assert.Equal("http://192.168.1.5:8768/opaque-token-1/live.ts", lan.Url);
        Assert.Equal(1000, lan.TargetLatencyMs);
        Assert.Equal("h264", lan.VideoCodec);

        // And the same video broadcast without its token is refused, not published bare.
        Assert.Throws<ArgumentException>(() =>
            BroadcastDescriptorWriter.Write(Plan(source: BroadcastSource.Screen, token: "  ")));
    }

    [Fact]
    public void CertFingerprintIsOnTheExchangeEndpointsNotOnTheLanOne()
    {
        var read = ReadBack(BroadcastDescriptorWriter.Write(Plan()));
        Assert.Null(read.Endpoints[0].CertFingerprint);
        Assert.Equal("SHA256:abcdefghij", read.Endpoints[1].CertFingerprint);
        Assert.Equal("SHA256:abcdefghij", read.Endpoints[2].CertFingerprint);
    }

    [Fact]
    public void LatencyAndQualityFollowTheContract()
    {
        var read = ReadBack(BroadcastDescriptorWriter.Write(Plan()));
        Assert.Equal(500, read.Endpoints[0].TargetLatencyMs);
        Assert.Equal(2000, read.Endpoints[1].TargetLatencyMs);
        Assert.Equal(2000, read.Endpoints[2].TargetLatencyMs);
        Assert.Equal(44100, read.Endpoints[0].SampleRate);
        Assert.Equal(128000, read.Endpoints[0].Bitrate);
        Assert.True(read.IsLive);
        Assert.Equal("AUDIO_ONLY", read.Mode);
    }

    [Fact]
    public void SourceIdNamesTheSourceAndIsStableAcrossRestarts()
    {
        // Item G: `<deviceSourceId>:<source>`, stable because the device id is - the restart reads the
        // same device id from the account store, so the same sourceId comes out again.
        var first = BroadcastSources.SourceId("device-id", BroadcastSource.Microphone);
        var second = BroadcastSources.SourceId("device-id", BroadcastSource.Microphone);
        Assert.Equal("device-id:mic", first);
        Assert.Equal(first, second);
        Assert.Equal("device-id:system-audio", BroadcastSources.SourceId("device-id", BroadcastSource.SystemSound));
        Assert.Equal("device-id:screen", BroadcastSources.SourceId("device-id", BroadcastSource.Screen));
        Assert.Equal("device-id:camera", BroadcastSources.SourceId("device-id", BroadcastSource.Camera));

        var read = ReadBack(BroadcastDescriptorWriter.Write(Plan()));
        Assert.Equal("device-id:mic", read.SourceId);
    }

    [Fact]
    public void TheSameDescriptionForALinkAFileAndABarcode()
    {
        var descriptor = BroadcastDescriptorWriter.Write(Plan());
        var channels = new[]
        {
            FastMediaSorterBroadcastShare.ImportFileContent(descriptor),
            FastMediaSorterBroadcastShare.BarcodeText(descriptor),
            FastMediaSorterBroadcastShare.Link(descriptor)
        };

        foreach (var channel in channels)
        {
            var read = FastMediaSorterBroadcastDescriptor.Read(channel);
            Assert.True(read.IsAccepted, read.Status.ToString());
            Assert.Equal(read.Broadcast!.Url, ReadBack(descriptor).Url);
            Assert.Equal(read.Broadcast.SourceId, ReadBack(descriptor).SourceId);
            Assert.Equal(read.Broadcast.Endpoints.Count, ReadBack(descriptor).Endpoints.Count);
        }
    }

    [Fact]
    public void TheBroadcastRecordCarriesTheDescriptorVerbatim()
    {
        var plan = Plan();
        var descriptor = BroadcastDescriptorWriter.Write(plan);
        var record = BroadcastDescriptorWriter.BroadcastRecord(plan, descriptor, DateTimeOffset.UtcNow);
        var json = JsonSerializer.Serialize(record, ExchangeProtocol.JsonOptions);
        using var document = JsonDocument.Parse(json);
        Assert.Equal("bcast-id", document.RootElement.GetProperty("broadcastId").GetString());
        Assert.Equal("device-id", document.RootElement.GetProperty("deviceId").GetString());
        Assert.Equal("AUDIO_ONLY", document.RootElement.GetProperty("mode").GetString());
        var embedded = document.RootElement.GetProperty("descriptor").GetRawText();
        var read = FastMediaSorterBroadcastDescriptor.Read(embedded);
        Assert.True(read.IsAccepted);
    }

    [Fact]
    public void SuggestedFileNameFollowsTheContract()
    {
        Assert.Equal("broadcast_1.fmsbcast", FastMediaSorterBroadcastShare.SuggestedFileName(1));
        Assert.Equal("broadcast_2.fmsbcast", FastMediaSorterBroadcastShare.SuggestedFileName(2));
    }
}
