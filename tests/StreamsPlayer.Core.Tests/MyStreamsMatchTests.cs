using System;
using System.Collections.Generic;
using StreamsPlayer.Core;
using Xunit;

namespace StreamsPlayer.Core.Tests;

public sealed class MyStreamsMatchTests
{
    private static StreamChannel CreateChannel(
        string title,
        string? sourceId,
        string? directoryBroadcastId = null,
        string? directoryEntryIdentity = null)
    {
        return new StreamChannel
        {
            Id = Guid.NewGuid(),
            Url = "http://127.0.0.1:8080/stream",
            Title = title,
            MediaKind = MediaKind.Audio,
            SourceOrigin = SourceOrigin.Imported,
            AddedAt = DateTimeOffset.UtcNow,
            FastMediaSorterBroadcast = new FastMediaSorterBroadcastInfo
            {
                Mode = FastMediaSorterBroadcastDescriptor.AudioOnlyMode,
                SourceId = sourceId,
                DirectoryBroadcastId = directoryBroadcastId,
                DirectoryEntryIdentity = directoryEntryIdentity
            }
        };
    }

    private static ExchangeBroadcastView CreateBroadcast(
        string broadcastId,
        string? sourceId,
        string title = "Test Broadcast",
        string mode = FastMediaSorterBroadcastDescriptor.AudioOnlyMode)
    {
        var descriptor = sourceId is null ? null : new FastMediaSorterBroadcast(
            Url: "http://192.168.1.50:9000/live",
            Mode: mode,
            Title: title,
            SourceId: sourceId,
            IsLive: true,
            TargetLatencyMs: null,
            Endpoints: [new FastMediaSorterBroadcastEndpoint("http://192.168.1.50:9000/live", "http", mode, null, null, null, null, true, null, null, null)]);

        var record = new ExchangeBroadcastRecord(
            BroadcastId: broadcastId,
            DeviceId: "dev-phone-1",
            Mode: mode,
            Title: title,
            Descriptor: descriptor);

        return new ExchangeBroadcastView(record, ExchangeBroadcastSupport.Supported);
    }

    [Fact]
    public void MatchesSavedChannelBySourceId()
    {
        var channel = CreateChannel("My Phone Mic", "src-mic-123", directoryEntryIdentity: "server1:user1");
        var view = CreateBroadcast("bcast-456", "src-mic-123");

        var matched = MyStreamsMatch.FindMatchingSavedChannel([channel], view, "server1:user1");

        Assert.NotNull(matched);
        Assert.Equal(channel.Id, matched.Id);
    }

    [Fact]
    public void DoesNotMatchWhenAccountDiffers()
    {
        var channel = CreateChannel("Foreign Mic", "src-mic-123", directoryEntryIdentity: "server2:user2");
        var view = CreateBroadcast("bcast-456", "src-mic-123");

        var matched = MyStreamsMatch.FindMatchingSavedChannel([channel], view, "server1:user1");

        Assert.Null(matched);
    }

    [Fact]
    public void CollectsAnnouncedSavedChannelIds()
    {
        var channel1 = CreateChannel("Phone Mic", "src-mic-1", directoryEntryIdentity: "s1:u1");
        var channel2 = CreateChannel("PC Screen", "src-screen-2", directoryEntryIdentity: "s1:u1");
        var channel3 = CreateChannel("Other Saved", "src-other-3", directoryEntryIdentity: "s1:u1");

        var view1 = CreateBroadcast("b1", "src-mic-1");
        var view2 = CreateBroadcast("b2", "src-screen-2");

        var group = new ExchangeDeviceGroup("dev1", "Phone", true, [view1, view2]);
        var snapshot = new ExchangeDirectorySnapshot(1, [group], ["src-mic-1", "src-screen-2"], ["b1", "b2"]);

        var announced = MyStreamsMatch.CollectAnnouncedSavedChannelIds(snapshot, [channel1, channel2, channel3], "s1:u1");

        Assert.Equal(2, announced.Count);
        Assert.Contains(channel1.Id, announced);
        Assert.Contains(channel2.Id, announced);
        Assert.DoesNotContain(channel3.Id, announced);
    }

    [Fact]
    public void MatchesSearchByDeviceNameOrTitle()
    {
        var view = CreateBroadcast("b1", "src-1", title: "Living Room Camera", mode: FastMediaSorterBroadcastDescriptor.VideoAudioMode);

        Assert.True(MyStreamsMatch.MatchesSearch("Living Room PC", view, null, "pc"));
        Assert.True(MyStreamsMatch.MatchesSearch("Studio", view, null, "camera"));
        Assert.True(MyStreamsMatch.MatchesSearch("Studio", view, null, "video"));
        Assert.False(MyStreamsMatch.MatchesSearch("Studio", view, null, "kitchen"));
    }
}
