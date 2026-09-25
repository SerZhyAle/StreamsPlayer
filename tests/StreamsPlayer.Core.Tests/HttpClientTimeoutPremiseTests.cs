using System.Diagnostics;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0129 requirement 1: what <see cref="HttpClient.Timeout"/> actually bounds. Code in this product used
/// to lean on the answer in both directions at once - comments saying it "severs the body read even under
/// ResponseHeadersRead", and a general client whose 30 s timeout was trusted to cut off a slow body. This
/// settles it on a real socket so every other deadline can be written against a fact.
/// </summary>
public sealed class HttpClientTimeoutPremiseTests
{
    private static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Observation = TimeSpan.FromSeconds(4);

    [Fact]
    public async Task ClientTimeout_BoundsTheWaitForHeaders()
    {
        await using var server = LoopbackHttpServer.SilentBeforeHeaders();
        using var client = new HttpClient { Timeout = ClientTimeout };
        var clock = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => client.GetAsync(server.Url, HttpCompletionOption.ResponseHeadersRead));

        Assert.True(clock.Elapsed < Observation, $"took {clock.Elapsed}");
    }

    /// <summary>
    /// The answer the product relies on: once <c>SendAsync</c> has returned the headers, the client's
    /// timeout no longer applies. A stalled body read runs until the caller's own token ends it - here the
    /// observation window, four times the client timeout - so a body is bounded only by a token the
    /// caller passes to the read.
    /// </summary>
    [Fact]
    public async Task ClientTimeout_DoesNotBoundABodyReadAfterHeaders()
    {
        await using var server = LoopbackHttpServer.StallAfterHeaders("application/octet-stream", declaredLength: 4096, firstBytes: 16);
        using var client = new HttpClient { Timeout = ClientTimeout };
        using var response = await client.GetAsync(server.Url, HttpCompletionOption.ResponseHeadersRead);
        await using var body = await response.Content.ReadAsStreamAsync();
        using var observation = new CancellationTokenSource(Observation);
        var buffer = new byte[4096];
        var clock = Stopwatch.StartNew();

        var stalled = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            while (await body.ReadAsync(buffer, observation.Token) > 0)
            {
            }
        });

        Assert.Equal(observation.Token, stalled.CancellationToken);
        Assert.True(clock.Elapsed >= Observation - TimeSpan.FromMilliseconds(250), $"ended after {clock.Elapsed}");
    }
}
