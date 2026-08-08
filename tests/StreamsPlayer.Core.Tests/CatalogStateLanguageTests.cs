using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0034 criteria 5 and 6. Before this ticket an unknown language value threw for the whole
/// document, so a state file written by a newer build cost an older build its catalog, collections,
/// history and window preferences - and the next save committed that emptiness over the real file.
/// Every test here asserts the same thing from a different angle: the rest of the state survives.
/// </summary>
public sealed class CatalogStateLanguageTests
{
    private static async Task<T> WithStoreAsync<T>(Func<string, StreamCatalogStore, Task<T>> body)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"StreamsPlayer.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            return await body(Path.Combine(directory, "catalog-state.json"), new StreamCatalogStore(directory));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>A state file carrying real user data alongside whatever language token is under test.</summary>
    private static string StateJson(string languageProperty) => $$"""
        {
          "schemaVersion": 1,
          {{languageProperty}}
          "channels": [
            {
              "id": "11111111-1111-1111-1111-111111111111",
              "title": "Kept Manual Row",
              "url": "http://example.test/manual",
              "mediaKind": "Audio",
              "sourceOrigin": "Manual",
              "addedAt": "2026-07-27T00:00:00+00:00"
            }
          ],
          "collections": [
            { "id": "22222222-2222-2222-2222-222222222222", "name": "Favourites", "channelIds": [] }
          ],
          "hiddenCatalogUrls": [ "http://example.test/hidden" ],
          "mainWindowTopmost": true,
          "audioVolume": 42,
          "tileSize": "Large"
        }
        """;

    private static void AssertUserDataSurvived(CatalogState state)
    {
        var channel = Assert.Single(state.Channels);
        Assert.Equal("Kept Manual Row", channel.Title);
        Assert.Equal(SourceOrigin.Manual, channel.SourceOrigin);
        Assert.Equal("Favourites", Assert.Single(state.Collections).Name);
        Assert.Equal("http://example.test/hidden", Assert.Single(state.HiddenCatalogUrls));
        Assert.True(state.MainWindowTopmost);
        Assert.Equal(42, state.AudioVolume);
        Assert.Equal(StreamTileSize.Large, state.TileSize);
    }

    [Theory]
    // A language a newer build shipped and this one does not know - the original defect.
    [InlineData("\"language\": \"Klingon\",")]
    // A member name that never existed.
    [InlineData("\"language\": \"NotALanguage\",")]
    // A number outside the defined members; the old converter accepted this silently.
    [InlineData("\"language\": 99,")]
    [InlineData("\"language\": -1,")]
    // An explicit null.
    [InlineData("\"language\": null,")]
    // Structural garbage where a language belongs.
    [InlineData("\"language\": { \"code\": \"hi\" },")]
    [InlineData("\"language\": [ \"hi\" ],")]
    public async Task Load_UnreadableLanguageYieldsNoPreferenceAndKeepsEverythingElse(string languageProperty)
    {
        await WithStoreAsync(async (path, store) =>
        {
            await File.WriteAllTextAsync(path, StateJson(languageProperty));

            var state = await store.LoadAsync();

            Assert.Null(state.Language);
            AssertUserDataSurvived(state);
            return true;
        });
    }

    [Fact]
    public async Task Load_MissingLanguagePropertyMeansNoPreference()
    {
        await WithStoreAsync(async (path, store) =>
        {
            await File.WriteAllTextAsync(path, StateJson(string.Empty));

            var state = await store.LoadAsync();

            Assert.Null(state.Language);
            AssertUserDataSurvived(state);
            return true;
        });
    }

    [Theory]
    [InlineData("English", AppLanguage.English)]
    [InlineData("Russian", AppLanguage.Russian)]
    [InlineData("Ukrainian", AppLanguage.Ukrainian)]
    [InlineData("Arabic", AppLanguage.Arabic)]
    [InlineData("Urdu", AppLanguage.Urdu)]
    [InlineData("Portuguese", AppLanguage.Portuguese)]
    // Case-insensitive, matching how the previous converter behaved, so no existing file regresses.
    [InlineData("russian", AppLanguage.Russian)]
    public async Task Load_HonoursASavedPreference(string token, AppLanguage expected)
    {
        await WithStoreAsync(async (path, store) =>
        {
            await File.WriteAllTextAsync(path, StateJson($"\"language\": \"{token}\","));

            var state = await store.LoadAsync();

            Assert.Equal(expected, state.Language);
            AssertUserDataSurvived(state);
            return true;
        });
    }

    [Fact]
    public async Task Save_RoundTripsEveryShippedLanguage()
    {
        await WithStoreAsync(async (_, store) =>
        {
            foreach (var entry in InterfaceLanguages.All)
            {
                await store.SaveAsync(new CatalogState { Language = entry.Language });
                Assert.Equal(entry.Language, (await store.LoadAsync()).Language);
            }

            return true;
        });
    }

    [Fact]
    public async Task Save_OmitsTheLanguagePropertyWhenNoPreferenceIsHeld()
    {
        // Written as an absent property rather than as null, so a build predating SP-0034 - whose
        // AppLanguage is not nullable - still loads this file instead of throwing on "language": null.
        await WithStoreAsync(async (path, store) =>
        {
            await store.SaveAsync(new CatalogState { Language = null, AudioVolume = 7 });

            var json = await File.ReadAllTextAsync(path);

            Assert.DoesNotContain("\"language\"", json, StringComparison.OrdinalIgnoreCase);
            JsonAssert.HasProperty(json, "audioVolume", "7");
            return true;
        });
    }

    [Fact]
    public async Task Save_WritesTheLanguageByNameSoAnOlderBuildCanReadIt()
    {
        await WithStoreAsync(async (path, store) =>
        {
            await store.SaveAsync(new CatalogState { Language = AppLanguage.Ukrainian });

            JsonAssert.HasProperty(await File.ReadAllTextAsync(path), "language", "\"Ukrainian\"");
            return true;
        });
    }

    [Fact]
    public async Task Save_NormalizesAnUndefinedLanguageRatherThanPersistingIt()
    {
        // Nothing should be able to hand us a cast value, but if it does, it must not be written back
        // and outlive the bug in the user's state file.
        await WithStoreAsync(async (path, store) =>
        {
            await store.SaveAsync(new CatalogState { Language = (AppLanguage)99 });

            var state = await store.LoadAsync();

            Assert.Null(state.Language);
            Assert.DoesNotContain("99", await File.ReadAllTextAsync(path));
            return true;
        });
    }
}
