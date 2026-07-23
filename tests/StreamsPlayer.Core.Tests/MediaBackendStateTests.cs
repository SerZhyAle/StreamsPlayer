using System.Text.Json;
using System.Text.Json.Serialization;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class MediaBackendStateTests
{
    private static readonly JsonSerializerOptions StringEnumJson =
        new() { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public void DefaultState_UsesLibVlcBackend()
    {
        Assert.Equal(MediaBackend.LibVlc, new CatalogState().VideoBackend);
    }

    [Fact]
    public async Task Save_PreservesVideoBackendChoice()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"StreamsPlayer.Tests.{Guid.NewGuid():N}");
        try
        {
            var store = new StreamCatalogStore(directory);

            await store.SaveAsync(new CatalogState { VideoBackend = MediaBackend.Flyleaf });

            var loaded = await store.LoadAsync();
            Assert.Equal(MediaBackend.Flyleaf, loaded.VideoBackend);
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
    public void VideoBackend_SerializesAsEnumString()
    {
        var json = JsonSerializer.Serialize(new CatalogState { VideoBackend = MediaBackend.Flyleaf }, StringEnumJson);
        Assert.Contains("\"Flyleaf\"", json);
    }

    [Fact]
    public void StateJson_WithoutVideoBackendKey_DefaultsToLibVlc()
    {
        var loaded = JsonSerializer.Deserialize<CatalogState>("{}", StringEnumJson);
        Assert.NotNull(loaded);
        Assert.Equal(MediaBackend.LibVlc, loaded!.VideoBackend);
    }
}
