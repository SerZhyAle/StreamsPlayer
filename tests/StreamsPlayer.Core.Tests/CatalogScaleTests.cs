using System.Diagnostics;
using System.Globalization;
using StreamsPlayer.Core;
using Xunit.Abstractions;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0171: work the UI thread does per list rebuild and per 30-second TV tick must follow the number of
/// distinct rubrics and of channels bound to a guide - never the size of the catalog. Every case runs at
/// the ticket's scale, 20,000 channels; the bounds are counts of calls, which do not flake, with the
/// wall-clock figures printed beside them as the recorded measurement.
/// </summary>
public sealed class CatalogScaleTests(ITestOutputHelper output)
{
    private const int Channels = 20_000;
    private const int Rubrics = 60;
    private const int Bound = 50;
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Stands in for the App's label comparer: a label lookup plus a culture comparison per call.</summary>
    private sealed class CountingLabelComparer : IComparer<string>
    {
        private readonly Dictionary<string, string> _labels;
        private readonly StringComparer _culture = StringComparer.Create(CultureInfo.InvariantCulture, ignoreCase: true);

        public CountingLabelComparer(IEnumerable<string> topics) =>
            _labels = topics.Distinct().ToDictionary(topic => topic, topic => "label " + topic);

        public int Calls { get; private set; }

        public int Compare(string? left, string? right)
        {
            Calls++;
            var leftGeneral = left == "general";
            var rightGeneral = right == "general";
            if (leftGeneral != rightGeneral)
            {
                return leftGeneral ? 1 : -1;
            }

            return _culture.Compare(_labels[left ?? string.Empty], _labels[right ?? string.Empty]);
        }
    }

    private static string[] Topics(int rows) =>
        Enumerable.Range(0, rows).Select(index => index % 7 == 0 ? "general" : $"topic-{index * 31 % Rubrics:D2}").ToArray();

    [Fact]
    public void TopicRanksOrderRowsExactlyAsTheComparerWould()
    {
        var topics = Topics(2_000);
        var comparer = new CountingLabelComparer(topics);

        var byComparer = Enumerable.Range(0, topics.Length).OrderBy(index => topics[index], comparer).ToList();
        var ranks = DistinctKeyRanking.Build(topics, comparer);
        var byRank = Enumerable.Range(0, topics.Length).OrderBy(index => ranks[topics[index]]).ToList();

        Assert.Equal(byComparer, byRank);
        Assert.Equal("general", topics[byRank[^1]]);
    }

    [Fact]
    public void TopicsTheComparerCallsEqualShareARankSoTiesKeepTheirOrder()
    {
        var ranks = DistinctKeyRanking.Build(["News", "news", "Jazz"], StringComparer.OrdinalIgnoreCase);

        Assert.Equal(ranks["News"], ranks["news"]);
        Assert.True(ranks["Jazz"] < ranks["News"]);
    }

    [Fact]
    public void AComparerIsRunOverDistinctRubricsNotOverRows()
    {
        var topics = Topics(Channels);
        var naive = new CountingLabelComparer(topics);
        var naiveTimer = Stopwatch.StartNew();
        _ = Enumerable.Range(0, topics.Length).OrderBy(_ => 0).ThenBy(index => topics[index], naive).ToList();
        naiveTimer.Stop();

        var ranked = new CountingLabelComparer(topics);
        var rankedTimer = Stopwatch.StartNew();
        var ranks = DistinctKeyRanking.Build(topics, ranked);
        _ = Enumerable.Range(0, topics.Length).OrderBy(_ => 0).ThenBy(index => ranks[topics[index]]).ToList();
        rankedTimer.Stop();

        output.WriteLine($"SP-0171 topic sort, {Channels} rows, {ranks.Count} rubrics (label lookup stubbed by a dictionary read):");
        output.WriteLine($"  before: comparer calls={naive.Calls}, {naiveTimer.ElapsedMilliseconds} ms");
        output.WriteLine($"  after:  comparer calls={ranked.Calls}, {rankedTimer.ElapsedMilliseconds} ms");

        Assert.True(ranked.Calls <= ranks.Count * 10, $"ranking called the comparer {ranked.Calls} times for {ranks.Count} rubrics");
        Assert.True(naive.Calls > ranked.Calls * 100);
        Assert.True(rankedTimer.ElapsedMilliseconds < 500, $"{rankedTimer.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void TheComparerCostDoesNotGrowWithTheRowCount()
    {
        int CallsFor(int rows)
        {
            var topics = Topics(rows);
            var comparer = new CountingLabelComparer(topics);
            _ = DistinctKeyRanking.Build(topics, comparer);
            return comparer.Calls;
        }

        Assert.Equal(CallsFor(2_000), CallsFor(Channels));
    }

    private sealed class Row(string url)
    {
        public string Url { get; } = url;
        public string? Shown { get; set; }
        public int Writes { get; set; }
    }

    private static (TvScheduleDocument Document, List<StreamChannel> Catalog) GuideWithBoundChannels()
    {
        var guide = Enumerable.Range(0, Bound)
            .Select(index => new TvScheduleChannel(
                $"guide.{index}",
                [$"Guide Channel {index}"],
                [new TvProgramme(Now.AddMinutes(-30), Now.AddMinutes(30), $"Show {index}"),
                 new TvProgramme(Now.AddMinutes(30), Now.AddMinutes(90), $"Later {index}")]))
            .ToList();
        var catalog = Enumerable.Range(0, Channels)
            .Select(index => new StreamChannel
            {
                Id = Guid.NewGuid(),
                Url = $"https://Example.test/live/{index}.m3u8",
                Title = index < Bound ? $"Guide Channel {index}" : $"Filler {index}",
                MediaKind = MediaKind.Video,
                SourceOrigin = SourceOrigin.Catalog,
                AddedAt = Now
            })
            .ToList();
        return (new TvScheduleDocument(TvScheduleDocument.CurrentSchemaVersion, "https://guide.test/g.xml", Now, guide), catalog);
    }

    [Fact]
    public void ATickWithTwentyThousandRowsAndFiftyBoundParsesNoAddress()
    {
        var (document, catalog) = GuideWithBoundChannels();
        var parses = 0;
        string Counted(string url)
        {
            parses++;
            return CatalogUrlIdentity.Normalize(url);
        }

        var buildTimer = Stopwatch.StartNew();
        var index = TvScheduleIndex.Build(document, [], catalog, Counted);
        buildTimer.Stop();
        var parsesByBuild = parses;

        var rows = catalog.Select(channel => new Row(channel.Url)).ToList();
        var lines = new TvScheduleLines<Row>();
        void Show(Row row, string? title)
        {
            row.Writes++;
            row.Shown = title;
        }

        parses = 0;
        var reconcileTimer = Stopwatch.StartNew();
        lines.Reconcile(rows, row => row.Url, index, Now, Show);
        reconcileTimer.Stop();
        var parsesByReconcile = parses;

        foreach (var row in rows)
        {
            row.Writes = 0;
        }

        parses = 0;
        var tickTimer = Stopwatch.StartNew();
        lines.Tick(Now.AddMinutes(45), Show);
        tickTimer.Stop();

        output.WriteLine($"SP-0171 TV, {Channels} rows, {Bound} bound:");
        output.WriteLine($"  index build: parser calls={parsesByBuild}, {buildTimer.ElapsedMilliseconds} ms");
        output.WriteLine($"  reconcile (catalog change): parser calls={parsesByReconcile}, {reconcileTimer.ElapsedMilliseconds} ms");
        output.WriteLine($"  tick: parser calls={parses}, rows written={rows.Sum(row => row.Writes)}, {tickTimer.ElapsedMilliseconds} ms");

        Assert.Equal(Bound, lines.BoundCount);
        Assert.True(parses <= Bound, $"the tick parsed {parses} addresses");
        Assert.Equal(0, parses);
        Assert.Equal(Bound, rows.Sum(row => row.Writes));
        Assert.Equal("Later 0", rows[0].Shown);
        Assert.Null(rows[Bound].Shown);
        // Building the index resolves only what matched; 19,950 fillers are not parsed for a binding.
        Assert.Equal(Bound, parsesByBuild);
    }

    [Fact]
    public void AttachAndForgetKeepTheBoundSetInStepWithTheList()
    {
        var (document, catalog) = GuideWithBoundChannels();
        var index = TvScheduleIndex.Build(document, [], catalog);
        var lines = new TvScheduleLines<Row>();
        void Show(Row row, string? title) => row.Shown = title;

        var bound = new Row(catalog[3].Url);
        var other = new Row(catalog[Bound + 5].Url);
        lines.Attach(bound, bound.Url, index, Now, Show);
        lines.Attach(other, other.Url, index, Now, Show);

        Assert.Equal("Show 3", bound.Shown);
        Assert.Null(other.Shown);
        Assert.Equal(1, lines.BoundCount);

        lines.Forget(bound);
        Assert.Equal(0, lines.BoundCount);
    }

    [Fact]
    public void ReconcileClearsARowWhoseGuideWentAway()
    {
        var (document, catalog) = GuideWithBoundChannels();
        var lines = new TvScheduleLines<Row>();
        void Show(Row row, string? title) => row.Shown = title;
        var row = new Row(catalog[0].Url);

        lines.Reconcile([row], item => item.Url, TvScheduleIndex.Build(document, [], catalog), Now, Show);
        Assert.Equal("Show 0", row.Shown);

        lines.Reconcile([row], item => item.Url, TvScheduleIndex.Empty, Now, Show);
        Assert.Null(row.Shown);
        Assert.Equal(0, lines.BoundCount);

        var rebound = TvScheduleIndex.Build(document, [new TvScheduleBinding(catalog[0].Url, null)], catalog);
        lines.Reconcile([row], item => item.Url, rebound, Now, Show);
        Assert.Null(row.Shown);
    }
}
