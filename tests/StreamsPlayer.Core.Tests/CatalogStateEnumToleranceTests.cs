using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0035. SP-0034 made one field survive an unknown value; every other enum in the persisted state
/// still aborted the whole document, which cost the user their catalog, collections, history and
/// preferences the moment a newer build wrote a value this one does not know. Each test here feeds one
/// unreadable enum and asserts the same two things: that field falls back, and everything else lives.
/// </summary>
public sealed class CatalogStateEnumToleranceTests
{
    private static async Task WithStoreAsync(Func<string, StreamCatalogStore, Task> body)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"StreamsPlayer.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await body(Path.Combine(directory, "catalog-state.json"), new StreamCatalogStore(directory));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>
    /// A state file carrying real user data, with the state-level and channel-level enum properties
    /// under test spliced in.
    /// </summary>
    private static string StateJson(string stateProperties, string channelProperties) => $$"""
        {
          "schemaVersion": 1,
          {{stateProperties}}
          "channels": [
            {
              "id": "11111111-1111-1111-1111-111111111111",
              "title": "Kept Manual Row",
              "url": "http://example.test/manual",
              {{channelProperties}}
              "addedAt": "2026-07-27T00:00:00+00:00"
            }
          ],
          "collections": [
            { "id": "22222222-2222-2222-2222-222222222222", "name": "Favourites", "channelIds": [] }
          ],
          "listeningHistory": [
            {
              "channelId": "11111111-1111-1111-1111-111111111111",
              "title": "Kept Manual Row",
              "mediaKind": "Audio",
              "lastPlayedAt": "2026-07-27T01:00:00+00:00"
            }
          ],
          "hiddenCatalogUrls": [ "http://example.test/hidden" ],
          "mainWindowTopmost": true,
          "audioVolume": 42
        }
        """;

    /// <summary>The channel's two required enums, present whenever a test is not replacing them itself.</summary>
    private const string RequiredChannelEnums = "\"mediaKind\": \"Audio\", \"sourceOrigin\": \"Manual\",";

    private static async Task<CatalogState> LoadWithAsync(string stateProperties, string? channelProperties = null)
    {
        channelProperties ??= RequiredChannelEnums;
        CatalogState? loaded = null;
        await WithStoreAsync(async (path, store) =>
        {
            await File.WriteAllTextAsync(path, StateJson(stateProperties, channelProperties));
            loaded = await store.LoadAsync();
        });

        return loaded!;
    }

    private static void AssertUserDataSurvived(CatalogState state)
    {
        var channel = Assert.Single(state.Channels);
        Assert.Equal("Kept Manual Row", channel.Title);
        Assert.Equal("Favourites", Assert.Single(state.Collections).Name);
        Assert.Equal("http://example.test/hidden", Assert.Single(state.HiddenCatalogUrls));
        Assert.Single(state.ListeningHistory);
        Assert.True(state.MainWindowTopmost);
        Assert.Equal(42, state.AudioVolume);
    }

    /// <summary>Every unreadable shape one enum property can take, reused across the fields.</summary>
    public static TheoryData<string> UnreadableValues =>
    [
        "\"ShippedByANewerBuild\"",
        "\"\"",
        "99",
        "-1",
        "null",
        "{ \"value\": \"Grid\" }",
        "[ \"Grid\" ]"
    ];

    [Theory]
    [MemberData(nameof(UnreadableValues))]
    public async Task Load_UnreadableViewModeFallsBackToList(string value)
    {
        var state = await LoadWithAsync($"\"viewMode\": {value},");

        Assert.Equal(CatalogViewMode.List, state.ViewMode);
        AssertUserDataSurvived(state);
    }

    [Theory]
    [MemberData(nameof(UnreadableValues))]
    public async Task Load_UnreadableTileSizeFallsBackToMedium(string value)
    {
        var state = await LoadWithAsync($"\"tileSize\": {value},");

        Assert.Equal(StreamTileSize.Medium, state.TileSize);
        AssertUserDataSurvived(state);
    }

    [Theory]
    [MemberData(nameof(UnreadableValues))]
    public async Task Load_UnreadableVideoBackendFallsBackToLibVlc(string value)
    {
        var state = await LoadWithAsync($"\"videoBackend\": {value},");

        Assert.Equal(MediaBackend.LibVlc, state.VideoBackend);
        AssertUserDataSurvived(state);
    }

    [Theory]
    [MemberData(nameof(UnreadableValues))]
    public async Task Load_UnreadableChannelAccessFallsBackToOpen(string value)
    {
        var state = await LoadWithAsync(
            string.Empty,
            $"\"mediaKind\": \"Audio\", \"sourceOrigin\": \"Manual\", \"access\": {value},");

        Assert.Equal(ChannelAccess.Open, Assert.Single(state.Channels).Access);
        AssertUserDataSurvived(state);
    }

    /// <summary>
    /// The fallback that protects data rather than restoring a default: a row whose origin cannot be
    /// read must not be treated as a catalog row, because the next refresh rewrites and prunes those.
    /// </summary>
    [Theory]
    [MemberData(nameof(UnreadableValues))]
    public async Task Load_UnreadableSourceOriginFallsBackToTheOriginARefreshNeverTouches(string value)
    {
        var state = await LoadWithAsync(
            string.Empty,
            $"\"mediaKind\": \"Audio\", \"sourceOrigin\": {value},");

        Assert.Equal(SourceOrigin.Manual, Assert.Single(state.Channels).SourceOrigin);
        AssertUserDataSurvived(state);
    }

    [Theory]
    [MemberData(nameof(UnreadableValues))]
    public async Task Load_UnreadableMediaKindFallsBackToVideo(string value)
    {
        var state = await LoadWithAsync(
            string.Empty,
            $"\"sourceOrigin\": \"Manual\", \"mediaKind\": {value},");

        Assert.Equal(MediaKind.Video, Assert.Single(state.Channels).MediaKind);
        AssertUserDataSurvived(state);
    }

    /// <summary>An optional enum has an honest representation for "unreadable": absent.</summary>
    [Theory]
    [MemberData(nameof(UnreadableValues))]
    public async Task Load_UnreadablePlayOutcomeIsNoOutcomeAtAll(string value)
    {
        var state = await LoadWithAsync(
            string.Empty,
            $"\"mediaKind\": \"Audio\", \"sourceOrigin\": \"Manual\", \"lastPlayOutcome\": {value},");

        Assert.Null(Assert.Single(state.Channels).LastPlayOutcome);
        AssertUserDataSurvived(state);
    }

    /// <summary>One unreadable field must not disturb the enums beside it.</summary>
    [Fact]
    public async Task Load_KeepsEveryReadableEnumWhenOneIsUnreadable()
    {
        var state = await LoadWithAsync(
            "\"viewMode\": \"Grid\", \"tileSize\": \"Nonsense\", \"videoBackend\": \"Flyleaf\",",
            "\"mediaKind\": \"Rtsp\", \"sourceOrigin\": \"Imported\", \"access\": \"GeoRestricted\",");

        Assert.Equal(CatalogViewMode.Grid, state.ViewMode);
        Assert.Equal(StreamTileSize.Medium, state.TileSize);
        Assert.Equal(MediaBackend.Flyleaf, state.VideoBackend);
        var channel = Assert.Single(state.Channels);
        Assert.Equal(MediaKind.Rtsp, channel.MediaKind);
        Assert.Equal(SourceOrigin.Imported, channel.SourceOrigin);
        Assert.Equal(ChannelAccess.GeoRestricted, channel.Access);
        AssertUserDataSurvived(state);
    }

    /// <summary>A missing property is the ordinary older-file case and must keep the type's own default.</summary>
    [Fact]
    public async Task Load_MissingEnumPropertiesKeepTheirDefaults()
    {
        var state = await LoadWithAsync(string.Empty, "\"mediaKind\": \"Audio\", \"sourceOrigin\": \"Manual\",");

        Assert.Equal(CatalogViewMode.List, state.ViewMode);
        Assert.Equal(StreamTileSize.Medium, state.TileSize);
        Assert.Equal(MediaBackend.LibVlc, state.VideoBackend);
        Assert.Equal(ChannelAccess.Open, Assert.Single(state.Channels).Access);
        AssertUserDataSurvived(state);
    }

    /// <summary>
    /// The persisted tokens are a compatibility contract with older builds, so tolerance must not have
    /// changed how a readable value is written back.
    /// </summary>
    [Fact]
    public async Task Save_StillWritesEveryEnumByName()
    {
        await WithStoreAsync(async (path, store) =>
        {
            await store.SaveAsync(new CatalogState
            {
                ViewMode = CatalogViewMode.Grid,
                TileSize = StreamTileSize.Large,
                VideoBackend = MediaBackend.Flyleaf,
                Channels =
                [
                    new StreamChannel
                    {
                        Id = Guid.NewGuid(),
                        Url = "http://example.test/manual",
                        Title = "Kept Manual Row",
                        MediaKind = MediaKind.Rtsp,
                        SourceOrigin = SourceOrigin.Imported,
                        Access = ChannelAccess.GeoRestricted,
                        LastPlayOutcome = PlayOutcome.Ok,
                        AddedAt = DateTimeOffset.UnixEpoch
                    }
                ]
            });

            var json = await File.ReadAllTextAsync(path);

            Assert.Contains("\"viewMode\": \"Grid\"", json, StringComparison.Ordinal);
            Assert.Contains("\"tileSize\": \"Large\"", json, StringComparison.Ordinal);
            Assert.Contains("\"videoBackend\": \"Flyleaf\"", json, StringComparison.Ordinal);
            Assert.Contains("\"mediaKind\": \"Rtsp\"", json, StringComparison.Ordinal);
            Assert.Contains("\"sourceOrigin\": \"Imported\"", json, StringComparison.Ordinal);
            Assert.Contains("\"access\": \"GeoRestricted\"", json, StringComparison.Ordinal);
            Assert.Contains("\"lastPlayOutcome\": \"Ok\"", json, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// A cast integer that reached memory some other way must not be persisted: writing a token this
    /// build cannot read back would plant tomorrow's unreadable file today.
    /// </summary>
    [Fact]
    public async Task Save_NormalizesAnUndefinedEnumRatherThanPersistingIt()
    {
        await WithStoreAsync(async (path, store) =>
        {
            await store.SaveAsync(new CatalogState { TileSize = (StreamTileSize)99 });

            var json = await File.ReadAllTextAsync(path);
            Assert.Contains("\"tileSize\": \"Medium\"", json, StringComparison.Ordinal);
            Assert.Equal(StreamTileSize.Medium, (await store.LoadAsync()).TileSize);
        });
    }
}
