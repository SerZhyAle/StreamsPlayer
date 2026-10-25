using System.Globalization;
using System.Net;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// S14-4: a hostile `.spres` cannot make a play fire thousands of probes or persist a megabyte title. The reader
/// keeps a bounded number of access paths and bounded text, and the race has a bounded fan-out whatever it is given.
/// </summary>
public sealed class BroadcastResourceBoundsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-12T12:00:00Z", CultureInfo.InvariantCulture);

    [Theory]
    [InlineData(1, 1)]
    [InlineData(BroadcastResourceReader.MaximumAccessPaths, BroadcastResourceReader.MaximumAccessPaths)]
    [InlineData(5000, BroadcastResourceReader.MaximumAccessPaths)]
    public void Access_paths_beyond_the_cap_are_dropped_in_the_documents_order(int given, int kept)
    {
        var read = BroadcastResourceReader.Read(Document(given, "Dune", ""));

        Assert.True(read.IsAccepted);
        var ports = read.Resource!.AccessPaths.Select(path => path.Port).ToArray();
        Assert.Equal(kept, ports.Length);
        Assert.Equal(Enumerable.Range(1000, kept), ports);
    }

    [Fact]
    public void A_long_title_and_note_are_cut_and_a_short_one_is_not()
    {
        var cut = BroadcastResourceReader.Read(Document(1, new string('a', 100_000), new string('b', 100_000)));

        Assert.True(cut.IsAccepted);
        Assert.Equal(BroadcastResourceReader.MaximumResourceNameLength, cut.Resource!.ResourceName.Length);
        Assert.Equal(BroadcastResourceReader.MaximumAccessNoteLength, cut.Resource.AccessNote.Length);
        Assert.True(BroadcastChannelImport.BuildChannel(cut.Resource, Now).Title.Length <= BroadcastResourceReader.MaximumResourceNameLength);

        var name = new string('a', BroadcastResourceReader.MaximumResourceNameLength);
        var note = new string('b', BroadcastResourceReader.MaximumAccessNoteLength);
        var whole = BroadcastResourceReader.Read(Document(1, name, note));
        Assert.Equal(name, whole.Resource!.ResourceName);
        Assert.Equal(note, whole.Resource.AccessNote);
    }

    [Fact]
    public async Task The_race_never_has_more_than_the_cap_in_flight()
    {
        var urls = Addresses(24);
        var handler = new ProbeCounter(_ => HttpStatusCode.ServiceUnavailable);
        using var client = new HttpClient(handler);

        var winner = await BroadcastUrl.RaceAsync(client, urls, TimeSpan.FromSeconds(30));

        Assert.Null(winner);
        Assert.Equal(urls.Count, handler.Total);
        Assert.InRange(handler.Peak, 2, BroadcastUrl.MaximumConcurrentProbes);
    }

    [Fact]
    public async Task An_address_past_the_cap_still_wins_when_the_ones_before_it_fail()
    {
        var urls = Addresses(24);
        var handler = new ProbeCounter(uri => uri.Host == "probe20.test" ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable);
        using var client = new HttpClient(handler);

        var winner = await BroadcastUrl.RaceAsync(client, urls, TimeSpan.FromSeconds(30));

        Assert.Equal(urls[20], winner);
        Assert.InRange(handler.Peak, 1, BroadcastUrl.MaximumConcurrentProbes);
    }

    private static List<string> Addresses(int count) =>
        Enumerable.Range(0, count).Select(index => $"http://probe{index}.test/b/tok").ToList();

    private static string Document(int accessPaths, string name, string note)
    {
        var paths = string.Join(",", Enumerable.Range(0, accessPaths)
            .Select(index => "{ \"kind\": \"lan\", \"host\": \"192.168.1.100\", \"port\": " + (1000 + index) + " }"));
        return "{ \"schemaVersion\": 1, \"kind\": \"broadcast\", \"protocol\": \"http\", \"mediaKind\": \"audio\", "
            + "\"itemCount\": 1, \"resourceName\": \"" + name + "\", \"accessNote\": \"" + note + "\", "
            + "\"accessPaths\": [" + paths + "], \"path\": \"/b/tok\" }";
    }

    /// <summary>Answers every probe after a pause, as the status the test chose, and records how many ran at once.</summary>
    private sealed class ProbeCounter(Func<Uri, HttpStatusCode> answer) : HttpMessageHandler
    {
        private int _inFlight;
        private int _peak;
        private int _total;

        public int Peak => Volatile.Read(ref _peak);

        public int Total => Volatile.Read(ref _total);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _total);
            var current = Interlocked.Increment(ref _inFlight);
            int seen;
            while ((seen = Volatile.Read(ref _peak)) < current && Interlocked.CompareExchange(ref _peak, current, seen) != seen)
            {
            }

            try
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                return new HttpResponseMessage(answer(request.RequestUri!));
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }
}
