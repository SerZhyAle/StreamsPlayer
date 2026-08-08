using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0071 amendment: the file itself. It is a cache, so the only behaviour that matters under failure is
/// that nothing escapes - an unreadable file has to look exactly like a source nobody has watched yet.
/// </summary>
public sealed class QualityMemoryStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sp0071-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public async Task AnAbsentFile_LoadsAsNoEvidence()
    {
        var store = new QualityMemoryStore(_directory);

        Assert.Empty(await store.LoadAsync());
    }

    [Fact]
    public async Task WhatIsSavedIsWhatIsLoaded()
    {
        var store = new QualityMemoryStore(_directory);
        var at = new DateTimeOffset(2026, 8, 8, 14, 0, 0, TimeSpan.Zero);
        var entries = new[]
        {
            new ChannelQualityMemory("https://host/live.m3u8", at, [new QualityRungMemory(2_096_000, 4)])
        };

        Assert.True(await store.SaveAsync(entries));
        var loaded = await store.LoadAsync();

        var entry = Assert.Single(loaded);
        Assert.Equal("https://host/live.m3u8", entry.Url);
        Assert.Equal(at, entry.UpdatedAt);
        Assert.Equal(4, Assert.Single(entry.Rungs).Failures);
    }

    [Fact]
    public async Task SavingCreatesTheDirectoryAndLeavesNoTemporaryFile()
    {
        var store = new QualityMemoryStore(_directory);

        Assert.True(await store.SaveAsync([]));

        Assert.True(File.Exists(store.FilePath));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    // The whole reason this is not a field of the catalog state: losing it must cost one relearned probe
    // and nothing else. A corrupt file is not an error the player has to handle.
    [Fact]
    public async Task AnUnreadableFile_LoadsAsNoEvidence()
    {
        var store = new QualityMemoryStore(_directory);
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(store.FilePath, "{ this is not the list it used to be");

        Assert.Empty(await store.LoadAsync());
    }

    // SP-0076 criterion 5, and the reason Ceiling is the last member and nullable: a document written
    // before that field existed still loads, keeping the failure counts it does carry. Written as literal
    // JSON rather than through an old type, because the file on the user's disk is the contract here.
    [Fact]
    public async Task ADocumentFromTheVersionBeforeTheCeiling_StillLoads()
    {
        var store = new QualityMemoryStore(_directory);
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(
            store.FilePath,
            """[{"url":"https://host/live.m3u8","updatedAt":"2026-08-08T14:00:00+00:00","rungs":[{"bandwidthBps":2096000,"failures":4}]}]""");

        var entry = Assert.Single(await store.LoadAsync());

        Assert.Equal(4, Assert.Single(entry.Rungs).Failures);
        Assert.Null(entry.Ceiling);
    }

    [Fact]
    public async Task ACeilingSurvivesTheRoundTrip()
    {
        var store = new QualityMemoryStore(_directory);
        var at = new DateTimeOffset(2026, 8, 8, 14, 0, 0, TimeSpan.Zero);
        var ceiling = new StreamQualityRung(796_000, 640, 360);

        Assert.True(await store.SaveAsync(
            [new ChannelQualityMemory("https://host/live.m3u8", at, [new QualityRungMemory(2_096_000, 4)], ceiling)]));

        Assert.Equal(ceiling, Assert.Single(await store.LoadAsync()).Ceiling);
    }

    [Fact]
    public async Task ASaveReplacesTheWholeDocument()
    {
        var store = new QualityMemoryStore(_directory);
        var at = new DateTimeOffset(2026, 8, 8, 14, 0, 0, TimeSpan.Zero);
        await store.SaveAsync([new ChannelQualityMemory("https://first/live.m3u8", at, [new QualityRungMemory(1, 1)])]);

        await store.SaveAsync([new ChannelQualityMemory("https://second/live.m3u8", at, [new QualityRungMemory(2, 2)])]);

        Assert.Equal("https://second/live.m3u8", Assert.Single(await store.LoadAsync()).Url);
    }
}
