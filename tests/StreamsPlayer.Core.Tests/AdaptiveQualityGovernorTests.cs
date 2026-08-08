using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0071 criteria 1-5: when the ceiling moves and when it must not. Driven as a sequence of starvations
/// and observations with no network and no media engine - the states are hard to provoke on a live
/// stream, which is exactly why the decision was pushed out of the player and into a type testable here.
/// </summary>
public sealed class AdaptiveQualityGovernorTests
{
    private static readonly StreamQualityRung Low = new(446_000, 426, 240);
    private static readonly StreamQualityRung Mid = new(796_000, 640, 360);
    private static readonly StreamQualityRung High = new(2_096_000, 1024, 576);

    /// <summary>The real three-rung ladder of the reported channel.</summary>
    private static readonly StreamQualityRung[] Ladder = [Low, Mid, High];

    private static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);

    private static AdaptiveQualityGovernor OnTheReferenceLadder()
    {
        var governor = new AdaptiveQualityGovernor();
        governor.UseLadder(Ladder, At(0));
        Assert.True(governor.HasLadder);
        Assert.Null(governor.Ceiling); // the top rung is no restriction at all
        return governor;
    }

    /// <summary>Two starvations inside the window, which is what condemns a rung.</summary>
    private static QualityDecision StepDown(AdaptiveQualityGovernor governor, double first, double second)
    {
        Assert.Null(governor.NotifyStarvation(At(first)));
        var decision = governor.NotifyStarvation(At(second));
        Assert.NotNull(decision);
        return decision.Value;
    }

    // Criterion 2, the load-bearing one: a healthy stream never reaches the rule at all.
    [Fact]
    public void WithoutStarvation_TheCeilingNeverMoves()
    {
        var governor = OnTheReferenceLadder();

        for (var second = 0; second <= 1200; second += 2)
        {
            Assert.Null(governor.Observe(At(second)));
        }

        Assert.Null(governor.Ceiling);
    }

    [Fact]
    public void OneStarvation_IsAHiccupAndCostsNothing()
    {
        var governor = OnTheReferenceLadder();

        Assert.Null(governor.NotifyStarvation(At(30)));
        Assert.Null(governor.Ceiling);
    }

    [Fact]
    public void TwoStarvationsFurtherApartThanTheWindow_AreStillTwoHiccups()
    {
        var governor = OnTheReferenceLadder();

        Assert.Null(governor.NotifyStarvation(At(30)));
        Assert.Null(governor.NotifyStarvation(At(160)));
        Assert.Null(governor.Ceiling);
    }

    // Criterion 1.
    [Fact]
    public void TwoStarvationsInsideTheWindow_StepDownExactlyOneRung()
    {
        var governor = OnTheReferenceLadder();

        var decision = StepDown(governor, 30, 90);

        Assert.Equal(Mid, decision.Rung);
        Assert.Equal(QualityChangeKind.StepDown, decision.Kind);
        Assert.Equal(2, decision.Starvations);
        Assert.Equal(Mid, governor.Ceiling);
        Assert.Equal(Mid, governor.CurrentRung);
    }

    // The two answers differ exactly at the top, and the log depends on the difference: the ceiling is
    // what the engine is given, the rung is what the player is on.
    [Fact]
    public void OnTheTopRung_ThereIsARungButNoCeiling()
    {
        var governor = OnTheReferenceLadder();

        Assert.Null(governor.Ceiling);
        Assert.Equal(High, governor.CurrentRung);
    }

    [Fact]
    public void WithoutALadder_ThereIsNoRungEither()
    {
        var governor = new AdaptiveQualityGovernor();

        Assert.Null(governor.CurrentRung);
    }

    // The evidence that condemned the old rung must not also condemn the new one; otherwise one bad
    // minute walks the ladder to the bottom before the lower rung has had a chance to prove itself.
    [Fact]
    public void AStarvationRightAfterAStepDown_DoesNotStepDownAgain()
    {
        var governor = OnTheReferenceLadder();
        StepDown(governor, 30, 90);

        Assert.Null(governor.NotifyStarvation(At(95)));
        Assert.Equal(Mid, governor.Ceiling);
    }

    // The reported source needs this: its middle rung was borderline (2.0-5.0 s for 4 s of media).
    [Fact]
    public void ARungThatAlsoStarves_StepsDownAgain()
    {
        var governor = OnTheReferenceLadder();
        StepDown(governor, 30, 90);

        var decision = StepDown(governor, 120, 180);

        Assert.Equal(Low, decision.Rung);
        Assert.Equal(Low, governor.Ceiling);
        Assert.True(governor.AtLowestRung);
    }

    [Fact]
    public void OnTheLowestRung_FurtherStarvationDecidesNothing()
    {
        var governor = OnTheReferenceLadder();
        StepDown(governor, 30, 90);
        StepDown(governor, 120, 180);

        Assert.Null(governor.NotifyStarvation(At(200)));
        Assert.Null(governor.NotifyStarvation(At(260)));
        Assert.Null(governor.NotifyStarvation(At(320)));
        Assert.Equal(Low, governor.Ceiling);
    }

    // Criterion 5: a single-quality source has no ladder, and every input has to be inert.
    [Fact]
    public void WithoutALadder_NothingIsEverDecided()
    {
        var governor = new AdaptiveQualityGovernor();
        governor.UseLadder([High], At(0));

        Assert.False(governor.HasLadder);
        Assert.Null(governor.Ceiling);
        Assert.Null(governor.NotifyStarvation(At(30)));
        Assert.Null(governor.NotifyStarvation(At(60)));
        Assert.Null(governor.Observe(At(300)));
    }

    [Fact]
    public void BeforeALadderIsKnown_StarvationDecidesNothing()
    {
        var governor = new AdaptiveQualityGovernor();

        Assert.Null(governor.NotifyStarvation(At(10)));
        Assert.Null(governor.Observe(At(120)));
    }

    // Criterion 3. Five minutes, not one: a probe costs a re-open whether or not it succeeds, and the
    // owner's own session spent 73 % of its black screen on probing at the old sixty-second base.
    [Fact]
    public void AfterAStepDown_TheNextRungIsProbedFiveMinutesLater()
    {
        var governor = OnTheReferenceLadder();
        StepDown(governor, 30, 90);

        Assert.Null(governor.Observe(At(389))); // one second short of the five minutes
        var decision = governor.Observe(At(390));

        Assert.NotNull(decision);
        Assert.Equal(High, decision.Value.Rung);
        Assert.Equal(QualityChangeKind.Probe, decision.Value.Kind);
        Assert.Null(governor.Ceiling); // back on the top rung, so no restriction is applied
    }

    // Criterion 4: the anti-flap rule. A rung that fails its trial is not retried on the same schedule.
    [Fact]
    public void AFailedProbe_StepsBackDownAndDoublesThatRungsWait()
    {
        var governor = OnTheReferenceLadder();
        StepDown(governor, 30, 90);
        Assert.NotNull(governor.Observe(At(390))); // probe up at the 300 s wait

        var back = StepDown(governor, 400, 440);
        Assert.Equal(Mid, back.Rung);

        Assert.Null(governor.Observe(At(1039))); // 300 s would have been enough; 600 s is not yet up
        Assert.NotNull(governor.Observe(At(1040)));
    }

    [Fact]
    public void EachFailedProbe_DoublesThatRungsWaitAgain()
    {
        var governor = OnTheReferenceLadder();
        StepDown(governor, 30, 90);
        Assert.NotNull(governor.Observe(At(390)));   // wait 300 -> probe
        StepDown(governor, 400, 440);                 // failed -> that rung now waits 600
        Assert.NotNull(governor.Observe(At(1040)));  // probe
        StepDown(governor, 1050, 1100);               // failed -> 1200

        Assert.Null(governor.Observe(At(2299)));
        Assert.NotNull(governor.Observe(At(2300)));
    }

    [Fact]
    public void TheWaitIsCapped_HoweverManyProbesFail()
    {
        var governor = OnTheReferenceLadder();
        StepDown(governor, 0, 1);
        var now = 1d;
        var wait = 300d;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            now += wait;
            Assert.NotNull(governor.Observe(At(now)));
            StepDown(governor, now + 1, now + 2);
            now += 2;
            wait = Math.Min(wait * 2, 3600);
        }

        Assert.Equal(3600, wait);
        Assert.Null(governor.Observe(At(now + 3599)));
        Assert.NotNull(governor.Observe(At(now + 3600)));
    }

    // Criterion 3 again: a rung that has genuinely proved itself must not be punished for its past.
    [Fact]
    public void AProbeThatSurvivesTheWindow_ClearsThatRungsFailures()
    {
        var governor = OnTheReferenceLadder();
        StepDown(governor, 30, 90);
        Assert.NotNull(governor.Observe(At(390))); // probe to High
        StepDown(governor, 400, 440);               // High failed once -> it now waits 600
        Assert.NotNull(governor.Observe(At(1040))); // probe to High again

        Assert.Null(governor.Observe(At(1160)));   // survived one full window: High is forgiven
        StepDown(governor, 1170, 1220);             // and knocked back down by fresh starvation

        Assert.Null(governor.Observe(At(1519)));   // so its wait is the base 300 s once more
        Assert.NotNull(governor.Observe(At(1520)));
    }

    // The field defect, 2026-08-08: with one shared wait, the probe that succeeded at 796k reset it and
    // sent the player straight back to a top rung that had already failed three times. What a rung earns
    // is its own; what another rung earns is not.
    [Fact]
    public void ASuccessAtOneRung_DoesNotForgiveTheRungAboveIt()
    {
        var governor = new AdaptiveQualityGovernor();
        governor.UseLadder(Ladder, At(0));

        // High fails a probe from Mid, twice, so it owes a 1200 s wait.
        StepDown(governor, 30, 90);
        Assert.NotNull(governor.Observe(At(390)));
        StepDown(governor, 400, 440);
        Assert.NotNull(governor.Observe(At(1040)));
        StepDown(governor, 1050, 1100);

        // Now knock the player down to the bottom rung and let Mid win its own probe cleanly.
        StepDown(governor, 1110, 1150);
        Assert.Equal(Low, governor.CurrentRung);
        Assert.NotNull(governor.Observe(At(1450)));   // Mid has no failures: the base 300 s
        Assert.Equal(Mid, governor.CurrentRung);
        Assert.Null(governor.Observe(At(1570)));       // Mid survives its window and is forgiven

        // High must still be serving its own sentence rather than riding Mid's success.
        Assert.Null(governor.Observe(At(1750)));       // 300 s after the move to Mid: not enough for High
        Assert.Equal(Mid, governor.CurrentRung);
        Assert.NotNull(governor.Observe(At(2650)));    // 1200 s after it, exactly as High owed
        Assert.Equal(High, governor.CurrentRung);
    }

    // ---- The record carried in from earlier sessions (SP-0071 amendment) ----

    // Measured: the owner's 2026-08-08 session spent 21.5 s of black screen - 60 % of its total - on one
    // probe to a rung that had already failed four times in earlier sessions, because the record died
    // with the window. Seeded, that probe does not happen.
    [Fact]
    public void ARecalledFailure_MakesThatRungWaitAsIfItHadJustFailed()
    {
        var governor = new AdaptiveQualityGovernor();
        governor.UseLadder(Ladder, At(0), new Dictionary<int, int> { [High.BandwidthBps] = 1 });
        StepDown(governor, 30, 90);

        Assert.Null(governor.Observe(At(689)));  // 300 s would have been enough with no record
        Assert.NotNull(governor.Observe(At(690)));
    }

    [Fact]
    public void RecalledFailuresKeepDoublingFromWhereTheyLeftOff()
    {
        var governor = new AdaptiveQualityGovernor();
        governor.UseLadder(Ladder, At(0), new Dictionary<int, int> { [High.BandwidthBps] = 3 });
        StepDown(governor, 30, 90);

        Assert.Null(governor.Observe(At(2489)));  // 300 doubled three times = 2400 s
        Assert.NotNull(governor.Observe(At(2490)));
    }

    // A source may re-encode between sessions. A record for a rendition this ladder no longer offers is
    // not applied to whatever now sits at that position - the key is bandwidth, not index.
    [Fact]
    public void ARecalledRungTheLadderNoLongerOffers_IsIgnored()
    {
        var governor = new AdaptiveQualityGovernor();
        governor.UseLadder(Ladder, At(0), new Dictionary<int, int> { [3_000_000] = 4 });
        StepDown(governor, 30, 90);

        Assert.Null(governor.Observe(At(389)));
        Assert.NotNull(governor.Observe(At(390)));  // the base wait: nothing was recalled for High
        Assert.Empty(governor.Failures);
    }

    [Fact]
    public void WhatWasRecalledIsWhatIsExported()
    {
        var governor = new AdaptiveQualityGovernor();
        governor.UseLadder(Ladder, At(0), new Dictionary<int, int> { [High.BandwidthBps] = 2 });

        var recorded = Assert.Single(governor.Failures);
        Assert.Equal(High.BandwidthBps, recorded.BandwidthBps);
        Assert.Equal(2, recorded.Failures);
    }

    [Fact]
    public void AFailedProbe_IsExportedAndBumpsTheRevision()
    {
        var governor = OnTheReferenceLadder();
        Assert.Equal(0, governor.MemoryRevision);
        Assert.Empty(governor.Failures);

        StepDown(governor, 30, 90);
        Assert.NotNull(governor.Observe(At(390)));
        StepDown(governor, 400, 440);

        Assert.Equal(1, governor.MemoryRevision);
        var recorded = Assert.Single(governor.Failures);
        Assert.Equal(High.BandwidthBps, recorded.BandwidthBps);
        Assert.Equal(1, recorded.Failures);
    }

    // The change no return value reports: a rung forgiven inside Observe, which hands back null. Without
    // the revision the player would keep writing a failure the probe has just disproved.
    [Fact]
    public void AForgivenRung_LeavesNothingToPersistAndBumpsTheRevision()
    {
        var governor = new AdaptiveQualityGovernor();
        governor.UseLadder(Ladder, At(0), new Dictionary<int, int> { [High.BandwidthBps] = 1 });
        StepDown(governor, 30, 90);
        Assert.NotNull(governor.Observe(At(690)));   // probe to High on its doubled wait
        var before = governor.MemoryRevision;

        Assert.Null(governor.Observe(At(810)));      // survived the window: forgiven

        Assert.Equal(before + 1, governor.MemoryRevision);
        Assert.Empty(governor.Failures);
    }

    [Fact]
    public void TheRevisionDoesNotMove_WhileTheRecordDoesNot()
    {
        var governor = OnTheReferenceLadder();
        StepDown(governor, 30, 90);

        for (var second = 100; second < 380; second += 2)
        {
            Assert.Null(governor.Observe(At(second)));
        }

        Assert.Equal(0, governor.MemoryRevision);
    }

    [Fact]
    public void WithoutALadder_ThereIsNothingToPersist()
    {
        var governor = new AdaptiveQualityGovernor();
        governor.UseLadder([High], At(0), new Dictionary<int, int> { [High.BandwidthBps] = 4 });

        Assert.Empty(governor.Failures);
    }

    // One starvation is not evidence at any rung, including a rung on trial.
    [Fact]
    public void OneStarvationDuringAProbe_LeavesTheProbeOnTrial()
    {
        var governor = OnTheReferenceLadder();
        StepDown(governor, 30, 90);
        Assert.NotNull(governor.Observe(At(390)));

        Assert.Null(governor.NotifyStarvation(At(440)));

        Assert.Null(governor.Ceiling);
        Assert.Null(governor.Observe(At(700)));
    }

    // ---- Entering the ladder where the media was actually opened (SP-0076) ----

    [Fact]
    public void WithNoRememberedCeiling_TheSessionStartsAtTheTopAsItAlwaysDid()
    {
        var governor = OnTheReferenceLadder();

        Assert.Equal(QualityLadderEntry.Top, governor.LadderEntry);
        Assert.Equal(High, governor.CurrentRung);
    }

    // The ticket in one case: the media was opened capped, so the governor has to be where the engine is.
    [Fact]
    public void ARememberedCeiling_EntersTheLadderAtThatRung()
    {
        var governor = new AdaptiveQualityGovernor();
        governor.UseLadder(Ladder, At(0), null, Mid);

        Assert.Equal(QualityLadderEntry.Remembered, governor.LadderEntry);
        Assert.Equal(Mid, governor.CurrentRung);
        Assert.Equal(Mid, governor.Ceiling); // and every re-open keeps carrying it
    }

    // The same rule libvlc's representation selector applies: the highest rendition that fits. A source
    // that re-encoded slightly must not throw the record away over a resolution that moved by 8 pixels.
    [Fact]
    public void ACeilingBetweenTwoRungs_EntersAtTheLowerOne()
    {
        var governor = new AdaptiveQualityGovernor();
        governor.UseLadder(Ladder, At(0), null, new StreamQualityRung(900_000, 800, 450));

        Assert.Equal(QualityLadderEntry.Remembered, governor.LadderEntry);
        Assert.Equal(Mid, governor.CurrentRung);
    }

    // The ticket's main risk. The record names a ladder this source no longer offers, so it is worth
    // nothing - and the session falls back to exactly today's behaviour rather than to a guess.
    [Fact]
    public void ACeilingBelowEveryRung_IsAMissAndEntersAtTheTop()
    {
        var governor = new AdaptiveQualityGovernor();
        governor.UseLadder(Ladder, At(0), null, new StreamQualityRung(200_000, 320, 180));

        Assert.Equal(QualityLadderEntry.Missed, governor.LadderEntry);
        Assert.Equal(High, governor.CurrentRung);
        Assert.Null(governor.Ceiling); // so the next re-open drops the cap that fitted nothing
    }

    // A ceiling that restricts nothing is not a restriction, whatever the record called it.
    [Fact]
    public void ACeilingAtOrAboveTheTopRung_ReadsAsAnUnrestrictedStart()
    {
        var governor = new AdaptiveQualityGovernor();
        governor.UseLadder(Ladder, At(0), null, High);

        Assert.Equal(QualityLadderEntry.Top, governor.LadderEntry);
        Assert.Null(governor.Ceiling);
    }

    // Both halves of the record are in play at once, and they are independent: the ceiling says where the
    // session starts, the failures say how long before it tries to leave.
    [Fact]
    public void ARememberedCeilingStillLetsTheSeededWaitsGovernTheProbe()
    {
        var governor = new AdaptiveQualityGovernor();
        governor.UseLadder(Ladder, At(0), new Dictionary<int, int> { [High.BandwidthBps] = 1 }, Mid);

        Assert.Null(governor.Observe(At(599)));            // 300 s doubled once, and no step down paid
        var probe = governor.Observe(At(600));
        Assert.Equal(High, probe?.Rung);
        Assert.Equal(QualityChangeKind.Probe, probe?.Kind);
    }

    // Criterion 4: a source that stopped throttling climbs back out on its own, one rung at a time.
    [Fact]
    public void FromARememberedRung_TheProbeStillClimbsOneRungAtATime()
    {
        var governor = new AdaptiveQualityGovernor();
        governor.UseLadder(Ladder, At(0), null, Low);

        Assert.Equal(Low, governor.CurrentRung);
        Assert.Equal(Mid, governor.Observe(At(300))?.Rung);
        Assert.Equal(High, governor.Observe(At(720))?.Rung); // after the trial window and a fresh wait
    }

    [Fact]
    public void WithoutALadder_ARememberedCeilingChangesNothing()
    {
        var governor = new AdaptiveQualityGovernor();
        governor.UseLadder([High], At(0), null, Mid);

        Assert.False(governor.HasLadder);
        Assert.Null(governor.Ceiling);
        Assert.Equal(QualityLadderEntry.Top, governor.LadderEntry);
    }
}
