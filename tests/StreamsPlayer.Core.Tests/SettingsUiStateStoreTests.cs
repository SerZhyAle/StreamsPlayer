using System.Text.Json;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0191: the settings window's private navigation context store - round trip, tolerant reads and
/// the SP-0175 boundary (a file that could not be read is never written over).
/// </summary>
public sealed class SettingsUiStateStoreTests
{
    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sp191-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public async Task RoundTripsPageGroupsViewportsAndWindow()
    {
        var directory = TempDirectory();
        try
        {
            var store = new SettingsUiStateStore(directory);
            var state = new SettingsUiState
            {
                LastPageIndex = 4,
                Groups = new Dictionary<string, bool> { ["library.catalog"] = false, ["about.diagnostics"] = true },
                Viewports = new Dictionary<string, SettingsViewportAnchor>
                {
                    ["page1"] = new SettingsViewportAnchor("library.tv", 37.5)
                },
                Window = new SettingsWindowRectangle(11, 22, 800, 600)
            };

            Assert.True(await store.SaveAsync(state));
            var loaded = await new SettingsUiStateStore(directory).LoadAsync();

            Assert.Equal(4, loaded.LastPageIndex);
            Assert.False(loaded.Groups["library.catalog"]);
            Assert.Equal("library.tv", loaded.Viewports["page1"].GroupId);
            Assert.Equal(37.5, loaded.Viewports["page1"].OffsetWithinGroup);
            Assert.True(loaded.Window!.IsUsable);
            Assert.Equal(800, loaded.Window.Width);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AbsentFileReadsAsEmptyStateAndOlderFilesWithoutNewKeysStayReadable()
    {
        var directory = TempDirectory();
        try
        {
            var store = new SettingsUiStateStore(directory);
            var empty = await store.LoadAsync();
            Assert.Equal(0, empty.LastPageIndex);
            Assert.Empty(empty.Groups);
            Assert.Null(empty.Window);

            // A state written before the window rectangle existed must still load.
            var path = Path.Combine(directory, "settings-ui.json");
            await File.WriteAllTextAsync(path, """{"schemaVersion":1,"lastPageIndex":2}""");
            var loaded = await new SettingsUiStateStore(directory).LoadAsync();
            Assert.Equal(2, loaded.LastPageIndex);
            Assert.Null(loaded.Window);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task UnreadableFileIsNeverOverwritten()
    {
        var directory = TempDirectory();
        try
        {
            var path = Path.Combine(directory, "settings-ui.json");
            const string broken = "{ not json";
            await File.WriteAllTextAsync(path, broken);

            var store = new SettingsUiStateStore(directory);
            var loaded = await store.LoadAsync();
            Assert.Equal(0, loaded.LastPageIndex);

            // The read failed, so the save declines and the broken file is left exactly as it was -
            // one closed window must not replace every remembered placement with a single entry.
            Assert.False(await store.SaveAsync(new SettingsUiState { LastPageIndex = 3 }));
            Assert.Equal(broken, await File.ReadAllTextAsync(path));

            // A later successful read re-enables saving (SP-0175's boundary).
            await File.WriteAllTextAsync(path, """{"schemaVersion":1,"lastPageIndex":1}""");
            _ = await store.LoadAsync();
            Assert.True(await store.SaveAsync(new SettingsUiState { LastPageIndex = 5 }));
            var after = await new SettingsUiStateStore(directory).LoadAsync();
            Assert.Equal(5, after.LastPageIndex);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Audit 26.1010.0106 A7: a zero-byte file is absent for both reads, and never blocks saving.</summary>
    [Fact]
    public async Task ZeroByteFileReadsAsAbsentForBothReadsAndSavingStillWorks()
    {
        var directory = TempDirectory();
        try
        {
            var path = Path.Combine(directory, "settings-ui.json");
            await File.WriteAllBytesAsync(path, []);

            var store = new SettingsUiStateStore(directory);
            var loaded = store.LoadSync();
            Assert.Equal(0, loaded.LastPageIndex);
            Assert.Empty(loaded.Groups);

            Assert.True(await store.SaveAsync(new SettingsUiState { LastPageIndex = 2 }));
            Assert.Equal(2, new SettingsUiStateStore(directory).LoadSync().LastPageIndex);

            await File.WriteAllBytesAsync(path, []);
            var viaAsync = new SettingsUiStateStore(directory);
            _ = await viaAsync.LoadAsync();
            Assert.True(await viaAsync.SaveAsync(new SettingsUiState { LastPageIndex = 3 }));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ABrokenFileStillRefusesSavesAfterTheSynchronousRead()
    {
        var directory = TempDirectory();
        try
        {
            var path = Path.Combine(directory, "settings-ui.json");
            await File.WriteAllTextAsync(path, "{ not json");

            var store = new SettingsUiStateStore(directory);
            Assert.Equal(0, store.LoadSync().LastPageIndex);
            Assert.False(await store.SaveAsync(new SettingsUiState { LastPageIndex = 3 }));
            Assert.Equal("{ not json", await File.ReadAllTextAsync(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Audit A7: a hand-edited document that parses but holds nulls must not reach the settings window,
    /// which indexes the dictionaries without a check (SP-0175, as PlayerWindowGeometryStore.Sanitize).
    /// </summary>
    [Theory]
    [InlineData("""{"schemaVersion":1,"lastPageIndex":1,"groups":null,"viewports":null}""")]
    [InlineData("""{"schemaVersion":1,"lastPageIndex":1,"viewports":{"page1":null,"page2":{"groupId":null,"offsetWithinGroup":3}}}""")]
    [InlineData("null")]
    public async Task NullMembersOfAParsedDocumentAreNormalizedForBothReads(string json)
    {
        var directory = TempDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "settings-ui.json"), json);

            foreach (var state in new[]
            {
                new SettingsUiStateStore(directory).LoadSync(),
                await new SettingsUiStateStore(directory).LoadAsync()
            })
            {
                Assert.NotNull(state.Groups);
                Assert.NotNull(state.Viewports);
                Assert.Empty(state.Viewports);
                Assert.All(state.Viewports.Values, anchor => Assert.NotNull(anchor.GroupId));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ValidAnchorsAndGroupsSurviveNormalization()
    {
        var directory = TempDirectory();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(directory, "settings-ui.json"),
                """{"schemaVersion":1,"groups":{"a":false},"viewports":{"p":{"groupId":"g","offsetWithinGroup":4.5},"q":null}}""");

            var state = new SettingsUiStateStore(directory).LoadSync();

            Assert.False(state.Groups["a"]);
            var anchor = Assert.Single(state.Viewports);
            Assert.Equal("p", anchor.Key);
            Assert.Equal(new SettingsViewportAnchor("g", 4.5), anchor.Value);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Re-audit D6: the store reads through the one sanitizer, so its anchor rule is the sanitizer's - an
    /// anchor with an empty group ID is dropped on both reads, not only a null one.
    /// </summary>
    [Fact]
    public async Task AnAnchorWithAnEmptyGroupIdIsDroppedOnBothReads()
    {
        var directory = TempDirectory();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(directory, "settings-ui.json"),
                """{"schemaVersion":1,"viewports":{"p":{"groupId":"","offsetWithinGroup":4},"q":{"groupId":"g","offsetWithinGroup":1}}}""");

            var sync = new SettingsUiStateStore(directory).LoadSync();
            var viaAsync = await new SettingsUiStateStore(directory).LoadAsync();

            Assert.Equal("q", Assert.Single(sync.Viewports).Key);
            Assert.Equal("q", Assert.Single(viaAsync.Viewports).Key);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void WindowRectangleDegradesGracefullyOnNonsense()
    {
        Assert.False(new SettingsWindowRectangle(0, 0, 10, 5000).IsUsable);
        Assert.True(new SettingsWindowRectangle(-800, -100, 1280, 720).IsUsable);
    }
}
