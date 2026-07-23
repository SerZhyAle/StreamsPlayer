using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class ListeningHistoryTests
{
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static ListeningHistoryEntry Entry(Guid id, int minute, string? track = null) => new()
    {
        ChannelId = id,
        Title = $"Channel {id:N}",
        MediaKind = MediaKind.Audio,
        LastPlayedAt = Base.AddMinutes(minute),
        LastTrackText = track
    };

    [Fact]
    public void CatalogState_ListeningHistory_DefaultsToEmpty()
    {
        Assert.Empty(new CatalogState().ListeningHistory);
    }

    [Fact]
    public void RecordPlay_NewChannel_PrependsAndLeavesInputUnchanged()
    {
        var existing = Guid.NewGuid();
        var input = new List<ListeningHistoryEntry> { Entry(existing, 0) };

        var result = ListeningHistory.RecordPlay(input, Guid.NewGuid(), "Fresh", MediaKind.Video, Base.AddMinutes(5));

        Assert.Equal(2, result.Count);
        Assert.Equal("Fresh", result[0].Title);
        Assert.Equal(MediaKind.Video, result[0].MediaKind);
        Assert.Equal(Base.AddMinutes(5), result[0].LastPlayedAt);
        // Input list is not mutated.
        Assert.Single(input);
        Assert.Equal(existing, input[0].ChannelId);
    }

    [Fact]
    public void RecordPlay_ExistingChannel_MovesToFrontWithoutDuplicatingAndCarriesTrackText()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var input = new List<ListeningHistoryEntry>
        {
            Entry(b, 2),
            Entry(a, 1, track: "Artist - Song")
        };

        var result = ListeningHistory.RecordPlay(input, a, "Channel A refreshed", MediaKind.Audio, Base.AddMinutes(10));

        Assert.Equal(2, result.Count); // no duplicate row
        Assert.Equal(a, result[0].ChannelId);
        Assert.Equal("Channel A refreshed", result[0].Title);
        Assert.Equal(Base.AddMinutes(10), result[0].LastPlayedAt);
        Assert.Equal("Artist - Song", result[0].LastTrackText); // carried over from the prior session
        Assert.Equal(b, result[1].ChannelId);
    }

    [Fact]
    public void RecordPlay_BeyondMax_EvictsOldestAndCapsCount()
    {
        var history = new List<ListeningHistoryEntry>();
        // Fill to exactly MaxEntries, oldest last.
        var firstId = Guid.Empty;
        for (var i = 0; i < ListeningHistory.MaxEntries; i++)
        {
            var id = Guid.NewGuid();
            if (i == 0)
            {
                firstId = id;
            }

            history = ListeningHistory.RecordPlay(history, id, $"C{i}", MediaKind.Audio, Base.AddMinutes(i));
        }

        Assert.Equal(ListeningHistory.MaxEntries, history.Count);
        var oldestId = history[^1].ChannelId; // least recently played

        var newId = Guid.NewGuid();
        var result = ListeningHistory.RecordPlay(history, newId, "Newest", MediaKind.Audio, Base.AddMinutes(1000));

        Assert.Equal(ListeningHistory.MaxEntries, result.Count); // still capped
        Assert.Equal(newId, result[0].ChannelId); // newest at front
        Assert.DoesNotContain(result, entry => entry.ChannelId == oldestId); // oldest evicted
        Assert.Equal(firstId, oldestId); // sanity: the first inserted was the one evicted
    }

    [Fact]
    public void RecordPlay_ProducesStrictMostRecentFirstOrder()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var history = new List<ListeningHistoryEntry>();
        for (var i = 0; i < ids.Length; i++)
        {
            history = ListeningHistory.RecordPlay(history, ids[i], $"C{i}", MediaKind.Audio, Base.AddMinutes(i));
        }

        Assert.Equal(new[] { ids[2], ids[1], ids[0] }, history.Select(entry => entry.ChannelId));
    }

    [Fact]
    public void UpdateTrackText_MatchingEntry_UpdatesTextInPlaceOnly()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var input = new List<ListeningHistoryEntry> { Entry(a, 2), Entry(b, 1) };

        var result = ListeningHistory.UpdateTrackText(input, b, "Now - Live");

        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal(a, result[0].ChannelId); // order unchanged
        Assert.Null(result[0].LastTrackText);
        Assert.Equal("Now - Live", result[1].LastTrackText);
    }

    [Fact]
    public void UpdateTrackText_UnknownChannel_ReturnsNull()
    {
        var input = new List<ListeningHistoryEntry> { Entry(Guid.NewGuid(), 1) };

        var result = ListeningHistory.UpdateTrackText(input, Guid.NewGuid(), "ignored");

        Assert.Null(result);
    }

    [Fact]
    public void UpdateTrackText_UnchangedText_ReturnsNull()
    {
        var id = Guid.NewGuid();
        var input = new List<ListeningHistoryEntry> { Entry(id, 1, track: "Same") };

        var result = ListeningHistory.UpdateTrackText(input, id, "Same");

        Assert.Null(result);
    }

    [Fact]
    public async Task Save_RoundTripsListeningHistory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"StreamsPlayer.Tests.{Guid.NewGuid():N}");
        try
        {
            var id = Guid.NewGuid();
            var store = new StreamCatalogStore(directory);
            await store.SaveAsync(new CatalogState
            {
                ListeningHistory = [Entry(id, 3, track: "Artist - Title")]
            });

            var loaded = await store.LoadAsync();

            var entry = Assert.Single(loaded.ListeningHistory);
            Assert.Equal(id, entry.ChannelId);
            Assert.Equal("Artist - Title", entry.LastTrackText);
            Assert.Equal(MediaKind.Audio, entry.MediaKind);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Load_LegacyStateWithoutHistoryField_YieldsEmptyList()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"StreamsPlayer.Tests.{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            // A pre-SP-0019 state file has no ListeningHistory member; deserialization must default it, not throw.
            await File.WriteAllTextAsync(
                Path.Combine(directory, "catalog-state.json"),
                "{ \"schemaVersion\": 1, \"channels\": [] }");
            var store = new StreamCatalogStore(directory);

            var loaded = await store.LoadAsync();

            Assert.NotNull(loaded.ListeningHistory);
            Assert.Empty(loaded.ListeningHistory);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
