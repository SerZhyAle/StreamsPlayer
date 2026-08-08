using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0071 amendment: the record that survives a player window. Every case here is a way the record can
/// be wrong in the user's favour or against it - a stale opinion capping a source that recovered, or a
/// forgotten one paying for the same failed probe on every launch.
/// <para>SP-0076 added a second fact to the same record, on its own shorter lifetime: the ceiling a new
/// session may open at. The cases below keep the two apart deliberately - a record can be current enough
/// to seed a probe wait and too old to cap anything.</para>
/// </summary>
public sealed class QualityMemoryTests
{
    private const string Url = "https://streaming.thestream.cyou/live/210.m3u8";
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 14, 0, 0, TimeSpan.Zero);
    private static readonly StreamQualityRung Middle = new(796_000, 640, 360);

    private static ChannelQualityMemory Entry(string url, DateTimeOffset at, params (int Bandwidth, int Failures)[] rungs) =>
        new(url, at, [.. rungs.Select(rung => new QualityRungMemory(rung.Bandwidth, rung.Failures))]);

    private static ChannelQualityMemory Capped(
        DateTimeOffset at,
        StreamQualityRung? ceiling,
        params (int Bandwidth, int Failures)[] rungs) =>
        Entry(Url, at, rungs) with { Ceiling = ceiling };

    [Fact]
    public void AnUnknownSource_RecallsNoEvidence()
    {
        var recalled = QualityMemory.Recall([Entry("https://other/live.m3u8", Now, (2_096_000, 3))], Url, Now);

        Assert.Empty(recalled.Failures);
        Assert.Null(recalled.Ceiling);
        Assert.Equal(QualityCeilingRecall.NoRecord, recalled.CeilingRecall);
    }

    [Fact]
    public void AKnownSource_RecallsItsFailuresByBandwidth()
    {
        var recalled = QualityMemory.Recall([Entry(Url, Now, (2_096_000, 4), (796_000, 1))], Url, Now);

        Assert.Equal(4, recalled.Failures[2_096_000]);
        Assert.Equal(1, recalled.Failures[796_000]);
    }

    // The identity a refresh and a hidden channel already use, so a host cased differently in the catalog
    // than in the playlist is the same source here as it is everywhere else.
    [Fact]
    public void TheUrlIsMatchedByNormalizedIdentity()
    {
        var recalled = QualityMemory.Recall(
            [Entry("https://STREAMING.thestream.cyou/live/210.m3u8", Now, (2_096_000, 2))],
            Url,
            Now);

        Assert.Equal(2, recalled.Failures[2_096_000]);
    }

    // The load-bearing expiry case: an origin that was slow last week must not still be capped today.
    [Fact]
    public void ARecordOlderThanTheRetention_IsNoEvidence()
    {
        var stale = Entry(Url, Now - QualityMemory.Retention, (2_096_000, 4));

        Assert.Empty(QualityMemory.Recall([stale], Url, Now).Failures);
    }

    [Fact]
    public void ARecordInsideTheRetention_StillCounts()
    {
        var recent = Entry(Url, Now - QualityMemory.Retention + TimeSpan.FromMinutes(1), (2_096_000, 4));

        Assert.Equal(4, QualityMemory.Recall([recent], Url, Now).Failures[2_096_000]);
    }

    // SP-0076 criterion 1: the whole point - the second session does not have to rediscover this.
    [Fact]
    public void AFreshRecord_RecallsTheCeilingItSettledOn()
    {
        var recalled = QualityMemory.Recall([Capped(Now, Middle, (2_096_000, 3))], Url, Now);

        Assert.Equal(Middle, recalled.Ceiling);
        Assert.Equal(QualityCeilingRecall.Applied, recalled.CeilingRecall);
        Assert.Equal(3, recalled.Failures[2_096_000]); // and the waits are still seeded
    }

    // The two lifetimes in one record. A cap applied before a single observation is the riskier of the two
    // facts, so it goes first - and the failure counts it travelled with are still current.
    [Fact]
    public void ACeilingOlderThanADay_IsNotAppliedThoughItsFailuresStillAre()
    {
        var yesterday = Capped(Now - QualityMemory.BlindCeilingRetention, Middle, (2_096_000, 3));

        var recalled = QualityMemory.Recall([yesterday], Url, Now);

        Assert.Null(recalled.Ceiling);
        Assert.Equal(QualityCeilingRecall.Stale, recalled.CeilingRecall);
        Assert.Equal(3, recalled.Failures[2_096_000]);
    }

    [Fact]
    public void ACeilingInsideTheDay_StillCaps()
    {
        var recent = Capped(Now - QualityMemory.BlindCeilingRetention + TimeSpan.FromMinutes(1), Middle, (2_096_000, 3));

        Assert.Equal(Middle, QualityMemory.Recall([recent], Url, Now).Ceiling);
    }

    // SP-0076 criterion 5: a file written by the previous version has failures and no ceiling. It keeps
    // working, and the fact it does not carry is reported rather than guessed at.
    [Fact]
    public void ARecordWithNoCeiling_KeepsItsFailuresAndCapsNothing()
    {
        var recalled = QualityMemory.Recall([Entry(Url, Now, (2_096_000, 3))], Url, Now);

        Assert.Null(recalled.Ceiling);
        Assert.Equal(QualityCeilingRecall.NoCeiling, recalled.CeilingRecall);
        Assert.Equal(3, recalled.Failures[2_096_000]);
    }

    // The ceiling reaches the engines as a resolution, so a rung without one would exclude every
    // rendition there is - which is a black screen, not a cap.
    [Fact]
    public void ACeilingWithNoResolution_IsTreatedAsUnrecorded()
    {
        var recalled = QualityMemory.Recall(
            [Capped(Now, new StreamQualityRung(796_000, 0, 0), (2_096_000, 3))],
            Url,
            Now);

        Assert.Null(recalled.Ceiling);
        Assert.Equal(QualityCeilingRecall.NoCeiling, recalled.CeilingRecall);
    }

    [Fact]
    public void RecordingStoresTheCeilingAlongsideTheFailures()
    {
        var after = QualityMemory.Record([], Url, [new QualityRungMemory(2_096_000, 1)], Middle, Now);

        Assert.Equal(Middle, Assert.Single(after).Ceiling);
    }

    // A session that climbed back to the top has no restriction to hand on, and writing one down would
    // cap the next session at a rung this one proved unnecessary.
    [Fact]
    public void RecordingAtTheTopRung_StoresNoCeiling()
    {
        var before = new[] { Capped(Now - TimeSpan.FromHours(1), Middle, (796_000, 2)) };

        var after = QualityMemory.Record(before, Url, [new QualityRungMemory(796_000, 2)], null, Now);

        Assert.Null(Assert.Single(after).Ceiling);
    }

    [Fact]
    public void RecordingReplacesThisSourcesEntryRatherThanMergingIt()
    {
        var before = new[] { Entry(Url, Now - TimeSpan.FromHours(1), (2_096_000, 4), (796_000, 2)) };

        var after = QualityMemory.Record(before, Url, [new QualityRungMemory(2_096_000, 1)], Middle, Now);

        var entry = Assert.Single(after);
        var rung = Assert.Single(entry.Rungs);
        Assert.Equal(2_096_000, rung.BandwidthBps);
        Assert.Equal(1, rung.Failures);
        Assert.Equal(Now, entry.UpdatedAt);
    }

    // A rung the governor has forgiven has to lose its record, or the next session re-seeds the very
    // failure the probe just disproved.
    [Fact]
    public void RecordingNothing_RemovesTheEntry()
    {
        var before = new[] { Entry(Url, Now - TimeSpan.FromHours(1), (2_096_000, 4)) };

        Assert.Empty(QualityMemory.Record(before, Url, [], Middle, Now));
    }

    [Fact]
    public void ARungAtZero_IsNeverWrittenDown()
    {
        var after = QualityMemory.Record([], Url, [new QualityRungMemory(796_000, 0)], Middle, Now);

        Assert.Empty(after);
    }

    [Fact]
    public void RecordingLeavesOtherSourcesAlone()
    {
        var other = Entry("https://other/live.m3u8", Now - TimeSpan.FromHours(2), (500_000, 1));

        var after = QualityMemory.Record([other], Url, [new QualityRungMemory(2_096_000, 1)], Middle, Now);

        Assert.Equal(2, after.Count);
        Assert.Contains(after, entry => entry.Url.Contains("other", StringComparison.Ordinal));
    }

    [Fact]
    public void RecordingPrunesEveryExpiredEntry()
    {
        var stale = Entry("https://stale/live.m3u8", Now - QualityMemory.Retention, (500_000, 3));

        var after = QualityMemory.Record([stale], Url, [new QualityRungMemory(2_096_000, 1)], Middle, Now);

        Assert.Single(after);
        Assert.DoesNotContain(after, entry => entry.Url.Contains("stale", StringComparison.Ordinal));
    }

    // The cap is what keeps this a cache and not a second listening history.
    [Fact]
    public void TheFileIsCappedAtTheMostRecentSources()
    {
        var crowd = Enumerable
            .Range(0, QualityMemory.MaxSources + 50)
            .Select(index => Entry($"https://host{index}/live.m3u8", Now - TimeSpan.FromMinutes(index + 1), (500_000, 1)))
            .ToArray();

        var after = QualityMemory.Record(crowd, Url, [new QualityRungMemory(2_096_000, 1)], Middle, Now);

        Assert.Equal(QualityMemory.MaxSources, after.Count);
        Assert.Equal(Url, after[0].Url); // the one just written is the most recent
        Assert.DoesNotContain(after, entry => entry.Url.Contains($"host{QualityMemory.MaxSources + 10}/", StringComparison.Ordinal));
    }

    [Fact]
    public void AnEmptyUrl_IsNeitherRecalledNorRecorded()
    {
        Assert.Empty(QualityMemory.Recall([Entry(Url, Now, (2_096_000, 1))], "  ", Now).Failures);
        Assert.Empty(QualityMemory.Record([], "  ", [new QualityRungMemory(2_096_000, 1)], Middle, Now));
    }
}
