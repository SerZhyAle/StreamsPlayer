using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class CatalogStateHideTests
{
    [Fact]
    public void HiddenCatalogUrls_DefaultsToEmpty()
    {
        Assert.Empty(new CatalogState().HiddenCatalogUrls);
    }

    [Fact]
    public async Task Save_PreservesHiddenCatalogUrls()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"StreamsPlayer.Tests.{Guid.NewGuid():N}");
        try
        {
            var store = new StreamCatalogStore(directory);
            await store.SaveAsync(new CatalogState
            {
                HiddenCatalogUrls = ["https://example.test/one.m3u8", "https://example.test/two.m3u8"]
            });

            var loaded = await store.LoadAsync();

            Assert.Equal(
                ["https://example.test/one.m3u8", "https://example.test/two.m3u8"],
                loaded.HiddenCatalogUrls);
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
    public async Task Load_LegacyStateWithoutHiddenField_YieldsEmptyList()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"StreamsPlayer.Tests.{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            // A pre-SP-0020 state file has no HiddenCatalogUrls member; deserialization must default it, not throw.
            await File.WriteAllTextAsync(
                Path.Combine(directory, "catalog-state.json"),
                "{ \"schemaVersion\": 1, \"channels\": [] }");
            var store = new StreamCatalogStore(directory);

            var loaded = await store.LoadAsync();

            Assert.NotNull(loaded.HiddenCatalogUrls);
            Assert.Empty(loaded.HiddenCatalogUrls);
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
