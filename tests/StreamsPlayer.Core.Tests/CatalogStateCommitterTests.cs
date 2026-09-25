using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class CatalogStateCommitterTests
{
    [Fact]
    public async Task CommitAsync_SerializesInterleavedMutationsAgainstTheLatestState()
    {
        var firstWriteStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFirstWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saved = new List<CatalogState>();
        var writeCount = 0;
        var committer = new CatalogStateCommitter(new CatalogState(), async (state, _) =>
        {
            saved.Add(state);
            if (Interlocked.Increment(ref writeCount) == 1)
            {
                firstWriteStarted.SetResult();
                await allowFirstWrite.Task;
            }

            return state;
        });

        var pin = committer.CommitAsync(state => state with
        {
            Channels = [Channel("https://example.test/pinned") with { Pinned = true }]
        });
        await firstWriteStarted.Task;
        var collection = committer.CommitAsync(state => state with
        {
            Collections = [new ChannelCollection { Id = Guid.NewGuid(), Name = "Morning", ChannelIds = [state.Channels[0].Id] }]
        });

        allowFirstWrite.SetResult();
        await Task.WhenAll(pin, collection);

        var final = Assert.Single(saved.Last().Channels);
        Assert.True(final.Pinned);
        Assert.Single(saved.Last().Collections);
        Assert.Contains(final.Id, saved.Last().Collections[0].ChannelIds);
    }

    [Fact]
    public async Task CommitAsync_KeepsAnUnsavedMutationAndRetriesItWithTheNextCommit()
    {
        var attempts = 0;
        var committer = new CatalogStateCommitter(new CatalogState(), (state, _) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                throw new IOException("locked");
            }

            return Task.FromResult(state);
        });

        var failed = await committer.CommitAsync(state => state with { AudioVolume = 42 });
        var retried = await committer.CommitAsync(state => state with { VideoVolume = 24 });

        Assert.False(failed.Saved);
        Assert.IsType<IOException>(failed.Failure);
        Assert.True(retried.Saved);
        Assert.Equal(42, retried.State.AudioVolume);
        Assert.Equal(24, retried.State.VideoVolume);
    }

    private static StreamChannel Channel(string url) => new()
    {
        Id = Guid.NewGuid(),
        Url = url,
        Title = "Station",
        MediaKind = MediaKind.Audio,
        SourceOrigin = SourceOrigin.Catalog,
        AddedAt = DateTimeOffset.UtcNow
    };
}
