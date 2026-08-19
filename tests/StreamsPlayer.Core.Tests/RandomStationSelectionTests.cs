using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class RandomStationSelectionTests
{
    [Fact]
    public void Eligible_KeepsAudioAndDropsVideoAndRtsp()
    {
        var channels = new[]
        {
            Channel("https://example.com/radio", MediaKind.Audio),
            Channel("https://example.com/tv.m3u8", MediaKind.Video),
            Channel("rtsp://example.com/cam", MediaKind.Rtsp)
        };

        var eligible = RandomStationSelection.Eligible(channels, []);

        Assert.Equal(["https://example.com/radio"], eligible.Select(channel => channel.Url));
    }

    [Fact]
    public void Eligible_DropsAHiddenCatalogRowByNormalizedIdentity()
    {
        var channels = new[] { Channel("https://Example.COM/radio", MediaKind.Audio) };

        var eligible = RandomStationSelection.Eligible(channels, ["https://example.com/radio"]);

        Assert.Empty(eligible);
    }

    [Fact]
    public void Eligible_KeepsAUserOwnedRowThatSharesAHiddenUrl()
    {
        // Hiding is catalog-only. A Manual row is the user's own record, and a refresh-driven hide of the
        // same address must never make it disappear from anything - including this draw.
        var channels = new[]
        {
            Channel("https://example.com/radio", MediaKind.Audio, SourceOrigin.Manual),
            Channel("https://example.com/radio", MediaKind.Audio, SourceOrigin.Imported)
        };

        var eligible = RandomStationSelection.Eligible(channels, ["https://example.com/radio"]);

        Assert.Equal(2, eligible.Count);
    }

    [Fact]
    public void Eligible_DropsAnUnlaunchableUrl()
    {
        var channels = new[]
        {
            Channel("file:///C:/music/local.mp3", MediaKind.Audio),
            Channel("not a url", MediaKind.Audio),
            Channel("https://example.com/radio", MediaKind.Audio)
        };

        var eligible = RandomStationSelection.Eligible(channels, []);

        Assert.Equal(["https://example.com/radio"], eligible.Select(channel => channel.Url));
    }

    [Fact]
    public void Eligible_WithNothingHiddenKeepsEveryAudioRowInOrder()
    {
        var channels = Enumerable.Range(0, 5)
            .Select(index => Channel($"https://example.com/{index}", MediaKind.Audio))
            .ToArray();

        var eligible = RandomStationSelection.Eligible(channels, []);

        Assert.Equal(channels.Select(channel => channel.Url), eligible.Select(channel => channel.Url));
    }

    [Fact]
    public void Draw_OnAnEmptySetReturnsNull()
    {
        Assert.Null(RandomStationSelection.Draw([], new Random(1)));
    }

    [Fact]
    public void Draw_CanReachEveryIndexIncludingTheLast()
    {
        // Guards the off-by-one that silently excludes one end of the catalog: a station the command can
        // never offer is indistinguishable from a missing one, and nobody would notice among 20 000.
        var eligible = Enumerable.Range(0, 50)
            .Select(index => Channel($"https://example.com/{index}", MediaKind.Audio))
            .ToList();
        var random = new Random(20860810);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var roll = 0; roll < 5_000 && seen.Count < eligible.Count; roll++)
        {
            seen.Add(RandomStationSelection.Draw(eligible, random)!.Url);
        }

        Assert.Equal(eligible.Count, seen.Count);
    }

    [Fact]
    public void Draw_ReturnsAMemberOfTheSetItWasGiven()
    {
        var eligible = new List<StreamChannel> { Channel("https://example.com/only", MediaKind.Audio) };

        Assert.Same(eligible[0], RandomStationSelection.Draw(eligible, new Random(7)));
    }

    private static StreamChannel Channel(string url, MediaKind kind, SourceOrigin origin = SourceOrigin.Catalog) => new()
    {
        Id = Guid.NewGuid(),
        Url = url,
        Title = url,
        MediaKind = kind,
        SourceOrigin = origin,
        AddedAt = DateTimeOffset.UnixEpoch
    };
}
