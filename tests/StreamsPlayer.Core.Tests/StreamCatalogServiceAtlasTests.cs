using System.IO.Compression;
using System.Net;
using System.Text;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class StreamCatalogServiceAtlasTests
{
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

    [Fact]
    public async Task Refresh_KeepsInstalledAtlasWhenTheBankCarriesNone()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"StreamsPlayer.Tests.{Guid.NewGuid():N}");
        try
        {
            var store = new StreamCatalogStore(directory);
            var handler = new QueuedZipHandler(
                CreateBankZip(atlas: ValidAtlasA),
                CreateBankZip(atlas: null));
            using var httpClient = new HttpClient(handler);
            var service = new StreamCatalogService(httpClient, store);

            var withAtlas = await service.RefreshAsync(new CatalogState());
            var atlasPath = store.ResolveAtlasPath(withAtlas.State);
            Assert.NotNull(atlasPath);
            Assert.True(File.Exists(atlasPath));
            Assert.True(withAtlas.AtlasReplaced);

            var withoutAtlas = await service.RefreshAsync(withAtlas.State);

            // SP-0087: keeping the old atlas is right, but the caller has to be able to say so.
            // SP-0088 removed the consequence this comment used to record - the new bank's indices no
            // longer resolve against the previous sheet, because they are discarded; see
            // Refresh_DiscardsThisBuildsFaviconIndicesWhenTheBankCarriesNoAtlas.
            Assert.False(withoutAtlas.AtlasReplaced);
            Assert.Equal(withAtlas.State.AtlasFileName, withoutAtlas.State.AtlasFileName);
            Assert.Equal(ValidAtlasA, await File.ReadAllBytesAsync(store.ResolveAtlasPath(withoutAtlas.State)!));
            Assert.Equal(withAtlas.State.AtlasFileName, (await store.LoadAsync()).AtlasFileName);
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
    public async Task Refresh_ReplacesTheAtlasWhenTheBankCarriesANewOne()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"StreamsPlayer.Tests.{Guid.NewGuid():N}");
        try
        {
            var store = new StreamCatalogStore(directory);
            var handler = new QueuedZipHandler(
                CreateBankZip(atlas: ValidAtlasA),
                CreateBankZip(atlas: ValidAtlasB));
            using var httpClient = new HttpClient(handler);
            var service = new StreamCatalogService(httpClient, store);

            var first = await service.RefreshAsync(new CatalogState());
            var second = await service.RefreshAsync(first.State);

            Assert.True(first.AtlasReplaced);
            Assert.True(second.AtlasReplaced);
            Assert.NotEqual(first.State.AtlasFileName, second.State.AtlasFileName);
            Assert.Equal(ValidAtlasB, await File.ReadAllBytesAsync(store.ResolveAtlasPath(second.State)!));
            Assert.False(File.Exists(store.ResolveAtlasPath(first.State)!));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    // SP-0088, STREAM-BANK item A: a build is atomic. The atlas that fails to arrive takes its own
    // build's indices with it, because an index resolved against a different build's sheet does not
    // produce a missing icon - it produces a confidently wrong one, on a UI that looks healthy.
    [Fact]
    public async Task Refresh_DiscardsThisBuildsFaviconIndicesWhenTheBankCarriesNoAtlas()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"StreamsPlayer.Tests.{Guid.NewGuid():N}");
        try
        {
            var store = new StreamCatalogStore(directory);
            var handler = new QueuedZipHandler(
                CreateBankZip(atlas: ValidAtlasA, faviconIndex: 0),
                CreateBankZip(atlas: null, faviconIndex: 7));
            using var httpClient = new HttpClient(handler);
            var service = new StreamCatalogService(httpClient, store);

            var withAtlas = await service.RefreshAsync(new CatalogState());
            Assert.Equal(0, Assert.Single(withAtlas.State.Channels).FaviconIndex);

            var withoutAtlas = await service.RefreshAsync(withAtlas.State);

            // The index this build published is gone rather than pointed at the previous sheet.
            Assert.Null(Assert.Single(withoutAtlas.State.Channels).FaviconIndex);
            // The other half of the same rule: the installed sheet survives, so a corrected republish
            // can re-point at it. Discarding indices must not become "delete the atlas".
            Assert.False(withoutAtlas.AtlasReplaced);
            Assert.True(File.Exists(store.ResolveAtlasPath(withoutAtlas.State)!));
            Assert.Equal(ValidAtlasA, await File.ReadAllBytesAsync(store.ResolveAtlasPath(withoutAtlas.State)!));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static byte[] CreateBankZip(byte[]? atlas, int faviconIndex = 0)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var csv = archive.CreateEntry("streams.csv");
            using (var stream = csv.Open())
            {
                stream.Write(Encoding.UTF8.GetBytes(
                    $"name,url,media_kind,favicon_index\nOne,https://example.test/live,AUDIO,{faviconIndex}"));
            }

            if (atlas is not null)
            {
                using var stream = archive.CreateEntry("favicon-atlas.png").Open();
                stream.Write(atlas);
            }
        }

        return buffer.ToArray();
    }

    private sealed class QueuedZipHandler(params byte[][] responses) : HttpMessageHandler
    {
        private int _index;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(responses[Math.Min(_index++, responses.Length - 1)])
            });
    }
}
