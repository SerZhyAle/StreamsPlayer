using System.Text;
using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0226 / DEVICE-EXCHANGE items DG, DK and DL, the producer's decisions end to end without a socket: who the box is for,
/// what a device joining or leaving does (a new box; or a new group key and epoch), the outward link, and that each
/// listener - an own device, the link, a stranger - reads the stream through the real decoder or does not.
/// </summary>
public sealed class ExchangeBroadcastSealerTests
{
    private const string BroadcastId = "sealer-1";

    private static ExchangeSealedRecipient Recipient(ExchangeE2eKeyPair pair, string id) => new(id, pair.PublicPoint);

    private static byte[] Join(params byte[][] parts) => parts.SelectMany(part => part).ToArray();

    private static (byte[] Clear, ExchangeSealedRelayStatus Status) Listen(byte[] stream, byte[] groupKey, string mediaType = "audio/aac")
    {
        var decoder = new ExchangeSealedRelayDecoder(BroadcastId, [groupKey]);
        var clear = new List<byte[]>();
        var status = decoder.Feed(stream, clear);
        Assert.Equal(mediaType, status == ExchangeSealedRelayStatus.Ok ? decoder.MediaType : mediaType);
        return (Join([.. clear]), status);
    }

    private static byte[] OpenRelayKey(ExchangeSealedBox box, string deviceId, ExchangeE2eKeyPair reader, ExchangeE2eKeyPair sender)
    {
        var opened = ExchangeSealedBoxes.Open(box, ExchangeSealedContext.Broadcast(BroadcastId), deviceId, reader, sender.PublicPoint);
        Assert.True(opened.Opened, opened.Failure.ToString());
        using var payload = JsonDocument.Parse(opened.Payload!);
        Assert.True(ExchangeBase64Url.TryDecode(payload.RootElement.GetProperty("relayKey").GetString(), out var key));
        return key;
    }

    [Fact]
    public void TheBoxHandsEachOwnDeviceTheRelayKeyAndTheTunnelPin_AndTheStreamOpensWithIt()
    {
        using var phone = ExchangeE2eKeyPair.Generate();
        using var tablet = ExchangeE2eKeyPair.Generate();
        using var self = ExchangeE2eKeyPair.Generate();
        using var sealer = new ExchangeBroadcastSealer(BroadcastId, "audio/aac", ExchangeTunnelCertificate.Create());

        var box = sealer.Seal("pc", self, [Recipient(phone, "phone"), Recipient(tablet, "tablet")]);

        Assert.Equal(["phone", "tablet"], box.Keys.Select(key => key.To).Order().ToArray());
        var media = Encoding.ASCII.GetBytes("ADTS-FRAMES-ADTS-FRAMES");
        var stream = Join(sealer.Relay.HeaderChunk([]), sealer.Relay.SealChunk(ExchangeUploadChunks.Joinable, media));
        foreach (var (reader, id) in new[] { (phone, "phone"), (tablet, "tablet") })
        {
            var key = OpenRelayKey(box, id, reader, self);
            var (clear, status) = Listen(stream, key);
            Assert.Equal(ExchangeSealedRelayStatus.Ok, status);
            Assert.Equal(media, clear);
            var opened = ExchangeSealedBoxes.Open(box, ExchangeSealedContext.Broadcast(BroadcastId), id, reader, self.PublicPoint);
            using var payload = JsonDocument.Parse(opened.Payload!);
            Assert.Equal(sealer.Tunnel.Pin, payload.RootElement.GetProperty("tunnelPin").GetString());
        }
    }

    [Fact]
    public void ADeviceThatJoins_GetsANewBoxAndNothingElseChanges()
    {
        using var phone = ExchangeE2eKeyPair.Generate();
        using var tablet = ExchangeE2eKeyPair.Generate();
        using var self = ExchangeE2eKeyPair.Generate();
        using var sealer = new ExchangeBroadcastSealer(BroadcastId, "audio/aac", ExchangeTunnelCertificate.Create());
        sealer.Seal("pc", self, [Recipient(phone, "phone")]);
        var epochBefore = sealer.Relay.Epoch;
        var ownKeyBefore = sealer.Relay.GroupKey(ExchangeBroadcastSealer.OwnPurpose);

        var refresh = sealer.Refresh("pc", self, [Recipient(phone, "phone"), Recipient(tablet, "tablet")]);

        Assert.True(refresh.Changed);
        Assert.False(refresh.NewEpoch);
        Assert.Equal(epochBefore, sealer.Relay.Epoch);
        Assert.Equal(ownKeyBefore, sealer.Relay.GroupKey(ExchangeBroadcastSealer.OwnPurpose));
        Assert.Contains(refresh.Box!.Keys, key => key.To == "tablet");
        // The tablet opens the new box with the same relay key the phone had.
        Assert.Equal(OpenRelayKey(refresh.Box, "tablet", tablet, self), ownKeyBefore);
    }

    [Fact]
    public void ADeviceThatLeaves_MakesTheOwnKeyAnewAndBeginsAnEpoch_SoWhatItHeldOpensNothing()
    {
        using var phone = ExchangeE2eKeyPair.Generate();
        using var tablet = ExchangeE2eKeyPair.Generate();
        using var self = ExchangeE2eKeyPair.Generate();
        using var sealer = new ExchangeBroadcastSealer(BroadcastId, "audio/aac", ExchangeTunnelCertificate.Create());
        var first = sealer.Seal("pc", self, [Recipient(phone, "phone"), Recipient(tablet, "tablet")]);
        var tabletOldKey = OpenRelayKey(first, "tablet", tablet, self);
        var firstStream = Join(sealer.Relay.HeaderChunk([]), sealer.Relay.SealChunk(ExchangeUploadChunks.Joinable, [1, 1, 1]));
        Assert.Equal(ExchangeSealedRelayStatus.Ok, Listen(firstStream, tabletOldKey).Status);

        var refresh = sealer.Refresh("pc", self, [Recipient(phone, "phone")]);

        Assert.True(refresh.Changed);
        Assert.True(refresh.NewEpoch);
        Assert.Equal(2u, sealer.Relay.Epoch);
        Assert.DoesNotContain(refresh.Box!.Keys, key => key.To == "tablet");
        var secondStream = Join(sealer.Relay.HeaderChunk([]), sealer.Relay.SealChunk(ExchangeUploadChunks.Joinable, [2, 2, 2]));
        // The phone follows with the key in the new box; the removed tablet's old key reads nothing of epoch 2.
        var phoneKey = OpenRelayKey(refresh.Box, "phone", phone, self);
        var (phoneClear, phoneStatus) = Listen(secondStream, phoneKey);
        Assert.Equal(ExchangeSealedRelayStatus.Ok, phoneStatus);
        Assert.Equal(new byte[] { 2, 2, 2 }, phoneClear);
        var (clear, status) = Listen(secondStream, tabletOldKey);
        Assert.Equal(ExchangeSealedRelayStatus.EpochNotHeld, status);
        Assert.Empty(clear);
    }

    [Fact]
    public void ARefreshWithTheSameDevicesInAnotherOrderIsNoChange()
    {
        using var phone = ExchangeE2eKeyPair.Generate();
        using var tablet = ExchangeE2eKeyPair.Generate();
        using var self = ExchangeE2eKeyPair.Generate();
        using var sealer = new ExchangeBroadcastSealer(BroadcastId, "audio/aac", ExchangeTunnelCertificate.Create());
        sealer.Seal("pc", self, [Recipient(phone, "phone"), Recipient(tablet, "tablet")]);

        var refresh = sealer.Refresh("pc", self, [Recipient(tablet, "tablet"), Recipient(phone, "phone")]);

        Assert.False(refresh.Changed);
        Assert.False(refresh.NewEpoch);
    }

    [Fact]
    public void ADeviceWhoseKeyChanged_IsOnlyDroppedAsARecipientWhenTheCallerDropsIt()
    {
        using var phone = ExchangeE2eKeyPair.Generate();
        using var phoneAgain = ExchangeE2eKeyPair.Generate();
        using var self = ExchangeE2eKeyPair.Generate();
        using var sealer = new ExchangeBroadcastSealer(BroadcastId, "audio/aac", ExchangeTunnelCertificate.Create());
        sealer.Seal("pc", self, [Recipient(phone, "phone")]);

        // The directory layer withholds a device whose key changed (item DG), which reads here as one that left.
        var refresh = sealer.Refresh("pc", self, []);

        Assert.True(refresh.NewEpoch);
        Assert.Empty(refresh.Box!.Keys);
    }

    [Fact]
    public void TheLinkKeyOpensTheStreamUntilTheLinkIsWithdrawn()
    {
        using var self = ExchangeE2eKeyPair.Generate();
        using var sealer = new ExchangeBroadcastSealer(BroadcastId, "video/mp2t", ExchangeTunnelCertificate.Create());
        sealer.Seal("pc", self, []);
        var linkKey = sealer.LinkKey()!;
        var header = new byte[188];
        header[0] = 0x47;
        var stream = Join(sealer.Relay.HeaderChunk(header), sealer.Relay.SealChunk(ExchangeUploadChunks.Joinable, header));

        var (clear, status) = Listen(stream, linkKey, "video/mp2t");
        Assert.Equal(ExchangeSealedRelayStatus.Ok, status);
        Assert.Equal(Join(header, header), clear);

        sealer.WithdrawLink();
        var after = Join(sealer.Relay.HeaderChunk(header), sealer.Relay.SealChunk(ExchangeUploadChunks.Joinable, header));

        Assert.Equal(ExchangeSealedRelayStatus.EpochNotHeld, Listen(after, linkKey, "video/mp2t").Status);
        Assert.NotEqual(linkKey, sealer.LinkKey());
        Assert.Equal(ExchangeSealedRelayStatus.Ok, Listen(after, sealer.LinkKey()!, "video/mp2t").Status);
    }

    [Fact]
    public void ABoxForNobody_IsAValidBoxAndTheRelayStillWorksForTheLink()
    {
        using var self = ExchangeE2eKeyPair.Generate();
        using var sealer = new ExchangeBroadcastSealer(BroadcastId, "audio/aac", ExchangeTunnelCertificate.Create());

        var box = sealer.Seal("pc", self, []);

        Assert.Empty(box.Keys);
        Assert.NotNull(sealer.Relay.HeaderChunk([]));
    }

    [Fact]
    public void AProtectedBroadcastsPairRidesInsideTheBoxAndIsNeverInClear()
    {
        using var phone = ExchangeE2eKeyPair.Generate();
        using var self = ExchangeE2eKeyPair.Generate();
        using var sealer = new ExchangeBroadcastSealer(BroadcastId, "audio/aac", ExchangeTunnelCertificate.Create());

        var box = sealer.Seal("pc", self, [Recipient(phone, "phone")], new ExchangeRelayCredentials("listener", "s3cret-pass"));

        Assert.DoesNotContain("s3cret-pass", JsonSerializer.Serialize(box, ExchangeProtocol.JsonOptions));
        var record = new ExchangeBroadcastRecord(BroadcastId, "pc", "t", "AUDIO_ONLY", null, Sealed: box);
        var trust = new ExchangeDeviceTrust();
        trust.ObserveDevice("pc", self.PublicPoint);
        Assert.Equal(ExchangeSealedState.None, ExchangeBroadcastKeys.OpenRecord(record, "phone", phone, trust));
        Assert.Equal(new ExchangeRelayCredentials("listener", "s3cret-pass"), ExchangeBroadcastKeys.Find(BroadcastId)!.Credentials);
        ExchangeBroadcastKeys.Clear();
    }

    // ------------------------------------------------------------------ the descriptor writer

    private static BroadcastWirePlan Plan(BroadcastSealMode mode, string? key = null, string? pin = null) => new(
        BroadcastId, "pc-device", "Mic", BroadcastSource.Microphone, "192.168.1.5", 8554, null, "exchange.example.net:22022",
        true, true, "SHA256:" + new string('A', 43), 48000, 2, 128, Seal: mode, LinkGroupKey: key, TunnelPin: pin);

    [Fact]
    public void ARecordDescriptor_DeclaresTheSealVersionAndCarriesNoKey()
    {
        var read = FastMediaSorterBroadcastDescriptor.Read(BroadcastDescriptorWriter.Write(Plan(BroadcastSealMode.Record)));

        Assert.True(read.IsAccepted);
        var exchange = read.Broadcast!.Endpoints.Where(ExchangeEndpointKinds.IsExchangeEndpoint).ToArray();
        Assert.Equal(["RELAY", "TUNNEL"], exchange.Select(e => e.Transport).ToArray());
        Assert.All(exchange, endpoint => Assert.True(endpoint.Seal is { IsKnown: true, Key: null, Pin: null }));
        Assert.Null(read.Broadcast.Endpoints.Single(e => e.Transport == "HTTP").Seal);
        Assert.Equal(["HTTP", "RELAY", "TUNNEL"], read.Broadcast.PlaybackAttemptEndpoints().Select(e => e.Transport).ToArray());
    }

    [Fact]
    public void AnOutwardDescriptor_CarriesTheKeysAndLeavesOutAnEndpointNobodyCouldPlay()
    {
        var key = ExchangeBase64Url.Encode(new byte[32]);
        var full = FastMediaSorterBroadcastDescriptor.Read(BroadcastDescriptorWriter.Write(Plan(BroadcastSealMode.Outward, key, "SHA256:" + new string('B', 43))));
        var noKey = FastMediaSorterBroadcastDescriptor.Read(BroadcastDescriptorWriter.Write(Plan(BroadcastSealMode.Outward, null, "SHA256:" + new string('B', 43))));
        var nothing = FastMediaSorterBroadcastDescriptor.Read(BroadcastDescriptorWriter.Write(Plan(BroadcastSealMode.Outward)));

        Assert.Equal(new byte[32], full.Broadcast!.Endpoints.Single(e => e.Transport == "RELAY").Seal!.GroupKey());
        Assert.Equal("SHA256:" + new string('B', 43), full.Broadcast.Endpoints.Single(e => e.Transport == "TUNNEL").Seal!.Pin);
        Assert.DoesNotContain(noKey.Broadcast!.Endpoints, e => e.Transport == "RELAY");
        Assert.Contains(noKey.Broadcast.Endpoints, e => e.Transport == "TUNNEL");
        Assert.Equal(["HTTP"], nothing.Broadcast!.Endpoints.Select(e => e.Transport).ToArray());
    }

    [Fact]
    public void TheRecordCarriesTheBoxOnlyWhenThereIsOne()
    {
        using var self = ExchangeE2eKeyPair.Generate();
        using var sealer = new ExchangeBroadcastSealer(BroadcastId, "audio/aac", ExchangeTunnelCertificate.Create());
        var plan = Plan(BroadcastSealMode.Record);
        var descriptor = BroadcastDescriptorWriter.Write(plan);

        var plain = BroadcastDescriptorWriter.BroadcastRecord(plan, descriptor, DateTimeOffset.UnixEpoch);
        var withBox = BroadcastDescriptorWriter.BroadcastRecord(plan, descriptor, DateTimeOffset.UnixEpoch,
            sealer.Seal("pc-device", self, []));

        Assert.False(plain.ContainsKey("sealed"));
        using var json = JsonSerializer.SerializeToDocument(withBox, ExchangeProtocol.JsonOptions);
        Assert.NotNull(ExchangeSealedBox.TryRead(json.RootElement.GetProperty("sealed")));
        Assert.Equal(BroadcastId, json.RootElement.GetProperty("broadcastId").GetString());
    }
}
