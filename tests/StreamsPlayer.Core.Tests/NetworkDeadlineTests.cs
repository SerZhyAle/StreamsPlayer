using System.Diagnostics;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0129: every request gives up within a bounded time when the other side stops answering - before the
/// head and while reading the body - and never reads more than its purpose needs. Real sockets on
/// loopback, so the stall is the one the socket handler actually sees.
/// </summary>
public sealed class NetworkDeadlineTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(1);

    // Generous against scheduler noise, yet far below anything that would pass without the bound: every
    // stalling server here holds the socket open until the test disposes it.
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task SendForHeaders_FailsWithinTheHeaderBoundWhenNoHeadArrives()
    {
        await using var server = LoopbackHttpServer.SilentBeforeHeaders();
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var clock = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(
            () => HttpDownload.GetForHeadersAsync(client, new Uri(server.Url), Bound, CancellationToken.None));

        AssertWithinLimit(clock);
    }

    [Fact]
    public async Task SendForHeaders_ReportsTheUsersCancelAsACancellation()
    {
        await using var server = LoopbackHttpServer.SilentBeforeHeaders();
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var user = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => HttpDownload.GetForHeadersAsync(client, new Uri(server.Url), Limit, user.Token));

        Assert.IsNotType<TimeoutException>(cancelled);
    }

    [Fact]
    public async Task BodyRead_FailsWithinTheInactivityBoundWhenTheBodyStalls()
    {
        await using var server = LoopbackHttpServer.StallAfterHeaders("application/zip", declaredLength: 1_000_000, firstBytes: 64);
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var response = await HttpDownload.GetForHeadersAsync(client, new Uri(server.Url), Limit, CancellationToken.None);
        var clock = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(
            () => HttpDownload.ReadAllBytesAsync(response, null, ceilingBytes: null, Bound, CancellationToken.None));

        AssertWithinLimit(clock);
    }

    [Fact]
    public async Task PlaylistImport_FailsWithinTheHeaderBoundWhenNoHeadArrives()
    {
        await using var server = LoopbackHttpServer.SilentBeforeHeaders();
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var clock = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(() => Import(client).FetchAsync(server.Url));

        AssertWithinLimit(clock);
    }

    [Fact]
    public async Task PlaylistImport_FailsWithinTheInactivityBoundWhenTheBodyStalls()
    {
        await using var server = LoopbackHttpServer.StallAfterHeaders("text/plain", declaredLength: 100_000, firstBytes: 64);
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var clock = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(() => Import(client).FetchAsync(server.Url));

        AssertWithinLimit(clock);
    }

    [Fact]
    public async Task PlaylistImport_RefusesAnUndeclaredBodyAtTheCeiling()
    {
        var oversize = (int)M3uImportService.MaximumPlaylistBytes + 64 * 1024;
        await using var server = LoopbackHttpServer.Body("text/plain", oversize);
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        await Assert.ThrowsAsync<InvalidDataException>(() => Import(client).FetchAsync(server.Url));
    }

    /// <summary>
    /// The server stalls after the head, so a client that started reading the body would end in the
    /// inactivity bound with a <see cref="TimeoutException"/>. The refusal type proves the body was never read.
    /// </summary>
    [Theory]
    [InlineData("audio/mpeg", null)]
    [InlineData("video/mp4", null)]
    [InlineData("application/octet-stream", "icy-name: Station\r\n")]
    public async Task PlaylistImport_RefusesMediaFromTheHeadBeforeReadingTheBody(string contentType, string? extraHeaders)
    {
        await using var server = extraHeaders is null
            ? LoopbackHttpServer.StallAfterHeaders(contentType, declaredLength: 100_000_000, firstBytes: 64)
            : LoopbackHttpServer.Body(contentType, bodyBytes: 64, extraHeaders);
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        await Assert.ThrowsAsync<InvalidDataException>(() => Import(client).FetchAsync(server.Url));
    }

    [Theory]
    [InlineData("audio/x-mpegurl")]
    [InlineData("application/vnd.apple.mpegurl")]
    [InlineData("audio/x-scpls")]
    [InlineData("text/plain")]
    public async Task PlaylistImport_AcceptsPlaylistTypes(string contentType)
    {
        const string playlist = "#EXTM3U\n#EXTINF:-1,Station\nhttps://radio.test/live\n";
        await using var server = LoopbackHttpServer.Document(contentType, playlist);
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        Assert.Equal(playlist, await Import(client).FetchAsync(server.Url));
    }

    [Fact]
    public async Task IcecastStatus_GivesUpWithinTheRequestDeadlineWhenTheBodyStalls()
    {
        await using var server = LoopbackHttpServer.StallAfterHeaders("application/json", declaredLength: 4096, firstBytes: 16);
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var reader = new IcecastStatusReader(client, Bound);
        var clock = Stopwatch.StartNew();

        var outcome = await reader.ReadAsync(new Uri(server.Url), new Progress<string?>(), CancellationToken.None);

        Assert.Equal(IcecastStatusReadOutcome.EndpointUnavailable, outcome);
        AssertWithinLimit(clock);
    }

    [Fact]
    public async Task IcecastStatus_GivesUpWithinTheRequestDeadlineWhenNoHeadArrives()
    {
        await using var server = LoopbackHttpServer.SilentBeforeHeaders();
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var reader = new IcecastStatusReader(client, Bound);
        var clock = Stopwatch.StartNew();

        var outcome = await reader.ReadAsync(new Uri(server.Url), new Progress<string?>(), CancellationToken.None);

        Assert.Equal(IcecastStatusReadOutcome.EndpointUnavailable, outcome);
        AssertWithinLimit(clock);
    }

    private static M3uImportService Import(HttpClient client) => new(client, Bound, Bound);

    private static void AssertWithinLimit(Stopwatch clock) =>
        Assert.True(clock.Elapsed < Limit, $"gave up after {clock.Elapsed}, limit {Limit}");
}
