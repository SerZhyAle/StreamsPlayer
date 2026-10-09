using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0201 acceptance: the account's directory records read into playable channels, the sourceId
/// replacement holds, a record that leaves marks and never deletes, a manual row is never the
/// directory's to touch, foreign records are skipped in silence, and a revision gap is answered
/// with a new list.
/// </summary>
public sealed class ExchangeDirectoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private const string AudioDescriptor =
        """{"schemaVersion":1,"url":"http://192.168.1.97:8768/live-audio.aac","title":"Kitchen","mode":"AUDIO_ONLY","sourceId":"AAECAwQFBgcICQoLDA0ODw","isLive":true,"targetLatencyMs":1000,"endpoints":[{"url":"http://192.168.1.97:8768/live-audio.aac","transport":"HTTP","mode":"AUDIO_ONLY","audioCodec":"AAC","sampleRate":44100,"isLive":true,"targetLatencyMs":1000},{"url":"https://exchange.example.net:44022/v2/b/ICEiIyQlJicoKSorLC0uLw/stream","transport":"RELAY","mode":"AUDIO_ONLY","targetLatencyMs":2000}]}""";

    [Theory]
    [InlineData("AUDIO_ONLY", "http://192.168.1.97:8768/live-audio.aac", "HTTP", MediaKind.Audio)]
    [InlineData("VIDEO_AUDIO", "rtsp://192.168.1.97:8554/", "RTSP", MediaKind.Rtsp)]
    [InlineData("VIDEO_ONLY", "rtsp://192.168.1.97:8554/", "RTSP", MediaKind.Rtsp)]
    public void ARecordInEveryModeReadsIntoAPlayableChannel(string mode, string url, string transport, MediaKind kind)
    {
        var state = Listed(Record(mode, url, transport));
        var view = Assert.Single(Assert.Single(state.Snapshot.Groups).Broadcasts);

        Assert.Equal(ExchangeBroadcastSupport.Supported, view.Support);
        var channel = FastMediaSorterBroadcastImport.Apply([], view.Record.Descriptor!, Now, view.Record.BroadcastId);

        Assert.True(channel.Added);
        Assert.Equal(kind, channel.Channel.MediaKind);
        Assert.Equal(url, channel.Channel.Url);
        Assert.Equal("b1", channel.Channel.FastMediaSorterBroadcast?.DirectoryBroadcastId);
        Assert.Null(channel.Channel.FastMediaSorterBroadcast?.DirectoryEndedAt);
    }

    [Fact]
    public void ASecondRecordWithTheSameSourceIdReplacesTheChannel()
    {
        var state = Listed(Record("AUDIO_ONLY", "http://192.168.1.97:8768/live-audio.aac", "HTTP"));
        var view = Assert.Single(Assert.Single(state.Snapshot.Groups).Broadcasts);
        var first = FastMediaSorterBroadcastImport.Apply([], view.Record.Descriptor!, Now, view.Record.BroadcastId).Channel;

        var replacementUrl = "http://192.168.1.97:9000/live-audio.aac";
        var again = FastMediaSorterBroadcastImport.Apply(
            [first],
            Descriptor(replacementUrl, first.FastMediaSorterBroadcast!.SourceId),
            Now,
            "new-broadcast-id");

        Assert.False(again.Added);
        var replaced = Assert.Single(again.Channels);
        Assert.Equal(first.Id, replaced.Id);
        Assert.Equal(replacementUrl, replaced.Url);
        Assert.Equal("new-broadcast-id", replaced.FastMediaSorterBroadcast?.DirectoryBroadcastId);
    }

    [Fact]
    public void ARemovalMarksEndedAndNeverDeletes()
    {
        var state = Listed(Record("AUDIO_ONLY", "http://192.168.1.97:8768/live-audio.aac", "HTTP"));
        var view = Assert.Single(Assert.Single(state.Snapshot.Groups).Broadcasts);
        var kept = FastMediaSorterBroadcastImport.Apply([], view.Record.Descriptor!, Now, view.Record.BroadcastId).Channel;
        kept = kept with { Pinned = true, Title = "Kitchen radio" };

        // The record left the directory: the next list names a different broadcast only.
        var after = Listed(Record("AUDIO_ONLY", "http://192.168.1.5:1234/live-audio.aac", "HTTP",
            sourceId: "another-source", broadcastId: "b9"));
        var marked = ExchangeDirectoryChannels.MarkEnded(
            [kept], after.Snapshot.LiveSourceIds, after.Snapshot.LiveBroadcastIds, Now);

        var row = Assert.Single(marked);
        Assert.Equal(kept.Id, row.Id);
        Assert.Equal("Kitchen radio", row.Title);
        Assert.True(row.Pinned);
        Assert.NotNull(row.FastMediaSorterBroadcast?.DirectoryEndedAt);
    }

    [Fact]
    public void AManualRowWithTheSameAddressIsUntouched()
    {
        var manual = Channel("http://192.168.1.97:8768/live-audio.aac");
        var state = Listed(Record("AUDIO_ONLY", "http://192.168.1.5:1234/live-audio.aac", "HTTP"));

        var marked = ExchangeDirectoryChannels.MarkEnded(
            [manual], state.Snapshot.LiveSourceIds, state.Snapshot.LiveBroadcastIds, Now);
        Assert.Empty(marked);

        // The push refresh is equally bound: with no sourceId and no stored row of its own, it adds nothing.
        var refreshed = ExchangeDirectoryChannels.RefreshStored(
            [manual], state.Snapshot.Groups.SelectMany(group => group.Broadcasts), Now);
        Assert.Equal(manual, Assert.Single(refreshed));
    }

    [Fact]
    public void AManualRowWithTheSameSourceIdIsReplacedButNeverMarkedEnded()
    {
        var state = Listed(Record("AUDIO_ONLY", "http://192.168.1.97:8768/live-audio.aac", "HTTP"));
        var view = Assert.Single(Assert.Single(state.Snapshot.Groups).Broadcasts);
        var descriptor = view.Record.Descriptor!;

        // A link the user pasted created the row first; the directory's same sourceId only replaces it.
        var manual = FastMediaSorterBroadcastImport.Apply(
            [], descriptor with { Title = "My own name" }, Now).Channel;
        Assert.Null(manual.FastMediaSorterBroadcast?.DirectoryBroadcastId);

        var replaced = FastMediaSorterBroadcastImport.Apply(
            [manual], descriptor, Now, view.Record.BroadcastId).Channel;
        Assert.Null(replaced.FastMediaSorterBroadcast?.DirectoryBroadcastId);

        // The record then leaves: a row the directory did not create is never marked ended.
        Assert.Empty(ExchangeDirectoryChannels.MarkEnded([replaced], [], [], Now));
    }

    [Fact]
    public void ARevivingRecordWithTheSameSourceIdClearsTheEndedMark()
    {
        var state = Listed(Record("AUDIO_ONLY", "http://192.168.1.97:8768/live-audio.aac", "HTTP"));
        var view = Assert.Single(Assert.Single(state.Snapshot.Groups).Broadcasts);
        var kept = FastMediaSorterBroadcastImport.Apply([], view.Record.Descriptor!, Now, view.Record.BroadcastId).Channel;
        var ended = Assert.Single(ExchangeDirectoryChannels.MarkEnded([kept], [], [], Now));

        var revived = FastMediaSorterBroadcastImport.Apply(
            [ended], view.Record.Descriptor!, Now, "a-new-broadcast-id").Channel;

        Assert.Equal(kept.Id, revived.Id);
        Assert.Null(revived.FastMediaSorterBroadcast?.DirectoryEndedAt);
        Assert.Equal("a-new-broadcast-id", revived.FastMediaSorterBroadcast?.DirectoryBroadcastId);
    }

    [Fact]
    public void AnUnknownKindAndAnOversizeRecordAreSkippedWithNoException()
    {
        var oversize = new string('x', ExchangeDirectoryState.MaximumRecordBytes);
        var json = $$"""
        {"schemaVersion":2,"type":"directory","revision":7,
         "devices":[{"deviceId":"phone","deviceName":"Pixel 8","presence":"online"}],
         "resources":[{"resourceId":"r1","deviceId":"phone","kind":"webdav-share","name":"Disk"},
                      {"resourceId":"r2","deviceId":"phone","kind":"sftp-share","name":"Files"}],
         "broadcasts":[{"broadcastId":"b1","deviceId":"phone","title":"Big","mode":"AUDIO_ONLY","padding":"{{oversize}}",
                        "descriptor":{{AudioDescriptor}}},
                       {"broadcastId":"b2","deviceId":"phone","title":"Kitchen","mode":"AUDIO_ONLY","descriptor":{{AudioDescriptor}}}]}
        """;

        var transition = new ExchangeDirectoryState().ApplyFull(Parse(json));

        Assert.False(transition.RelistNeeded);
        var broadcasts = Assert.Single(transition.Snapshot.Groups).Broadcasts;
        Assert.Equal("b2", Assert.Single(broadcasts).Record.BroadcastId);
    }

    [Fact]
    public void AHigherDescriptorSchemaIsListedAsUpdateTheApplication()
    {
        var json = """
        {"schemaVersion":2,"type":"directory","revision":3,
         "devices":[{"deviceId":"phone","deviceName":"Pixel 8","presence":"online"}],
         "broadcasts":[{"broadcastId":"b9","deviceId":"phone","title":"Future","mode":"AUDIO_ONLY",
                       "descriptor":{"schemaVersion":2,"url":"http://192.168.1.97:8768/live-audio.aac","mode":"AUDIO_ONLY"}}]}
        """;

        var view = Assert.Single(Assert.Single(new ExchangeDirectoryState().ApplyFull(Parse(json)).Snapshot.Groups).Broadcasts);

        Assert.Equal(ExchangeBroadcastSupport.UnsupportedSchema, view.Support);
        Assert.Null(view.Record.Descriptor);
    }

    /// <summary>
    /// Audit 26.1010.0106 A6: this pinned the relay as unsupported, the opposite of what playback does
    /// (SP-0203 plays RELAY endpoints, and the capability list advertises them). Listing, push refresh
    /// and cast share the playback rule now; only a descriptor none of whose declared endpoints this build
    /// plays is unsupported.
    /// </summary>
    [Fact]
    public void ARelayOnlyBroadcastIsListedAsSupportedAndRefreshesAStoredRow()
    {
        var json = """
        {"schemaVersion":2,"type":"directory","revision":3,
         "devices":[{"deviceId":"phone","deviceName":"Pixel 8","presence":"online"}],
         "broadcasts":[{"broadcastId":"b3","deviceId":"phone","title":"Away","mode":"AUDIO_ONLY",
                       "descriptor":{"schemaVersion":1,"url":"https://exchange.example.net:44022/v2/b/b3/stream","mode":"AUDIO_ONLY","isLive":true,
                                     "endpoints":[{"url":"https://exchange.example.net:44022/v2/b/b3/stream","transport":"RELAY","mode":"AUDIO_ONLY"}]}}]}
        """;

        var view = Assert.Single(Assert.Single(new ExchangeDirectoryState().ApplyFull(Parse(json)).Snapshot.Groups).Broadcasts);

        Assert.Equal(ExchangeBroadcastSupport.Supported, view.Support);
        Assert.Equal("b3", view.Record.BroadcastId);

        // The push refresh follows the same verdict: a stored row of this sourceId moves to the relay.
        var stored = Channel("http://192.168.1.97:8768/live-audio.aac") with
        {
            SourceOrigin = SourceOrigin.Imported,
            FastMediaSorterBroadcast = new FastMediaSorterBroadcastInfo
            {
                SourceId = "relay-source",
                Mode = FastMediaSorterBroadcastDescriptor.AudioOnlyMode,
                DirectoryBroadcastId = "b3"
            }
        };
        var relayView = view with
        {
            Record = view.Record with { Descriptor = view.Record.Descriptor! with { SourceId = "relay-source" } }
        };
        var refreshed = Assert.Single(ExchangeDirectoryChannels.RefreshStored([stored], [relayView], Now));
        Assert.Equal("https://exchange.example.net:44022/v2/b/b3/stream", refreshed.Url);
    }

    [Fact]
    public void ALanHttpVideoBroadcastIsListedAsSupported()
    {
        var json = """
        {"schemaVersion":2,"type":"directory","revision":3,
         "devices":[{"deviceId":"phone","deviceName":"Pixel 8","presence":"online"}],
         "broadcasts":[{"broadcastId":"b5","deviceId":"phone","title":"Cam","mode":"VIDEO_AUDIO",
                       "descriptor":{"schemaVersion":1,"url":"http://192.168.1.97:8080/live.ts","mode":"VIDEO_AUDIO","isLive":true,
                                     "endpoints":[{"url":"http://192.168.1.97:8080/live.ts","transport":"HTTP","mode":"VIDEO_AUDIO"}]}}]}
        """;

        var view = Assert.Single(Assert.Single(new ExchangeDirectoryState().ApplyFull(Parse(json)).Snapshot.Groups).Broadcasts);

        Assert.Equal(ExchangeBroadcastSupport.Supported, view.Support);
    }

    [Fact]
    public void ABroadcastWhoseEveryEndpointIsAReservedTransportIsListedAsUnsupported()
    {
        var json = """
        {"schemaVersion":2,"type":"directory","revision":3,
         "devices":[{"deviceId":"phone","deviceName":"Pixel 8","presence":"online"}],
         "broadcasts":[{"broadcastId":"b6","deviceId":"phone","title":"Direct","mode":"AUDIO_ONLY",
                       "descriptor":{"schemaVersion":1,"url":"http://192.168.1.97:8768/live-audio.aac","mode":"AUDIO_ONLY","isLive":true,
                                     "endpoints":[{"url":"http://192.168.1.97:8768/p2p","transport":"P2P","mode":"AUDIO_ONLY"}]}}]}
        """;

        var view = Assert.Single(Assert.Single(new ExchangeDirectoryState().ApplyFull(Parse(json)).Snapshot.Groups).Broadcasts);

        Assert.Equal(ExchangeBroadcastSupport.Unsupported, view.Support);
        Assert.Equal("b6", view.Record.BroadcastId);
    }

    [Fact]
    public void ATunnelBroadcastIsListedAsSupported()
    {
        var json = """
        {"schemaVersion":2,"type":"directory","revision":3,
         "devices":[{"deviceId":"phone","deviceName":"Pixel 8","presence":"online"}],
         "broadcasts":[{"broadcastId":"b4","deviceId":"phone","title":"Mobile","mode":"AUDIO_ONLY",
                       "descriptor":{"schemaVersion":1,"url":"http://192.168.1.97:8768/live-audio.aac","mode":"AUDIO_ONLY","isLive":true,
                                     "endpoints":[{"url":"fmsx://exchange.example.net:44022/b/b4/http","transport":"TUNNEL","inner":"http://192.168.1.97:8768/live-audio.aac","mode":"AUDIO_ONLY"}]}}]}
        """;

        var view = Assert.Single(Assert.Single(new ExchangeDirectoryState().ApplyFull(Parse(json)).Snapshot.Groups).Broadcasts);

        Assert.Equal(ExchangeBroadcastSupport.Supported, view.Support);
        Assert.Equal("b4", view.Record.BroadcastId);
    }

    [Fact]
    public void TheOwnDeviceIsHiddenAndPresenceFollowsTheDeviceRecord()
    {
        var json = $$"""
        {"schemaVersion":2,"type":"directory","revision":3,
         "devices":[{"deviceId":"phone","deviceName":"Pixel 8","presence":"online"},
                    {"deviceId":"watch","deviceName":"Watch","presence":"offline"}],
         "broadcasts":[{"broadcastId":"b1","deviceId":"phone","title":"Kitchen","mode":"AUDIO_ONLY","descriptor":{{AudioDescriptor}}},
                       {"broadcastId":"b2","deviceId":"watch","title":"Walk","mode":"AUDIO_ONLY","descriptor":{{AudioDescriptor}}}]}
        """;

        var snapshot = new ExchangeDirectoryState().ApplyFull(Parse(json)).Snapshot;
        var visible = ExchangeDirectoryState.HideOwnDevice(snapshot, "phone");

        var group = Assert.Single(visible.Groups);
        Assert.Equal("watch", group.DeviceId);
        Assert.Equal("Watch", group.DeviceName);
        Assert.False(group.IsOnline);
        Assert.Equal("b2", Assert.Single(group.Broadcasts).Record.BroadcastId);
    }

    [Fact]
    public void ARevisionGapAsksForANewListAndLeavesTheStateUntouched()
    {
        var state = Listed(Record("AUDIO_ONLY", "http://192.168.1.97:8768/live-audio.aac", "HTTP"));
        var before = state.Snapshot;

        var gap = state.ApplyChange(Parse("""
            {"schemaVersion":2,"type":"changed","revision":9,
             "upserts":{"broadcasts":[{"broadcastId":"b2","deviceId":"phone","title":"New","mode":"AUDIO_ONLY","descriptor":{"schemaVersion":1,"url":"http://192.168.1.5:1/live-audio.aac","mode":"AUDIO_ONLY"}}]}}
            """));

        Assert.True(gap.RelistNeeded);
        Assert.Equal(before, state.Snapshot);
    }

    [Fact]
    public void AStaleRepeatOfTheSameRevisionAsksForANewListToo()
    {
        var state = Listed(Record("AUDIO_ONLY", "http://192.168.1.97:8768/live-audio.aac", "HTTP"));

        var stale = state.ApplyChange(Parse($$$"""
            {"schemaVersion":2,"type":"changed","revision":{{{state.Snapshot.Revision}}},
             "upserts":{},"removals":{"broadcastIds":["b1"]}}
            """));

        Assert.True(stale.RelistNeeded);
    }

    [Fact]
    public void AConsecutiveChangeUpsertsRemovesAndKeepsGroups()
    {
        var state = Listed(Record("AUDIO_ONLY", "http://192.168.1.97:8768/live-audio.aac", "HTTP"));
        Assert.Equal(7, state.Snapshot.Revision);

        var removal = state.ApplyChange(Parse("""
            {"schemaVersion":2,"type":"changed","revision":8,"upserts":{},"removals":{"broadcastIds":["b1"]}}
            """));

        Assert.False(removal.RelistNeeded);
        Assert.Empty(removal.Snapshot.Groups.SelectMany(group => group.Broadcasts));
        Assert.Empty(removal.Snapshot.LiveBroadcastIds);

        var upsert = state.ApplyChange(Parse($$$"""
            {"schemaVersion":2,"type":"changed","revision":9,
              "upserts":{"devices":[{"deviceId":"watch","deviceName":"Watch","presence":"online"}],
                          "broadcasts":[{"broadcastId":"b2","deviceId":"watch","title":"Walk","mode":"AUDIO_ONLY","descriptor":{{{AudioDescriptor}}}}]}}
            """));

        Assert.False(upsert.RelistNeeded);
        var group = Assert.Single(upsert.Snapshot.Groups);
        Assert.Equal("watch", group.DeviceId);
        Assert.True(group.IsOnline);
        Assert.Equal("b2", Assert.Single(group.Broadcasts).Record.BroadcastId);
    }

    [Fact]
    public void ADeviceRemovalTakesItsBroadcastsWithIt()
    {
        var state = Listed(Record("AUDIO_ONLY", "http://192.168.1.97:8768/live-audio.aac", "HTTP"));

        var transition = state.ApplyChange(Parse("""
            {"schemaVersion":2,"type":"changed","revision":8,"upserts":{},"removals":{"deviceIds":["phone"]}}
            """));

        Assert.False(transition.RelistNeeded);
        Assert.Empty(transition.Snapshot.Groups);
    }

    [Fact]
    public void ADirectoryPushRefreshesAStoredChannelButAddsNothing()
    {
        var state = Listed(Record("AUDIO_ONLY", "http://192.168.1.97:8768/live-audio.aac", "HTTP"));
        var view = Assert.Single(Assert.Single(state.Snapshot.Groups).Broadcasts);
        var stored = FastMediaSorterBroadcastImport.Apply([], view.Record.Descriptor!, Now, view.Record.BroadcastId).Channel;
        stored = stored with { Title = "Renamed by the user", Pinned = true };

        var moved = Descriptor("http://192.168.1.97:9000/live-audio.aac", stored.FastMediaSorterBroadcast!.SourceId)
            with { Title = "Producer's new title" };
        var refreshed = ExchangeDirectoryChannels.RefreshStored(
            [stored],
            [view with { Record = view.Record with { Descriptor = moved } }],
            Now);

        var row = Assert.Single(refreshed);
        Assert.Equal(stored.Id, row.Id);
        Assert.Equal("http://192.168.1.97:9000/live-audio.aac", row.Url);
        // The user's own words survive a push (requirement 4), and the push never added a row.
        Assert.Equal("Renamed by the user", row.Title);
    }

    [Fact]
    public void OfTwoRecordsOneSourceIdKeepsTheLaterUpdatedAt()
    {
        // LIVE-BROADCAST item G: the phone re-broadcast the same source from a new address while the
        // older record was still listed; the later record is the one the kept channel follows.
        var older = Listed(Record("AUDIO_ONLY", "http://192.168.1.97:8768/live-audio.aac", "HTTP",
            broadcastId: "b-old", updatedAt: "2026-10-07T12:00:00.000Z"));
        var newer = Listed(Record("AUDIO_ONLY", "http://192.168.1.97:9000/live-audio.aac", "HTTP",
            broadcastId: "b-new", updatedAt: "2026-10-07T12:05:00.000Z"));
        var stored = FastMediaSorterBroadcastImport.Apply(
            [],
            older.Snapshot.Groups.Single().Broadcasts.Single().Record.Descriptor!,
            Now, "b-old").Channel;

        // The newer record arrives first in the frame; the later updatedAt decides, not the order.
        var pushed = ExchangeDirectoryChannels.RefreshStored(
            [stored],
            newer.Snapshot.Groups.SelectMany(group => group.Broadcasts)
                .Concat(older.Snapshot.Groups.SelectMany(group => group.Broadcasts)),
            Now);

        var row = Assert.Single(pushed);
        Assert.Equal("http://192.168.1.97:9000/live-audio.aac", row.Url);
        Assert.Equal("b-new", row.FastMediaSorterBroadcast?.DirectoryBroadcastId);
    }

    [Fact]
    public void CapabilityAddressesAreRecognizedButALanAddressIsNotOne()
    {
        Assert.True(BroadcastCapabilityAddress.CarriesCapability(
            "https://exchange.example.net:44022/v2/b/ICEiIyQlJicoKSorLC0uLw/stream"));
        Assert.True(BroadcastCapabilityAddress.CarriesCapability(
            "fmsx://exchange.example.net:44022/b/ICEiIyQlJicoKSorLC0uLw/http"));
        Assert.False(BroadcastCapabilityAddress.CarriesCapability(
            "http://192.168.1.97:8768/live-audio.aac"));
        Assert.False(BroadcastCapabilityAddress.CarriesCapability("not a url"));
    }

    private static ExchangeDirectoryState Listed(string broadcastJson)
    {
        var state = new ExchangeDirectoryState();
        var transition = state.ApplyFull(Parse($$"""
            {"schemaVersion":2,"type":"directory","revision":7,
             "devices":[{"deviceId":"phone","deviceName":"Pixel 8","presence":"online"}],
             "broadcasts":[{{broadcastJson}}]}
            """));
        Assert.False(transition.RelistNeeded);
        return state;
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>The section 6.3 record with one LAN endpoint of the given mode's transport.</summary>
    private static string Record(
        string mode,
        string url,
        string transport,
        string? sourceId = "AAECAwQFBgcICQoLDA0ODw",
        string broadcastId = "b1",
        string? updatedAt = null) => $$$"""
        {"broadcastId":"{{{broadcastId}}}","deviceId":"phone","title":"Kitchen","mode":"{{{mode}}}","updatedAt":"{{{updatedAt}}}",
         "descriptor":{"schemaVersion":1,"url":"{{{url}}}","title":"Kitchen","mode":"{{{mode}}}","sourceId":"{{{sourceId}}}","isLive":true,
                       "endpoints":[{"url":"{{{url}}}","transport":"{{{transport}}}","mode":"{{{mode}}}","isLive":true}]}}
        """;

    private static FastMediaSorterBroadcast Descriptor(string url, string? sourceId) =>
        new(url, FastMediaSorterBroadcastDescriptor.AudioOnlyMode, "Kitchen", sourceId, true, 1000,
        [
            new(url, "HTTP", "AUDIO_ONLY", null, "AAC", 44100, 128000, true, 1000)
        ]);

    private static StreamChannel Channel(string url) => new()
    {
        Id = Guid.NewGuid(),
        Url = url,
        Title = "Original",
        MediaKind = MediaKind.Audio,
        SourceOrigin = SourceOrigin.Manual,
        SortIndex = 0,
        AddedAt = Now
    };
}
