using System.Diagnostics;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0129: every request gives up within a bounded time when the other side stops answering - before the
/// head and while reading the body - and never reads more than its purpose needs. Real sockets on
/// loopback, so the stall is the one the socket handler actually sees.
/// </summary>
/// <remarks>
/// Three numbers, three jobs. <see cref="Bound"/> is the product bound a stalling server has to trip; it is short so the
/// test is quick, and it is only ever given to a call whose server stalls. <see cref="Patient"/> is the bound for a call
/// whose server answers or whose end is the caller's own cancel: far above what a loaded machine needs, so the product's
/// bound never decides a test about something else (a one-second bound did, on a CI runner that took seconds to start
/// the loopback server). <see cref="Limit"/> is how long a stalled call may take before the test calls its bound broken:
/// every stalling server holds the socket open until the test disposes it, so a call without a working bound never ends,
/// and <see cref="WithinLimit"/> turns that hang into a failure. A mutation that removes the product's bound, or replaces
/// it with a minute, fails every stall test here; one that stretches it to a few seconds is not what this file measures.
/// </remarks>
public sealed class NetworkDeadlineTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan Patient = TimeSpan.FromSeconds(30);

    // Seven seconds was measured for a one-second bound on a two-core runner running three test projects at once.
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task SendForHeaders_FailsWithinTheHeaderBoundWhenNoHeadArrives()
    {
        await using var server = LoopbackHttpServer.SilentBeforeHeaders();
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var clock = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(
            () => WithinLimit(HttpDownload.GetForHeadersAsync(client, new Uri(server.Url), Bound, CancellationToken.None)));

        AssertWithinLimit(clock);
    }

    [Fact]
    public async Task SendForHeaders_ReportsTheUsersCancelAsACancellation()
    {
        await using var server = LoopbackHttpServer.SilentBeforeHeaders();
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var user = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        // The header bound is far beyond the user's cancel, so only the cancel can end this call.
        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => HttpDownload.GetForHeadersAsync(client, new Uri(server.Url), Patient, user.Token));

        Assert.IsNotType<TimeoutException>(cancelled);
    }

    [Fact]
    public async Task BodyRead_FailsWithinTheInactivityBoundWhenTheBodyStalls()
    {
        await using var server = LoopbackHttpServer.StallAfterHeaders("application/zip", declaredLength: 1_000_000, firstBytes: 64);
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var response = await HttpDownload.GetForHeadersAsync(client, new Uri(server.Url), Patient, CancellationToken.None);
        var clock = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(
            () => WithinLimit(HttpDownload.ReadAllBytesAsync(response, null, ceilingBytes: null, Bound, CancellationToken.None)));

        AssertWithinLimit(clock);
    }

    [Fact]
    public async Task PlaylistImport_FailsWithinTheHeaderBoundWhenNoHeadArrives()
    {
        await using var server = LoopbackHttpServer.SilentBeforeHeaders();
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var clock = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(
            () => WithinLimit(Import(client, headerBound: Bound).FetchAsync(server.Url)));

        AssertWithinLimit(clock);
    }

    [Fact]
    public async Task PlaylistImport_FailsWithinTheInactivityBoundWhenTheBodyStalls()
    {
        await using var server = LoopbackHttpServer.StallAfterHeaders("text/plain", declaredLength: 100_000, firstBytes: 64);
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var clock = Stopwatch.StartNew();

        // The head is given the patient bound, so the timeout that ends this call can only be the silence of the body.
        await Assert.ThrowsAsync<TimeoutException>(
            () => WithinLimit(Import(client, idleBound: Bound).FetchAsync(server.Url)));

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

        await Assert.ThrowsAsync<InvalidDataException>(() => Import(client, idleBound: Bound).FetchAsync(server.Url));
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

        var result = await WithinLimit(reader.ReadAsync(new Uri(server.Url), new Progress<string?>(), CancellationToken.None));

        Assert.Equal(IcecastStatusReadOutcome.EndpointUnavailable, result.Outcome);
        Assert.False(result.TitlesReported);
        AssertWithinLimit(clock);
    }

    [Fact]
    public async Task IcecastStatus_GivesUpWithinTheRequestDeadlineWhenNoHeadArrives()
    {
        await using var server = LoopbackHttpServer.SilentBeforeHeaders();
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var reader = new IcecastStatusReader(client, Bound);
        var clock = Stopwatch.StartNew();

        var result = await WithinLimit(reader.ReadAsync(new Uri(server.Url), new Progress<string?>(), CancellationToken.None));

        Assert.Equal(IcecastStatusReadOutcome.EndpointUnavailable, result.Outcome);
        Assert.False(result.TitlesReported);
        AssertWithinLimit(clock);
    }

    /// <summary>
    /// The bound a stalled part is given is the short one; the part the server answers promptly is given the patient one,
    /// so a slow start of the loopback server is never mistaken for the stall under test.
    /// </summary>
    private static M3uImportService Import(HttpClient client, TimeSpan? headerBound = null, TimeSpan? idleBound = null) =>
        new(client, headerBound ?? Patient, idleBound ?? Patient);

    private static Task WithinLimit(Task call) => call.WaitAsync(Limit);

    private static Task<T> WithinLimit<T>(Task<T> call) => call.WaitAsync(Limit);

    private static void AssertWithinLimit(Stopwatch clock) =>
        Assert.True(clock.Elapsed < Limit, $"gave up after {clock.Elapsed}, limit {Limit}");
}
