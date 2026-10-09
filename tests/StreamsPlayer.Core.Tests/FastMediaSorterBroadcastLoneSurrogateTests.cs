using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// Audit 26.1010.0106 A4: a string escape that is a lone surrogate (<c>"\ud800"</c>) is valid JSON text but
/// <c>JsonElement.GetString</c> throws <see cref="InvalidOperationException"/> on it. Foreign input must come
/// out as an invalid payload or a skipped record - never as an exception into a paste, drop or socket handler.
/// </summary>
public sealed class FastMediaSorterBroadcastLoneSurrogateTests
{
    private const string AudioDescriptor =
        """{"schemaVersion":1,"url":"http://192.168.1.97:8768/live-audio.aac","title":"Kitchen","mode":"AUDIO_ONLY","sourceId":"AAECAwQFBgcICQoLDA0ODw","isLive":true}""";

    [Theory]
    [InlineData("""{"schemaVersion":1,"url":"\ud800","mode":"AUDIO_ONLY"}""")]
    [InlineData("""{"schemaVersion":1,"url":"http://192.168.1.97:8768/live-audio.aac","mode":"\udc00"}""")]
    [InlineData("""{"schemaVersion":1,"url":"http://192.168.1.97:8768/live-audio.aac","mode":"AUDIO_ONLY","title":"x\ud800y"}""")]
    [InlineData("""{"schemaVersion":1,"url":"http://192.168.1.97:8768/live-audio.aac","mode":"AUDIO_ONLY","sourceId":"\udbff"}""")]
    [InlineData("""{"schemaVersion":1,"url":"http://192.168.1.97:8768/live-audio.aac","mode":"AUDIO_ONLY","endpoints":[{"url":"http://192.168.1.97:8768/live-audio.aac","transport":"\ud800"}]}""")]
    public void ALoneSurrogateEscapeReadsAsAnInvalidPayload(string json)
    {
        var read = FastMediaSorterBroadcastDescriptor.Read(json);

        Assert.Equal(FastMediaSorterBroadcastReadStatus.InvalidPayload, read.Status);
        Assert.Null(read.Broadcast);
    }

    [Fact]
    public void ALoneSurrogateEscapeInsideAnIntentLinkReadsAsAnInvalidPayload()
    {
        var payload = Uri.EscapeDataString("""{"schemaVersion":1,"url":"\ud800","mode":"AUDIO_ONLY"}""");

        var read = FastMediaSorterBroadcastDescriptor.Read($"fmsbcast://import?payload={payload}");

        Assert.Equal(FastMediaSorterBroadcastReadStatus.InvalidPayload, read.Status);
    }

    [Fact]
    public void ADirectoryRecordWithALoneSurrogateInItsDescriptorIsSkippedAndTheRestIsListed()
    {
        var json = $$$"""
        {"schemaVersion":2,"type":"directory","revision":3,
         "devices":[{"deviceId":"phone","deviceName":"Pixel 8","presence":"online"}],
         "broadcasts":[{"broadcastId":"bad","deviceId":"phone","title":"Bad","mode":"AUDIO_ONLY",
                        "descriptor":{"schemaVersion":1,"url":"\ud800","mode":"AUDIO_ONLY"}},
                       {"broadcastId":"good","deviceId":"phone","title":"Kitchen","mode":"AUDIO_ONLY","descriptor":{{{AudioDescriptor}}}}]}
        """;

        var snapshot = new ExchangeDirectoryState().ApplyFull(Parse(json)).Snapshot;

        var broadcasts = Assert.Single(snapshot.Groups).Broadcasts;
        Assert.Equal("good", Assert.Single(broadcasts).Record.BroadcastId);
    }

    [Fact]
    public void ALoneSurrogateInARecordMemberOrADeviceNameSkipsThatEntryOnly()
    {
        var json = $$$"""
        {"schemaVersion":2,"type":"directory","revision":3,
         "devices":[{"deviceId":"phone","deviceName":"Pixel 8","presence":"online"},
                    {"deviceId":"watch","deviceName":"W\ud800","presence":"online"}],
         "broadcasts":[{"broadcastId":"b\ud800","deviceId":"phone","title":"Bad id","mode":"AUDIO_ONLY","descriptor":{{{AudioDescriptor}}}},
                       {"broadcastId":"titled","deviceId":"phone","title":"T\udc00","mode":"AUDIO_ONLY","descriptor":{{{AudioDescriptor}}}}]}
        """;

        var snapshot = new ExchangeDirectoryState().ApplyFull(Parse(json)).Snapshot;

        var group = Assert.Single(snapshot.Groups);
        Assert.Equal("phone", group.DeviceId);
        var view = Assert.Single(group.Broadcasts);
        Assert.Equal("titled", view.Record.BroadcastId);
        // An unreadable title falls back to the descriptor's own, which is the one reader's answer.
        Assert.Equal("Kitchen", view.Record.Title);
    }

    [Fact]
    public void ALoneSurrogateInAChangedPushRemovalIsIgnored()
    {
        var state = new ExchangeDirectoryState();
        state.ApplyFull(Parse($$$"""
            {"schemaVersion":2,"type":"directory","revision":7,
             "devices":[{"deviceId":"phone","deviceName":"Pixel 8","presence":"online"}],
             "broadcasts":[{"broadcastId":"b1","deviceId":"phone","title":"Kitchen","mode":"AUDIO_ONLY","descriptor":{{{AudioDescriptor}}}}]}
            """));

        var transition = state.ApplyChange(Parse("""
            {"schemaVersion":2,"type":"changed","revision":8,"upserts":{},"removals":{"broadcastIds":["\ud800","b1"]}}
            """));

        Assert.False(transition.RelistNeeded);
        Assert.Empty(transition.Snapshot.Groups.SelectMany(group => group.Broadcasts));
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
