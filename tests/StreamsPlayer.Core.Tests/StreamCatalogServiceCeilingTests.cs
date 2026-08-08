using System.IO.Compression;
using System.Net;
using System.Text;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0069: the catalog archive is the one always-downloaded payload that had no size bound, while the
/// favicon atlas and the channel-preview sheet both had one. These tests pin the bound and, just as
/// importantly, pin what happens to the user's catalog when it is exceeded.
/// </summary>
public sealed class StreamCatalogServiceCeilingTests
{
    [Fact]
    public async Task Refresh_RefusesAnArchiveDeclaredAboveTheCeiling()
    {
        var directory = TempDirectory();
        try
        {
            var store = new StreamCatalogStore(directory);
            using var httpClient = new HttpClient(new OversizeHandler(CreateBankZip()));
            var service = new StreamCatalogService(httpClient, store);

            await Assert.ThrowsAsync<InvalidDataException>(() => service.RefreshAsync(new CatalogState()));
        }
        finally
        {
            Delete(directory);
        }
    }

    /// <summary>
    /// The refusal must land before <c>SaveAsync</c>, so a rejected bank cannot half-apply. This is the
    /// criterion the ticket words as "the application does not claim a false success": the user keeps the
    /// catalog they had.
    /// </summary>
    [Fact]
    public async Task Refresh_LeavesTheStoredCatalogIntactWhenTheArchiveIsRefused()
    {
        var directory = TempDirectory();
        try
        {
            var store = new StreamCatalogStore(directory);
            using var good = new HttpClient(new QueuedZipHandler(CreateBankZip()));
            var accepted = await new StreamCatalogService(good, store).RefreshAsync(new CatalogState());
            Assert.Single(accepted.State.Channels);

            using var oversize = new HttpClient(new OversizeHandler(CreateBankZip("Replacement")));
            await Assert.ThrowsAsync<InvalidDataException>(
                () => new StreamCatalogService(oversize, store).RefreshAsync(accepted.State));

            var persisted = await store.LoadAsync();
            Assert.Equal(accepted.State.Channels.Count, persisted.Channels.Count);
            Assert.Equal("One", persisted.Channels[0].Title);
            Assert.Equal(accepted.State.LastCatalogRefreshAt, persisted.LastCatalogRefreshAt);
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task Refresh_AcceptsAnArchiveUnderTheCeiling()
    {
        var directory = TempDirectory();
        try
        {
            var store = new StreamCatalogStore(directory);
            using var httpClient = new HttpClient(new QueuedZipHandler(CreateBankZip()));

            var result = await new StreamCatalogService(httpClient, store).RefreshAsync(new CatalogState());

            Assert.Single(result.State.Channels);
        }
        finally
        {
            Delete(directory);
        }
    }

    /// <summary>The ceiling is headroom over the shipped bank, not a fit to it - see the constant's remarks.</summary>
    [Fact]
    public void Ceiling_LeavesRoomAboveTheAtlasBoundItMustContain() =>
        Assert.True(StreamCatalogService.MaximumArchiveBytes > StreamBankReader.MaximumAtlasBytes);

    private static string TempDirectory() =>
        Path.Combine(Path.GetTempPath(), $"StreamsPlayer.Tests.{Guid.NewGuid():N}");

    private static void Delete(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static byte[] CreateBankZip(string title = "One")
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var stream = archive.CreateEntry("streams.csv").Open();
            stream.Write(Encoding.UTF8.GetBytes(
                $"name,url,media_kind,favicon_index\n{title},https://example.test/live,AUDIO,0"));
        }

        return buffer.ToArray();
    }

    private sealed class QueuedZipHandler(byte[] response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(response)
            });
    }

    /// <summary>
    /// Declares a length above the ceiling while carrying a perfectly valid bank. That combination is
    /// what makes the test meaningful: without the ceiling the refresh *succeeds* and replaces the
    /// catalog, so the test fails against the pre-fix code rather than passing for the wrong reason.
    /// </summary>
    private sealed class OversizeHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new OverDeclaringContent(body)
            });
    }

    private sealed class OverDeclaringContent(byte[] body) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(body, 0, body.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = StreamCatalogService.MaximumArchiveBytes + 1;
            return true;
        }
    }
}
