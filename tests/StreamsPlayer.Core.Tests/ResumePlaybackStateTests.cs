using System.Text.Json;
using System.Text.Json.Nodes;

using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class ResumePlaybackStateTests
{
    [Fact]
    public void DefaultState_DoesNotResumePlaybackAndRemembersNothing()
    {
        var state = new CatalogState();

        Assert.False(state.ResumePlaybackOnStartup);
        Assert.Empty(state.ResumeChannelIds);
    }

    [Fact]
    public async Task Save_PreservesTheResumePreferenceAndTheRecordedOrder()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"StreamsPlayer.Tests.{Guid.NewGuid():N}");
        try
        {
            var store = new StreamCatalogStore(directory);
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();

            await store.SaveAsync(new CatalogState
            {
                ResumePlaybackOnStartup = true,
                ResumeChannelIds = [first, second]
            });

            var loaded = await store.LoadAsync();

            Assert.True(loaded.ResumePlaybackOnStartup);
            // Order is the contract, not just membership: the record is replayed in the order the streams
            // started, and a set would also have lost the legitimate case of one channel in two windows.
            Assert.Equal([first, second], loaded.ResumeChannelIds);
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
    public async Task Save_KeepsTheSameChannelTwiceForTwoWindows()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"StreamsPlayer.Tests.{Guid.NewGuid():N}");
        try
        {
            var store = new StreamCatalogStore(directory);
            var id = Guid.NewGuid();

            await store.SaveAsync(new CatalogState { ResumeChannelIds = [id, id] });

            var loaded = await store.LoadAsync();
            Assert.Equal([id, id], loaded.ResumeChannelIds);
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
    public async Task Load_StateWrittenWithoutTheResumeKeys_YieldsOffAndKeepsEverythingElse()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"StreamsPlayer.Tests.{Guid.NewGuid():N}");
        try
        {
            var store = new StreamCatalogStore(directory);
            await store.SaveAsync(new CatalogState
            {
                ResumePlaybackOnStartup = true,
                ResumeChannelIds = [Guid.NewGuid()],
                CatalogSortMode = "Bitrate",
                AudioVolume = 42
            });

            // Strip both keys from the file the store itself wrote, so the real reader - not a hand-typed
            // JSON fragment - is what has to fall back. This is the shape of every pre-SP-0062 state file,
            // and it is the half of acceptance criterion 1 about an upgraded installation.
            var statePath = Path.Combine(directory, "catalog-state.json");
            var document = JsonNode.Parse(await File.ReadAllTextAsync(statePath))!.AsObject();
            Assert.True(document.Remove("resumePlaybackOnStartup"));
            Assert.True(document.Remove("resumeChannelIds"));
            await File.WriteAllTextAsync(statePath, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            var loaded = await store.LoadAsync();

            Assert.False(loaded.ResumePlaybackOnStartup);
            Assert.Empty(loaded.ResumeChannelIds);
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
}
