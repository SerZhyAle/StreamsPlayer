using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using StreamsPlayer.Core;
using static StreamsPlayer.Core.Tests.PublishWindowRetryTests;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0107, STREAM-BANK rule 11: a refresh or an artwork download that meets the publish window waits
/// and tries again inside the same operation, and nothing else it meets is retried.
/// </summary>
public sealed class PublishWindowFetchTests
{
    private const string Csv = "name,url,media_kind\nOne,https://example.test/live,AUDIO";

    private static readonly PublishWindowRetry Immediate = new([TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]);

    [Fact]
    public async Task Refresh_WaitsOutA404AndImportsTheRepublishedBank()
    {
        var handler = new ScriptedHandler(Status(HttpStatusCode.NotFound), Zip(BankZip(Csv)));
        var notices = new List<PublishWindowRetryNotice>();

        var result = await Refresh(handler, new CatalogState(), notices);

        Assert.Single(result.State.Channels);
        Assert.Equal(2, handler.Requests.Count);
        var notice = Assert.Single(notices);
        Assert.Equal(PublishWindowCause.NotFound, notice.Cause);
        Assert.Equal(2, notice.NextAttempt);
    }

    [Fact]
    public async Task Refresh_RetriesABodyCutShort()
    {
        var handler = new ScriptedHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ShortContent(new byte[10], 5000) },
            Zip(BankZip(Csv)));
        var notices = new List<PublishWindowRetryNotice>();

        var result = await Refresh(handler, new CatalogState(), notices);

        Assert.Single(result.State.Channels);
        Assert.Equal(PublishWindowCause.ShortRead, Assert.Single(notices).Cause);
    }

    /// <summary>HTTP was satisfied - the length it was told matches - and the ZIP is still cut off.</summary>
    [Fact]
    public async Task Refresh_RetriesATruncatedArchive()
    {
        var whole = BankZip(Csv);
        var handler = new ScriptedHandler(Zip(whole[..(whole.Length / 2)]), Zip(whole));
        var notices = new List<PublishWindowRetryNotice>();

        var result = await Refresh(handler, new CatalogState(), notices);

        Assert.Single(result.State.Channels);
        Assert.Equal(PublishWindowCause.TruncatedArchive, Assert.Single(notices).Cause);
    }

    /// <summary>
    /// The half of item H that was already held, pinned so the retry cannot erode it: when the window
    /// outlasts the schedule the refresh fails as before, and the stored catalog is exactly what it was:
    /// no empty bank, and no row retired or removed by absence handling.
    /// </summary>
    [Fact]
    public async Task Refresh_KeepsThePreviousBankWhenTheWindowOutlastsTheSchedule()
    {
        var directory = TempDirectory();
        try
        {
            var store = new StreamCatalogStore(directory);
            var first = await new StreamCatalogService(
                    new HttpClient(new ScriptedHandler(Zip(BankZip(Csv)))), store, Immediate)
                .RefreshAsync(new CatalogState());

            var missing = new ScriptedHandler(
                Status(HttpStatusCode.NotFound), Status(HttpStatusCode.NotFound),
                Status(HttpStatusCode.NotFound), Status(HttpStatusCode.NotFound));
            var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
                new StreamCatalogService(new HttpClient(missing), store, Immediate).RefreshAsync(first.State));

            Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
            Assert.Equal(Immediate.MaximumAttempts, missing.Requests.Count);
            var persisted = await store.LoadAsync();
            Assert.Equal("One", Assert.Single(persisted.Channels).Title);
            Assert.Null(persisted.Channels[0].RetiredAt);
            Assert.Equal(first.State.LastCatalogRefreshAt, persisted.LastCatalogRefreshAt);
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task Refresh_DoesNotRetryABankThatArrivedWholeAndIsWrong()
    {
        var handler = new ScriptedHandler(Zip(BankZip("name,url\n\"broken,https://example.test/live")));

        await Assert.ThrowsAsync<InvalidDataException>(() => Refresh(handler, new CatalogState(), []));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Refresh_DoesNotRetryAnArchiveAboveTheCeiling()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ShortContent([], StreamCatalogService.MaximumArchiveBytes + 1)
        });

        await Assert.ThrowsAsync<InvalidDataException>(() => Refresh(handler, new CatalogState(), []));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Refresh_DoesNotRetryAServerError()
    {
        var handler = new ScriptedHandler(Status(HttpStatusCode.InternalServerError));

        await Assert.ThrowsAsync<HttpRequestException>(() => Refresh(handler, new CatalogState(), []));

        Assert.Single(handler.Requests);
    }

    /// <summary>
    /// A retry starts from the manifest, never from the file that failed: after a publish the manifest
    /// may already describe the new build, and the pack has to be verified against that one.
    /// </summary>
    [Fact]
    public async Task Artwork_RestartsFromTheManifestWhenThePackIsMidPublish()
    {
        var coords = Encoding.UTF8.GetBytes("""{"https://example.test/live": 0}""");
        byte[] pack = [1, 2, 3, 4, 5, 6, 7, 8];
        var manifest = Manifest(coords, pack);
        var handler = new ScriptedHandler(
            Zip(manifest), Zip(coords), Status(HttpStatusCode.NotFound),
            Zip(manifest), Zip(coords), Zip(pack));
        var notices = new List<PublishWindowRetryNotice>();

        var artwork = await new ChannelPreviewArtworkService(new HttpClient(handler), Immediate)
            .DownloadAsync(null, new Collector<PublishWindowRetryNotice>(notices));

        Assert.Equal(pack, artwork.TilePack);
        Assert.Equal(
            [
                ChannelPreviewArtworkService.ManifestUrl, ChannelPreviewArtworkService.CoordsUrl,
                ChannelPreviewArtworkService.TilePackUrl, ChannelPreviewArtworkService.ManifestUrl,
                ChannelPreviewArtworkService.CoordsUrl, ChannelPreviewArtworkService.TilePackUrl
            ],
            handler.Requests);
        Assert.Equal(PublishWindowCause.NotFound, Assert.Single(notices).Cause);
    }

    /// <summary>
    /// SP-0106, STREAM-BANK item L: a manifest from a newer schema is refused before the sidecar or the
    /// pack is asked for, and is not retried - so nothing reaches the importer and no stamp can be
    /// recorded, which is what keeps the installed artwork in place.
    /// </summary>
    [Fact]
    public async Task Artwork_RefusesANewerManifestBeforeFetchingAnythingElse()
    {
        var coords = Encoding.UTF8.GetBytes("""{"https://example.test/live": 0}""");
        byte[] pack = [1, 2, 3, 4, 5, 6, 7, 8];
        var manifest = Encoding.UTF8.GetString(Manifest(coords, pack))
            .Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal);
        var handler = new ScriptedHandler(Zip(Encoding.UTF8.GetBytes(manifest)), Zip(coords), Zip(pack));
        var notices = new List<PublishWindowRetryNotice>();

        var refusal = await Assert.ThrowsAsync<UnsupportedArtworkManifestException>(() =>
            new ChannelPreviewArtworkService(new HttpClient(handler), Immediate)
                .DownloadAsync(null, new Collector<PublishWindowRetryNotice>(notices)));

        Assert.Equal(2, refusal.SchemaVersion);
        Assert.Equal([ChannelPreviewArtworkService.ManifestUrl], handler.Requests);
        Assert.Empty(notices);
    }

    private static Task<CatalogRefreshResult> Refresh(
        ScriptedHandler handler,
        CatalogState state,
        List<PublishWindowRetryNotice> notices)
    {
        var directory = TempDirectory();
        var service = new StreamCatalogService(new HttpClient(handler), new StreamCatalogStore(directory), Immediate);
        return RunAndClean(
            () => service.RefreshAsync(state, null, new Collector<PublishWindowRetryNotice>(notices)),
            directory);
    }

    private static async Task<T> RunAndClean<T>(Func<Task<T>> action, string directory)
    {
        try
        {
            return await action();
        }
        finally
        {
            Delete(directory);
        }
    }

    private static byte[] Manifest(byte[] coords, byte[] pack) => Encoding.UTF8.GetBytes($$"""
        {"schemaVersion": 1, "sets": {"channelPreview": {"stamp": "s1", "files": [
          {"name": "{{ChannelPreviewArtworkService.TilePackFile}}", "size": {{pack.Length}}, "sha256": "{{Hash(pack)}}"},
          {"name": "{{ChannelPreviewArtworkService.CoordsFile}}", "size": {{coords.Length}}, "sha256": "{{Hash(coords)}}"}
        ] } } }
        """);

    private static string Hash(byte[] payload) => Convert.ToHexStringLower(SHA256.HashData(payload));

    private static Func<HttpRequestMessage, HttpResponseMessage> Status(HttpStatusCode status) =>
        _ => new HttpResponseMessage(status);

    private static Func<HttpRequestMessage, HttpResponseMessage> Zip(byte[] body) =>
        _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static string TempDirectory() =>
        Path.Combine(Path.GetTempPath(), $"StreamsPlayer.Tests.{Guid.NewGuid():N}");

    private static void Delete(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Answers each request with the next scripted response and records what was asked for.</summary>
    private sealed class ScriptedHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] script)
        : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _script = new(script);

        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(_script.Dequeue()(request));
        }
    }
}
