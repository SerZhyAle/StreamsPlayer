using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class ExchangeAuditRemainderTests
{
    [Theory]
    [InlineData(1024, false, true)]
    [InlineData(1025, false, false)]
    [InlineData(64, true, true)]
    [InlineData(65, true, false)]
    public void EnrollmentSecretBoundsAreAppliedBeforeTheWire(int length, bool code, bool accepted) =>
        Assert.Equal(accepted, ExchangeEnrollmentFields.IsValidSecret(new string('x', length), code));

    [Fact]
    public void EnrollmentChecksUtf8BytesUnicodeCharactersAndControls()
    {
        Assert.True(ExchangeEnrollmentFields.IsValidSecret(new string('я', 512), false));
        Assert.False(ExchangeEnrollmentFields.IsValidSecret(new string('я', 513), false));
        Assert.True(ExchangeEnrollmentFields.IsValidSecret(string.Concat(Enumerable.Repeat("\U0001F600", 64)), true));
        Assert.False(ExchangeEnrollmentFields.IsValidSecret("word\n", false));
        Assert.False(ExchangeEnrollmentFields.IsValidSecret("\uD800", false));
        Assert.False(ExchangeEnrollmentFields.IsValidSecret("code\u007F", true));
    }

    [Fact]
    public void ADirectoryCanOnlyEndChannelsFromItsOwnEntry_AndRemovalKeepsTheirIdentity()
    {
        var firstIdentity = ExchangeAccountIdentity.Key("SERVER.Example", 44022, "Login");
        Assert.Equal(firstIdentity, ExchangeAccountIdentity.Key("server.example", 44022, "login"));
        var secondIdentity = ExchangeAccountIdentity.Key("server.example", 44022, "someone-else");
        var descriptor = FastMediaSorterBroadcastDescriptor.Read("""
            {"schemaVersion":1,"title":"Radio","url":"http://192.168.1.2/live-audio.aac","mode":"AUDIO_ONLY","sourceId":"audit-source"}
            """).Broadcast!;
        var kept = FastMediaSorterBroadcastImport.Apply([], descriptor, DateTimeOffset.UtcNow, "audit-b1",
            directoryEntryIdentity: firstIdentity).Channel;

        Assert.Empty(ExchangeDirectoryChannels.MarkEnded([kept], [], [], DateTimeOffset.UtcNow, secondIdentity));
        Assert.Single(ExchangeDirectoryChannels.MarkEnded([kept], [], [], DateTimeOffset.UtcNow, firstIdentity));
        var removed = Assert.Single(ExchangeDirectoryChannels.ReconcileEntries([kept], [secondIdentity]));
        Assert.Equal(kept.Id, removed.Id);
        Assert.True(removed.FastMediaSorterBroadcast!.DirectoryEntryRemoved);
        var rebound = Assert.Single(ExchangeDirectoryChannels.ReconcileEntries([removed], [firstIdentity]));
        Assert.False(rebound.FastMediaSorterBroadcast!.DirectoryEntryRemoved);
    }

    [Fact]
    public void AnAcceptedCertificatePinStaysWithItsChannelAcrossADescriptorRefresh()
    {
        var descriptor = FastMediaSorterBroadcastDescriptor.Read("""
            {"schemaVersion":1,"title":"Radio","url":"https://server.example/live-audio.aac","mode":"AUDIO_ONLY","sourceId":"audit-trust",
             "endpoints":[{"url":"https://server.example/live-audio.aac","transport":"HTTP","mode":"AUDIO_ONLY"}]}
            """).Broadcast!;
        var imported = FastMediaSorterBroadcastImport.Apply([], descriptor, DateTimeOffset.UtcNow).Channel;
        var identity = BroadcastCertificateTrust.EndpointIdentity(descriptor.Url)!;
        imported = imported with { FastMediaSorterBroadcast = imported.FastMediaSorterBroadcast! with
        {
            CertificatePins = new() { [identity] = "SHA256:accepted" }
        } };
        var refreshed = FastMediaSorterBroadcastImport.Apply([imported], descriptor, DateTimeOffset.UtcNow).Channel;

        Assert.Equal("SHA256:accepted", Assert.Single(refreshed.FastMediaSorterBroadcast!.PlaybackAttemptEndpoints()).CertFingerprint);
        var other = FastMediaSorterBroadcastImport.Apply([], descriptor, DateTimeOffset.UtcNow).Channel;
        Assert.Null(Assert.Single(other.FastMediaSorterBroadcast!.PlaybackAttemptEndpoints()).CertFingerprint);
    }

    [Theory]
    [InlineData(500u)]
    [InlineData(1500u)]
    [InlineData(2000u)]
    public void ABroadcastLiveEdgeFollowsItsEndpointBuffer(uint milliseconds)
    {
        var corridor = LiveEdgeCorridor.ForBroadcast(milliseconds);
        Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), corridor.Target);
        Assert.True(corridor.Floor < corridor.Target);
        Assert.Equal(corridor.Target * 2, corridor.Buffer);
        Assert.True(corridor.PeakSpeed < LiveEdgeCorridor.QueueFlushSpeed);
        Assert.Equal(TimeSpan.FromSeconds(10), LiveEdgeCorridor.Default.Target);
    }

    [Fact]
    public void AProducerMustHaveAReachableRoute()
    {
        Assert.False(BroadcastProducerGate.IsReachable(null, false, true, true, false, ["relay"]));
        Assert.False(BroadcastProducerGate.IsReachable(null, true, true, false, false, ["relay"]));
        Assert.False(BroadcastProducerGate.IsReachable(null, true, false, true, false, ["relay"]));
        Assert.True(BroadcastProducerGate.IsReachable("192.168.1.2", false, false, false, true, []));
        Assert.True(BroadcastProducerGate.IsReachable(null, true, true, true, false, ["tunnel"]));
    }

    [Theory]
    [InlineData("https://example.net/live", "RELAY")]
    [InlineData("https://example.net/live", "HTTP")]
    [InlineData("https://example.net/v2/b//stream", "RELAY")]
    public void ASealedRelayWithoutAnIdCannotBecomeAClearAttempt(string url, string transport)
    {
        var endpoint = new FastMediaSorterBroadcastEndpoint(url, transport, "VIDEO_ONLY", null, null, null, null, true, 1000,
            Seal: new ExchangeEndpointSeal(1, ExchangeBase64Url.Encode(new byte[32])));

        var decision = ExchangeBroadcastKeys.Decide(endpoint);

        Assert.False(decision.Allowed);
        Assert.Null(decision.RelayOptions(url));
        Assert.Equal(ExchangeSealedState.SealedFailed, decision.State);
    }

    [Fact]
    public void ARegisteringGrantNeitherAppearsNorPinsItsSender_TheRealBoxMayComeFromAnotherDevice()
    {
        using var registeringSender = ExchangeE2eKeyPair.Generate();
        using var actualSender = ExchangeE2eKeyPair.Generate();
        using var guest = ExchangeE2eKeyPair.Generate();
        var trust = new ExchangeDeviceTrust();
        var registering = ExchangeSealedBoxes.Seal("{}"u8, ExchangeSealedContext.Grant("audit-grant"),
            "owner-a", registeringSender, []);
        var record = new ExchangeBroadcastRecord("audit-granted", "owner-a", "t", "AUDIO_ONLY", null,
            Sealed: registering, GrantId: "audit-grant");

        Assert.Equal(ExchangeSealedState.None, ExchangeBroadcastKeys.OpenRecord(record, "guest", guest, trust));
        Assert.Null(trust.TrustedGrant("audit-grant"));
        Assert.Empty(Directory(registering, "audit-grant").Groups.SelectMany(group => group.Broadcasts));

        var actual = ExchangeSealedBoxes.Seal("{}"u8, ExchangeSealedContext.Grant("audit-grant"),
            "owner-b", actualSender, [new("guest", guest.PublicPoint)]);
        Assert.Equal(ExchangeSealedState.None, ExchangeBroadcastKeys.OpenRecord(record with { Sealed = actual }, "guest", guest, trust));
        Assert.Equal(actualSender.PublicPoint, trust.TrustedGrant("audit-grant"));
        Assert.Single(Directory(actual, "audit-grant").Groups.SelectMany(group => group.Broadcasts));
    }

    [Fact]
    public void AnOffCurveDeviceDoesNotPreventOtherRecipientsFromOpeningTheBox()
    {
        using var sender = ExchangeE2eKeyPair.Generate();
        using var reader = ExchangeE2eKeyPair.Generate();
        var invalid = new byte[65];
        invalid[0] = 4;
        Assert.False(ExchangeE2eKeyPair.IsValidPublicPoint(invalid));
        var device = new ExchangeDeviceRecord("off-curve", "bad", true, null, invalid, true);
        var trust = new ExchangeDeviceTrust();
        Assert.Equal(ExchangeSealedState.E2eMissing, ExchangeRecipients.Classify(device, trust));
        Assert.Null(trust.TrustedDevice(device.DeviceId));

        var box = ExchangeSealedBoxes.Seal("{}"u8, "broadcast:audit", "sender", sender,
            [new("bad", invalid), new("reader", reader.PublicPoint)]);

        Assert.Equal("reader", Assert.Single(box.Keys).To);
        Assert.True(ExchangeSealedBoxes.Open(box, "broadcast:audit", "reader", reader, sender.PublicPoint).Opened);
    }

    [Fact]
    public void TheFirstRefreshBuildsABoxEvenForAnEmptyAccount()
    {
        using var sender = ExchangeE2eKeyPair.Generate();
        using var sealer = new ExchangeBroadcastSealer("audit-empty", "audio/aac", ExchangeTunnelCertificate.Create());

        var refresh = sealer.Refresh("sender", sender, []);

        Assert.True(refresh.Changed);
        Assert.NotNull(refresh.Box);
        Assert.Empty(refresh.Box.Keys);
    }

    [Fact]
    public void ReconnectRefreshRevokesAFormerRecipientBeforeTheNewUpload()
    {
        using var sender = ExchangeE2eKeyPair.Generate();
        using var removed = ExchangeE2eKeyPair.Generate();
        using var kept = ExchangeE2eKeyPair.Generate();
        using var sealer = new ExchangeBroadcastSealer("audit-reconnect", "audio/aac", ExchangeTunnelCertificate.Create());
        var first = sealer.Refresh("sender", sender, [new("removed", removed.PublicPoint), new("kept", kept.PublicPoint)]);
        var opening = ExchangeSealedBoxes.Open(first.Box!, "broadcast:audit-reconnect", "removed", removed, sender.PublicPoint);
        using var payload = JsonDocument.Parse(opening.Payload!);
        Assert.True(ExchangeBase64Url.TryDecode(payload.RootElement.GetProperty("relayKey").GetString(), out var oldKey));
        var before = sealer.Relay.Epoch;

        var next = sealer.Refresh("sender", sender, [new("kept", kept.PublicPoint)]);
        Assert.True(next.NewEpoch);
        Assert.True(sealer.Relay.Epoch > before);
        Assert.DoesNotContain(next.Box!.Keys, key => key.To == "removed");
        var decoder = new ExchangeSealedRelayDecoder("audit-reconnect", [oldKey]);
        var clear = new List<byte[]>();
        var stream = sealer.Relay.HeaderChunk([]).Concat(sealer.Relay.SealChunk(ExchangeUploadChunks.Joinable, [1, 2, 3])).ToArray();
        Assert.Equal(ExchangeSealedRelayStatus.EpochNotHeld, decoder.Feed(stream, clear));
        Assert.Empty(clear);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    public void ClearCredentialsAreIgnoredEvenWhenTheSealedMemberCannotBeRead(string box)
    {
        using var frame = JsonDocument.Parse($$$"""
            {"schemaVersion":2,"type":"directory","revision":1,
             "devices":[{"deviceId":"owner","deviceName":"Owner","presence":"online"}],
             "broadcasts":[{"broadcastId":"audit-pair","deviceId":"owner","mode":"AUDIO_ONLY",
               "credentials":{"user":"u","password":"clear-secret"},"sealed":{{{box}}},
               "descriptor":{"schemaVersion":1,"title":"t","mode":"AUDIO_ONLY","url":"http://192.168.1.2/live-audio.aac"}}]}
            """);
        var snapshot = new ExchangeDirectoryState().ApplyFull(frame.RootElement).Snapshot;

        var view = Assert.Single(Assert.Single(snapshot.Groups).Broadcasts);
        Assert.Null(view.Record.Credentials);
        Assert.True(view.Record.SealedUnreadable);
    }

    private static ExchangeDirectorySnapshot Directory(ExchangeSealedBox box, string grantId)
    {
        using var frame = JsonSerializer.SerializeToDocument(new
        {
            schemaVersion = 2, type = "directory", revision = 1,
            devices = new[] { new { deviceId = "owner-a", deviceName = "Owner", presence = "online" } },
            broadcasts = new[] { new
            {
                broadcastId = "audit-granted", deviceId = "owner-a", mode = "AUDIO_ONLY", grantId, @sealed = box,
                descriptor = new { schemaVersion = 1, title = "t", mode = "AUDIO_ONLY", url = "http://192.168.1.2/live-audio.aac" }
            } }
        }, ExchangeProtocol.JsonOptions);
        return new ExchangeDirectoryState().ApplyFull(frame.RootElement).Snapshot;
    }
}
