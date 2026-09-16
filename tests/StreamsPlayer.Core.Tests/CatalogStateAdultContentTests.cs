using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class CatalogStateAdultContentTests
{
    [Fact]
    public void CatalogStateDefaultsHideAdultContentToFalse()
    {
        var state = new CatalogState();
        Assert.False(state.HideAdultContent);
    }

    [Fact]
    public void CatalogStateWithoutHideAdultContentDeserializesToFalse()
    {
        var json = """
            {
                "schemaVersion": 1,
                "channels": []
            }
            """;
        var state = JsonSerializer.Deserialize<CatalogState>(json);
        Assert.NotNull(state);
        Assert.False(state.HideAdultContent);
    }

    [Fact]
    public void CatalogStateWithHideAdultContentRoundTrips()
    {
        var state = new CatalogState
        {
            HideAdultContent = true
        };
        var json = JsonSerializer.Serialize(state);
        var restored = JsonSerializer.Deserialize<CatalogState>(json);
        Assert.NotNull(restored);
        Assert.True(restored.HideAdultContent);
    }
}
