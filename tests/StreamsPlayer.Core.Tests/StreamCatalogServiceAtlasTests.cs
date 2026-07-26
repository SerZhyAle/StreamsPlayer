using System.IO.Compression;
using System.Net;
using System.Text;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class StreamCatalogServiceAtlasTests
{
    [Fact]
    public async Task Refresh_KeepsInstalledAtlasWhenTheBankCarriesNone()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"StreamsPlayer.Tests.{Guid.NewGuid():N}");
        try
        {
            var store = new StreamCatalogStore(directory);
            var handler = new QueuedZipHandler(
                CreateBankZip(atlas: [1, 2, 3]),
                CreateBankZip(atlas: null));
            using var httpClient = new HttpClient(handler);
            var service = new StreamCatalogService(httpClient, store);

            var withAtlas = await service.RefreshAsync(new CatalogState());
            var atlasPath = store.ResolveAtlasPath(withAtlas.State);
            Assert.NotNull(atlasPath);
            Assert.True(File.Exists(atlasPath));

            var withoutAtlas = await service.RefreshAsync(withAtlas.State);

            Assert.Equal(withAtlas.State.AtlasFileName, withoutAtlas.State.AtlasFileName);
            Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(store.ResolveAtlasPath(withoutAtlas.State)!));
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
                CreateBankZip(atlas: [1, 2, 3]),
                CreateBankZip(atlas: [4, 5]));
            using var httpClient = new HttpClient(handler);
            var service = new StreamCatalogService(httpClient, store);

            var first = await service.RefreshAsync(new CatalogState());
            var second = await service.RefreshAsync(first.State);

            Assert.NotEqual(first.State.AtlasFileName, second.State.AtlasFileName);
            Assert.Equal([4, 5], await File.ReadAllBytesAsync(store.ResolveAtlasPath(second.State)!));
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

    private static byte[] CreateBankZip(byte[]? atlas)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var csv = archive.CreateEntry("streams.csv");
            using (var stream = csv.Open())
            {
                stream.Write(Encoding.UTF8.GetBytes(
                    "name,url,media_kind,favicon_index\nOne,https://example.test/live,AUDIO,0"));
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
