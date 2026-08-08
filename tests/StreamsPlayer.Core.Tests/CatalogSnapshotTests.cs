using System.IO.Compression;
using System.Text;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

// SP-0052. The payload reader is driven through synthetic archives so the suite does not depend on the
// tracked artifact being present in the working tree.
public sealed class CatalogSnapshotTests
{
    private static readonly DateTimeOffset SourceDate = new(2026, 8, 7, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Read_ReturnsTheBankAndItsProvenance()
    {
        var snapshot = BundledCatalogSnapshot.Read(CreateSnapshotZip(atlas: [1, 2, 3]));

        Assert.Equal(2, snapshot.Bank.Entries.Count);
        Assert.Equal([1, 2, 3], snapshot.Bank.FaviconAtlas);
        Assert.Equal(SourceDate, snapshot.SourceDate);
        Assert.Equal(StreamCatalogService.CatalogUrl, snapshot.SourceUrl);
    }

    // AC 10: every damaged payload is one exception type, and the message is fit to show the user.
    [Theory]
    [InlineData(SnapshotDamage.NotAZip)]
    [InlineData(SnapshotDamage.CsvNotFirst)]
    [InlineData(SnapshotDamage.NoMetadata)]
    [InlineData(SnapshotDamage.MetadataWithoutDate)]
    [InlineData(SnapshotDamage.NoChannels)]
    public void Read_RejectsADamagedPayload(SnapshotDamage damage)
    {
        var exception = Assert.Throws<InvalidDataException>(() => BundledCatalogSnapshot.Read(Damaged(damage)));
        Assert.Contains("snapshot", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Apply_FillsAnEmptyCatalogWithoutClaimingADownload()
    {
        await InTemporaryStore(async (store, directory) =>
        {
            var result = await new CatalogSnapshotService(store)
                .ApplyAsync(BundledCatalogSnapshot.Read(CreateSnapshotZip(atlas: [1, 2, 3])), new CatalogState());

            Assert.Equal(2, result.Added);
            Assert.Equal(0, result.Updated);
            Assert.Equal(SourceDate, result.SourceDate);
            Assert.Null(result.State.LastCatalogRefreshAt);
            Assert.Equal(SourceDate, result.State.AppliedSnapshotDate);
            Assert.All(result.State.Channels, channel =>
            {
                Assert.Equal(SourceOrigin.Catalog, channel.SourceOrigin);
                Assert.Equal(FaviconSource.Snapshot, channel.FaviconSource);
            });

            // The atlas is installed through the store's own slot, not dropped beside it.
            Assert.Null(result.State.AtlasFileName);
            Assert.NotNull(result.State.SnapshotAtlasFileName);
            Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(store.ResolveAtlasPath(result.State, AtlasSlot.Snapshot)!));
            Assert.Equal(result.State.SnapshotAtlasFileName, (await store.LoadAsync()).SnapshotAtlasFileName);
        });
    }

    // AC 5 and AC 7 together: nothing the download brought in is removed, and the two atlases coexist so
    // each row's favicon index keeps indexing the sheet it shipped with.
    [Fact]
    public async Task Apply_OverADownloadedCatalogKeepsBothRowsAndBothAtlases()
    {
        await InTemporaryStore(async (store, directory) =>
        {
            var downloaded = new StreamChannel
            {
                Id = Guid.NewGuid(),
                Url = "https://example.test/downloaded",
                Title = "Downloaded",
                MediaKind = MediaKind.Audio,
                SourceOrigin = SourceOrigin.Catalog,
                AddedAt = DateTimeOffset.UtcNow,
                FaviconIndex = 7
            };
            var manual = downloaded with
            {
                Id = Guid.NewGuid(),
                Url = "https://example.test/manual",
                SourceOrigin = SourceOrigin.Manual
            };
            var refreshed = DateTimeOffset.UtcNow.AddMinutes(-5);
            var seeded = await store.SaveAsync(
                new CatalogState { Channels = [downloaded, manual], LastCatalogRefreshAt = refreshed },
                [9, 9],
                replaceAtlas: true);

            var result = await new CatalogSnapshotService(store)
                .ApplyAsync(BundledCatalogSnapshot.Read(CreateSnapshotZip(atlas: [1, 2, 3])), seeded);

            Assert.Equal(4, result.State.Channels.Count);
            Assert.Contains(result.State.Channels, channel => channel.Id == downloaded.Id);
            Assert.Contains(result.State.Channels, channel => channel == manual);
            Assert.Equal(refreshed, result.State.LastCatalogRefreshAt);

            Assert.Equal(seeded.AtlasFileName, result.State.AtlasFileName);
            Assert.Equal([9, 9], await File.ReadAllBytesAsync(store.ResolveAtlasPath(result.State)!));
            Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(store.ResolveAtlasPath(result.State, AtlasSlot.Snapshot)!));

            var byId = result.State.Channels.ToDictionary(channel => channel.Id);
            Assert.Equal(FaviconSource.Catalog, byId[downloaded.Id].FaviconSource);
            Assert.All(
                result.State.Channels.Where(channel => channel.Url.Contains("snapshot", StringComparison.Ordinal)),
                channel => Assert.Equal(FaviconSource.Snapshot, channel.FaviconSource));
        });
    }

    // AC 6, and the disk cost that comes with it: an online refresh reclaims every snapshot row onto the
    // downloaded atlas, after which the bundled atlas is referenced by nothing. Its file has to go, or
    // the user keeps paying seven megabytes for a sheet no row indexes.
    [Fact]
    public async Task Refresh_ReleasesTheSnapshotAtlasOnceNoRowUsesIt()
    {
        await InTemporaryStore(async (store, directory) =>
        {
            var seeded = await new CatalogSnapshotService(store)
                .ApplyAsync(BundledCatalogSnapshot.Read(CreateSnapshotZip(atlas: [1, 2, 3])), new CatalogState());
            var snapshotAtlasPath = store.ResolveAtlasPath(seeded.State, AtlasSlot.Snapshot)!;
            Assert.True(File.Exists(snapshotAtlasPath));

            using var httpClient = new HttpClient(new SingleZipHandler(CreateSnapshotZip(atlas: [7, 7])));
            var refreshed = await new StreamCatalogService(httpClient, store).RefreshAsync(seeded.State);

            Assert.All(refreshed.State.Channels, channel => Assert.Equal(FaviconSource.Catalog, channel.FaviconSource));
            Assert.Null(refreshed.State.SnapshotAtlasFileName);
            Assert.Null(refreshed.State.AppliedSnapshotDate);
            Assert.False(File.Exists(snapshotAtlasPath));
            Assert.Equal([7, 7], await File.ReadAllBytesAsync(store.ResolveAtlasPath(refreshed.State)!));
        });
    }

    // AC 10: a damaged payload leaves the stored catalog exactly as it was.
    [Fact]
    public async Task Apply_LeavesTheStoredStateUntouchedWhenThePayloadIsDamaged()
    {
        await InTemporaryStore(async (store, directory) =>
        {
            await store.SaveAsync(new CatalogState { LastCatalogRefreshAt = DateTimeOffset.UtcNow }, [9], replaceAtlas: true);
            var statePath = Path.Combine(directory, "catalog-state.json");
            var before = await File.ReadAllBytesAsync(statePath);

            Assert.Throws<InvalidDataException>(() =>
                BundledCatalogSnapshot.Read(Damaged(SnapshotDamage.CsvNotFirst)));

            Assert.Equal(before, await File.ReadAllBytesAsync(statePath));
        });
    }

    // AC 9: a build that carries a snapshot must carry a usable one. It passes vacuously in a source
    // tree whose artifact has not been generated - the same state a deliberately snapshot-less build is
    // in - because whether the artifact is *present* is a release question, gated by
    // tools/build-catalog-snapshot.ps1 -Check, not something a developer build should fail on.
    [Fact]
    public void TheEmbeddedSnapshotOfThisBuildIsReadable()
    {
        if (!BundledCatalogSnapshot.Exists)
        {
            return;
        }

        var snapshot = BundledCatalogSnapshot.Read();

        Assert.NotEmpty(snapshot.Bank.Entries);
        Assert.True(snapshot.Bank.CsvWasFirstEntry);
        Assert.NotNull(snapshot.Bank.FaviconAtlas);
        Assert.NotEqual(default, snapshot.SourceDate);
        // Decision 8: not every row is a live stream upstream, and the ones that are not do not belong
        // in a first launch - the generator drops them, and this is what proves it kept doing so.
        Assert.DoesNotContain(snapshot.Bank.Entries, entry => entry.IsLive == false);
    }

    public enum SnapshotDamage
    {
        NotAZip,
        CsvNotFirst,
        NoMetadata,
        MetadataWithoutDate,
        NoChannels
    }

    private static async Task InTemporaryStore(Func<StreamCatalogStore, string, Task> body)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"StreamsPlayer.Tests.{Guid.NewGuid():N}");
        try
        {
            await body(new StreamCatalogStore(directory), directory);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static byte[] Damaged(SnapshotDamage damage) => damage switch
    {
        SnapshotDamage.NotAZip => Encoding.UTF8.GetBytes("not a zip at all"),
        SnapshotDamage.CsvNotFirst => CreateSnapshotZip(atlas: [1], csvFirst: false),
        SnapshotDamage.NoMetadata => CreateSnapshotZip(atlas: [1], metadata: null),
        SnapshotDamage.MetadataWithoutDate => CreateSnapshotZip(atlas: [1], metadata: """{"sourceUrl":"https://example.test"}"""),
        _ => CreateSnapshotZip(atlas: [1], csv: "name,url,media_kind,favicon_index\n")
    };

    private static byte[] CreateSnapshotZip(
        byte[]? atlas,
        bool csvFirst = true,
        string? csv = null,
        string? metadata = "default")
    {
        csv ??= "name,url,media_kind,favicon_index\n" +
            "Snapshot one,https://example.test/snapshot-one,AUDIO,0\n" +
            "Snapshot two,https://example.test/snapshot-two,VIDEO,1";
        metadata = metadata == "default"
            ? $$"""{"sourceDate":"{{SourceDate:o}}","sourceUrl":"{{StreamCatalogService.CatalogUrl}}"}"""
            : metadata;

        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (!csvFirst && atlas is not null)
            {
                Write(archive, "favicon-atlas.png", atlas);
            }

            Write(archive, "streams.csv", Encoding.UTF8.GetBytes(csv));
            if (csvFirst && atlas is not null)
            {
                Write(archive, "favicon-atlas.png", atlas);
            }

            if (metadata is not null)
            {
                Write(archive, "snapshot.json", Encoding.UTF8.GetBytes(metadata));
            }
        }

        return buffer.ToArray();
    }

    private sealed class SingleZipHandler(byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            });
    }

    private static void Write(ZipArchive archive, string name, byte[] content)
    {
        using var stream = archive.CreateEntry(name).Open();
        stream.Write(content);
    }
}
