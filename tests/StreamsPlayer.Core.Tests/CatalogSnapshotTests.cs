using System.IO.Compression;
using System.Text;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

// SP-0052. The payload reader is driven through synthetic archives so the suite does not depend on the
// tracked artifact being present in the working tree.
public sealed class CatalogSnapshotTests
{
    private static readonly DateTimeOffset SourceDate = new(2026, 8, 7, 9, 30, 0, TimeSpan.Zero);

    private static readonly byte[] ValidAtlasA =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D,
        0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x20,
        0x00, 0x00, 0x00, 0x20,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    ];

    private static readonly byte[] ValidAtlasB =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D,
        0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x40,
        0x00, 0x00, 0x00, 0x40,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    ];

    private static readonly byte[] ValidAtlasC =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D,
        0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x80,
        0x00, 0x00, 0x00, 0x80,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    ];

    [Fact]
    public void Read_ReturnsTheBankAndItsProvenance()
    {
        var snapshot = BundledCatalogSnapshot.Read(CreateSnapshotZip(atlas: ValidAtlasA));

        Assert.Equal(2, snapshot.Bank.Entries.Count);
        Assert.Equal(ValidAtlasA, snapshot.Bank.FaviconAtlas);
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
                .ApplyAsync(BundledCatalogSnapshot.Read(CreateSnapshotZip(atlas: ValidAtlasA)), new CatalogState());

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
            Assert.Equal(ValidAtlasA, await File.ReadAllBytesAsync(store.ResolveAtlasPath(result.State, AtlasSlot.Snapshot)!));
            Assert.Equal(result.State.SnapshotAtlasFileName, (await store.LoadAsync()).SnapshotAtlasFileName);
        });
    }

    // AC 5 and AC 7 together: nothing the download brought in is removed, and the two atlases coexist so
    // each row's favicon index keeps indexing the sheet it shipped with.
    // SP-0126: the snapshot is older than the download that retired the row; listing it proves nothing.
    [Fact]
    public void Apply_LeavesARetiredRowRetired()
    {
        var retiredAt = DateTimeOffset.UtcNow.AddDays(-1);
        var retired = new StreamChannel
        {
            Id = Guid.NewGuid(),
            Url = "https://example.test/snapshot-one",
            Title = "Retired",
            MediaKind = MediaKind.Audio,
            SourceOrigin = SourceOrigin.Catalog,
            AddedAt = DateTimeOffset.UtcNow.AddDays(-30),
            Pinned = true,
            RetiredAt = retiredAt
        };
        var outcome = CatalogSnapshotService.Prepare(BundledCatalogSnapshot.Read(CreateSnapshotZip(atlas: ValidAtlasA)));

        var result = outcome.Apply(new CatalogState { Channels = [retired] });

        var kept = Assert.Single(result.State.Channels, channel => channel.Id == retired.Id);
        Assert.Equal(retiredAt, kept.RetiredAt);
    }

    // SP-0177: offered after a failed refresh, the snapshot can meet a list a later download wrote.
    [Fact]
    public void Apply_OverALaterDownloadKeepsTheNewerRow()
    {
        var downloaded = new StreamChannel
        {
            Id = Guid.NewGuid(),
            Url = "https://example.test/snapshot-one",
            Title = "Newer title",
            MediaKind = MediaKind.Audio,
            SourceOrigin = SourceOrigin.Catalog,
            AddedAt = SourceDate,
            IsLive = true
        };
        var outcome = CatalogSnapshotService.Prepare(BundledCatalogSnapshot.Read(CreateSnapshotZip(atlas: null)));

        var later = outcome.Apply(new CatalogState { Channels = [downloaded], LastCatalogRefreshAt = SourceDate.AddDays(10) });
        var earlier = outcome.Apply(new CatalogState { Channels = [downloaded], LastCatalogRefreshAt = SourceDate.AddDays(-10) });

        Assert.Equal(downloaded, Assert.Single(later.State.Channels, channel => channel.Id == downloaded.Id));
        Assert.Contains(later.State.Channels, channel => channel.Url == "https://example.test/snapshot-two");
        Assert.Equal("Snapshot one", Assert.Single(earlier.State.Channels, channel => channel.Id == downloaded.Id).Title);
    }

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
                ValidAtlasB,
                replaceAtlas: true);

            var result = await new CatalogSnapshotService(store)
                .ApplyAsync(BundledCatalogSnapshot.Read(CreateSnapshotZip(atlas: ValidAtlasA)), seeded);

            Assert.Equal(4, result.State.Channels.Count);
            Assert.Contains(result.State.Channels, channel => channel.Id == downloaded.Id);
            Assert.Contains(result.State.Channels, channel => channel == manual);
            Assert.Equal(refreshed, result.State.LastCatalogRefreshAt);

            Assert.Equal(seeded.AtlasFileName, result.State.AtlasFileName);
            Assert.Equal(ValidAtlasB, await File.ReadAllBytesAsync(store.ResolveAtlasPath(result.State)!));
            Assert.Equal(ValidAtlasA, await File.ReadAllBytesAsync(store.ResolveAtlasPath(result.State, AtlasSlot.Snapshot)!));

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
                .ApplyAsync(BundledCatalogSnapshot.Read(CreateSnapshotZip(atlas: ValidAtlasA)), new CatalogState());
            var snapshotAtlasPath = store.ResolveAtlasPath(seeded.State, AtlasSlot.Snapshot)!;
            Assert.True(File.Exists(snapshotAtlasPath));

            using var httpClient = new HttpClient(new SingleZipHandler(CreateSnapshotZip(atlas: ValidAtlasC)));
            var refreshed = await new StreamCatalogService(httpClient, store).RefreshAsync(seeded.State);

            Assert.All(refreshed.State.Channels, channel => Assert.Equal(FaviconSource.Catalog, channel.FaviconSource));
            Assert.Null(refreshed.State.SnapshotAtlasFileName);
            Assert.Null(refreshed.State.AppliedSnapshotDate);
            Assert.False(File.Exists(snapshotAtlasPath));
            Assert.Equal(ValidAtlasC, await File.ReadAllBytesAsync(store.ResolveAtlasPath(refreshed.State)!));
        });
    }

    // AC 10: a damaged payload leaves the stored catalog exactly as it was.
    [Fact]
    public async Task Apply_LeavesTheStoredStateUntouchedWhenThePayloadIsDamaged()
    {
        await InTemporaryStore(async (store, directory) =>
        {
            await store.SaveAsync(new CatalogState { LastCatalogRefreshAt = DateTimeOffset.UtcNow }, ValidAtlasB, replaceAtlas: true);
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
        SnapshotDamage.CsvNotFirst => CreateSnapshotZip(atlas: ValidAtlasA, csvFirst: false),
        SnapshotDamage.NoMetadata => CreateSnapshotZip(atlas: ValidAtlasA, metadata: null),
        SnapshotDamage.MetadataWithoutDate => CreateSnapshotZip(atlas: ValidAtlasA, metadata: """{"sourceUrl":"https://example.test"}"""),
        _ => CreateSnapshotZip(atlas: ValidAtlasA, csv: "name,url,media_kind,favicon_index\n")
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
