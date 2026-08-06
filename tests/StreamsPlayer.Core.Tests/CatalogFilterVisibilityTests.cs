using System.Text.Json;
using System.Text.Json.Nodes;

using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class CatalogFilterVisibilityTests
{
    [Fact]
    public async Task Load_StateWrittenWithoutTheKey_YieldsHiddenAndKeepsEverythingElse()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"StreamsPlayer.Tests.{Guid.NewGuid():N}");
        try
        {
            var store = new StreamCatalogStore(directory);
            await store.SaveAsync(new CatalogState
            {
                CatalogFiltersVisible = true,
                CatalogCountryFilter = "Ukraine",
                CatalogSortMode = "Bitrate",
                AudioVolume = 42
            });

            // Strip the key from the file the store itself wrote, so the real reader - not a hand-typed
            // JSON fragment - is what has to fall back. This is the shape of a pre-SP-0050 state file.
            var statePath = Path.Combine(directory, "catalog-state.json");
            var document = JsonNode.Parse(await File.ReadAllTextAsync(statePath))!.AsObject();
            Assert.True(document.Remove("catalogFiltersVisible"));
            await File.WriteAllTextAsync(statePath, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            var loaded = await store.LoadAsync();

            Assert.False(loaded.CatalogFiltersVisible);
            Assert.Equal("Ukraine", loaded.CatalogCountryFilter);
            Assert.Equal("Bitrate", loaded.CatalogSortMode);
            Assert.Equal(42, loaded.AudioVolume);
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
    public async Task Save_PreservesVisibleFilters()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"StreamsPlayer.Tests.{Guid.NewGuid():N}");
        try
        {
            var store = new StreamCatalogStore(directory);
            await store.SaveAsync(new CatalogState { CatalogFiltersVisible = true });

            var loaded = await store.LoadAsync();

            Assert.True(loaded.CatalogFiltersVisible);
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
    public void TogglingVisibilityAlone_ProducesAnUnequalRecord()
    {
        // The browsing session skips a write when `updated == _state`, so the flag has to participate
        // in record equality or revealing the row would never reach the disk.
        var state = new CatalogState();

        var toggled = state with { CatalogFiltersVisible = true };

        Assert.NotEqual(state, toggled);
    }
}
