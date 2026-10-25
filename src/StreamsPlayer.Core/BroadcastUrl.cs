using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace StreamsPlayer.Core;

/// <summary>
/// URL composition and the access-path race of a broadcast resource (`BROADCAST-RESOURCE` contract,
/// rules 1 and 2). The URL is composed, never transmitted: <c>http://{host}:{port}{path}</c>, with an
/// IPv6 host bracketed. Ordering is advice; racing is the contract - the importer that plays
/// <c>accessPaths[0]</c> fails the day the PC moves to a different subnet.
/// </summary>
public static class BroadcastUrl
{
    /// <summary>
    /// Composes one address: <c>http://{host}:{port}{path}</c>. A host that carries a colon (IPv6) is
    /// bracketed; a host already bracketed is left alone. The path is appended verbatim - it is the
    /// same on every access path by contract.
    /// </summary>
    public static string Compose(string host, int port, string path)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("The access path has no host.", nameof(host));
        }

        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        if (string.IsNullOrEmpty(path) || path[0] != '/')
        {
            throw new ArgumentException("The resource path must be absolute.", nameof(path));
        }

        var bracketed = host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;
        return $"http://{bracketed}:{port}{path}";
    }

    /// <summary>
    /// The composed addresses of a document's access paths, in the document's order (lan, then ipv6,
    /// then portforward - the producer's advice). Unknown kinds were already dropped by the reader.
    /// </summary>
    public static IReadOnlyList<string> ComposeAll(BroadcastResource resource) =>
        resource.AccessPaths.Select(path => Compose(path.Host, path.Port, resource.Path)).ToArray();

    /// <summary>
    /// The stored addresses of a channel row: its own URL first (it is the last race's winner), then the
    /// alternates. A row without alternates answers with its one URL.
    /// </summary>
    public static IReadOnlyList<string> CandidatesOf(StreamChannel channel)
    {
        var candidates = new List<string> { channel.Url };
        if (channel.AlternateUrls is { } alternates)
        {
            candidates.AddRange(alternates.Where(url => !string.Equals(url, channel.Url, StringComparison.Ordinal)));
        }

        return candidates;
    }

    /// <summary>
    /// The per-request deadline of one race. Short on purpose: it is a pre-playback probe, not a
    /// playback attempt - a broadcast whose every address is silent this second is one the playback
    /// machinery cannot open either.
    /// </summary>
    public static readonly TimeSpan RaceDeadline = TimeSpan.FromSeconds(3);

    /// <summary>
    /// S14-4: the most probes in flight at once. A document is read down to a few addresses, but a row persisted
    /// before that bound can still carry thousands, and every play would otherwise open that many connections.
    /// </summary>
    public const int MaximumConcurrentProbes = BroadcastResourceReader.MaximumAccessPaths;

    /// <summary>
    /// Races the addresses and returns the first that answers with a body-bearing success (200/206): a
    /// <c>HEAD</c>, falling back to a one-byte <c>Range</c> <c>GET</c> for the servers that dislike
    /// <c>HEAD</c>. Up to <see cref="MaximumConcurrentProbes"/> are probed in parallel, in order; each one
    /// that fails frees its place for the next address. Null when nothing answered within
    /// <paramref name="deadline"/> - the caller owns the sentence for that case.
    /// </summary>
    /// <remarks>
    /// Never trusts the order: the first answer wins, whichever address produced it. The gateway of the
    /// contract answers every one of these shapes (196 section 3.1 makes <c>HEAD</c> mandatory), which
    /// is what lets a moved PC be found on another stored address.
    /// </remarks>
    public static async Task<string?> RaceAsync(
        HttpClient client,
        IReadOnlyList<string> urls,
        TimeSpan? deadline = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (urls.Count == 0)
        {
            return null;
        }

        using var race = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        race.CancelAfter(deadline ?? RaceDeadline);

        var attempts = new List<Task<string?>>(Math.Min(urls.Count, MaximumConcurrentProbes));
        var next = 0;
        while (next < urls.Count && attempts.Count < MaximumConcurrentProbes)
        {
            attempts.Add(ProbeAsync(client, urls[next++], race.Token));
        }

        while (attempts.Count > 0)
        {
            var answered = await Task.WhenAny(attempts).ConfigureAwait(false);
            attempts.Remove(answered);
            var winner = await answered.ConfigureAwait(false);
            if (winner is not null)
            {
                race.Cancel();
                return winner;
            }

            if (race.IsCancellationRequested)
            {
                return null;
            }

            if (next < urls.Count)
            {
                attempts.Add(ProbeAsync(client, urls[next++], race.Token));
            }
        }

        return null;
    }

    /// <summary>
    /// One address's probe: <c>HEAD</c> first; a server that answers 405 (or 501) to it gets the
    /// one-byte <c>Range</c> <c>GET</c> instead, which is the other shape the contract requires. A 404
    /// is an answer too - the broadcast is dead, and the caller turns it into the right sentence; the
    /// probe reports it as "not this address" and lets the race continue on the other addresses, which
    /// is exactly what a race is for (an address that moved subnets answers nothing at all).
    /// </summary>
    private static async Task<string?> ProbeAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, url);
            using var headResponse = await client.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (IsUsable(headResponse.StatusCode))
            {
                return url;
            }

            if (headResponse.StatusCode is not (HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented))
            {
                return null;
            }

            using var ranged = new HttpRequestMessage(HttpMethod.Get, url);
            ranged.Headers.Range = new RangeHeaderValue(0, 0);
            using var rangedResponse = await client.SendAsync(ranged, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            return IsUsable(rangedResponse.StatusCode) ? url : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static bool IsUsable(HttpStatusCode status) =>
        status is HttpStatusCode.OK or HttpStatusCode.PartialContent;
}
