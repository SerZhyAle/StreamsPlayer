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

    [Fact]
    public void WindowRectangleDegradesGracefullyOnNonsense()
    {
        Assert.False(new SettingsWindowRectangle(0, 0, 10, 5000).IsUsable);
        Assert.True(new SettingsWindowRectangle(-800, -100, 1280, 720).IsUsable);
    }
}
