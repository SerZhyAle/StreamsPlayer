using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.Exchange.Tests;

public sealed class ExchangeResourcePinBoundaryTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("{\"scheme\":\"pin-v1\"}")]
    [InlineData("{\"scheme\":\"future\"}")]
    public void FolderAccessIsNeverImportedAsAnUnprotectedPlaybackChannel(string protection)
    {
        using var document = JsonDocument.Parse($$$"""
            {"schemaVersion":2,"type":"directory","revision":1,"devices":[],"broadcasts":[],
             "resources":[{"resourceId":"private","deviceId":"producer","kind":"sftp-share",
             "name":"Private","protection":{{{protection}}},"access":{"descriptor":"synthetic-secret"}}]}
            """);
        var state = new ExchangeDirectoryState();
        var snapshot = state.ApplyFull(document.RootElement).Snapshot;
        Assert.Empty(snapshot.Groups);
        Assert.Empty(snapshot.LiveSourceIds);
        Assert.Empty(snapshot.LiveBroadcastIds);
    }
}
