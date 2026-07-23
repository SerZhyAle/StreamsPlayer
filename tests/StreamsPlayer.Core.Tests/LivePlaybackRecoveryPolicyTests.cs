using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class LivePlaybackRecoveryPolicyTests
{
    private static PlaybackFailureSignal Transient(string reason = "connection timeout") => new(reason);
    private static PlaybackFailureSignal BehindLive() => new("behind live window", BehindLiveWindow: true);
    private static PlaybackFailureSignal Stall() => new("stall", Stall: true);
    private static PlaybackFailureSignal Ended() => new("end_reached", EndReached: true);

    [Fact]
    public void Transient_FollowsExponentialBackoffThenHardFails()
    {
        var policy = new LivePlaybackRecoveryPolicy();
        var expected = new[] { 2, 4, 8, 16 };
        for (var i = 0; i < expected.Length; i++)
        {
            var decision = policy.Decide(Transient());
            Assert.Equal(RecoveryActionKind.Reconnect, decision.Kind);
            Assert.Equal(RecoveryTrigger.Transient, decision.Trigger);
            Assert.Equal(i + 1, decision.Attempt);
            Assert.Equal(4, decision.Budget);
            Assert.Equal(TimeSpan.FromSeconds(expected[i]), decision.Delay);
        }

        Assert.Equal(RecoveryActionKind.HardFail, policy.Decide(Transient()).Kind);
    }

    [Fact]
    public void BehindLiveWindow_FollowsLinearBackoffThenHardFails()
    {
        var policy = new LivePlaybackRecoveryPolicy();
        foreach (var seconds in new[] { 1, 2, 3 })
        {
            var decision = policy.Decide(BehindLive());
            Assert.Equal(RecoveryActionKind.Reconnect, decision.Kind);
            Assert.Equal(RecoveryTrigger.BehindLiveWindow, decision.Trigger);
            Assert.Equal(TimeSpan.FromSeconds(seconds), decision.Delay);
        }

        Assert.Equal(RecoveryActionKind.HardFail, policy.Decide(BehindLive()).Kind);
    }

    [Fact]
    public void Stall_AllowsThreeReconnectsThenHardFails()
    {
        var policy = new LivePlaybackRecoveryPolicy();
        for (var i = 0; i < 3; i++)
        {
            var decision = policy.Decide(Stall());
            Assert.Equal(RecoveryActionKind.Reconnect, decision.Kind);
            Assert.Equal(RecoveryTrigger.Stall, decision.Trigger);
            Assert.Equal(TimeSpan.FromSeconds(1), decision.Delay);
        }

        Assert.Equal(RecoveryActionKind.HardFail, policy.Decide(Stall()).Kind);
    }

    [Fact]
    public void StreamEnded_AllowsFourReconnectsThenHardFails()
    {
        var policy = new LivePlaybackRecoveryPolicy();
        for (var i = 0; i < 4; i++)
        {
            var decision = policy.Decide(Ended());
            Assert.Equal(RecoveryActionKind.Reconnect, decision.Kind);
            Assert.Equal(RecoveryTrigger.StreamEnded, decision.Trigger);
        }

        Assert.Equal(RecoveryActionKind.HardFail, policy.Decide(Ended()).Kind);
    }

    [Fact]
    public void NotifyLive_RestoresFullBudgetMidSequence()
    {
        var policy = new LivePlaybackRecoveryPolicy();
        policy.Decide(Transient());
        policy.Decide(Transient());
        policy.NotifyLive();

        var decision = policy.Decide(Transient());
        Assert.Equal(RecoveryActionKind.Reconnect, decision.Kind);
        Assert.Equal(1, decision.Attempt);
        Assert.Equal(TimeSpan.FromSeconds(2), decision.Delay);
    }

    [Fact]
    public void Budgets_AreIndependentPerTrigger()
    {
        var policy = new LivePlaybackRecoveryPolicy();
        for (var i = 0; i < 4; i++)
        {
            policy.Decide(Transient());
        }

        Assert.Equal(RecoveryActionKind.HardFail, policy.Decide(Transient()).Kind);

        // An exhausted transient budget must not reduce the independent stall budget.
        var stall = policy.Decide(Stall());
        Assert.Equal(RecoveryActionKind.Reconnect, stall.Kind);
        Assert.Equal(1, stall.Attempt);
    }

    [Fact]
    public void HardFailSignal_NeverConsumesTransientBudget()
    {
        var policy = new LivePlaybackRecoveryPolicy();
        var hard = policy.Decide(new PlaybackFailureSignal("not found", HttpStatusCode: 404));
        Assert.Equal(RecoveryActionKind.HardFail, hard.Kind);
        Assert.Equal(RecoveryTrigger.HardFail, hard.Trigger);

        // A later transient failure still starts at attempt 1 with the full budget.
        var decision = policy.Decide(Transient());
        Assert.Equal(RecoveryActionKind.Reconnect, decision.Kind);
        Assert.Equal(1, decision.Attempt);
    }

    [Theory]
    [InlineData(429, RecoveryTrigger.Transient)]
    [InlineData(500, RecoveryTrigger.Transient)]
    [InlineData(503, RecoveryTrigger.Transient)]
    [InlineData(403, RecoveryTrigger.HardFail)]
    [InlineData(404, RecoveryTrigger.HardFail)]
    [InlineData(451, RecoveryTrigger.HardFail)]
    public void Classify_MapsHttpStatus(int status, RecoveryTrigger expected)
    {
        Assert.Equal(expected, PlaybackRecoveryClassifier.Classify(new PlaybackFailureSignal("encountered_error", HttpStatusCode: status)));
    }

    [Theory]
    [InlineData("connection timeout", RecoveryTrigger.Transient)]
    [InlineData("Connection reset by peer", RecoveryTrigger.Transient)]
    [InlineData("unsupported codec", RecoveryTrigger.HardFail)]
    [InlineData("malformed manifest", RecoveryTrigger.HardFail)]
    public void Classify_MapsReasonTokens(string reason, RecoveryTrigger expected)
    {
        Assert.Equal(expected, PlaybackRecoveryClassifier.Classify(new PlaybackFailureSignal(reason)));
    }

    [Fact]
    public void Classify_StallFlagWinsOverEverything()
    {
        Assert.Equal(RecoveryTrigger.Stall, PlaybackRecoveryClassifier.Classify(
            new PlaybackFailureSignal("connection timeout", HttpStatusCode: 404, Stall: true)));
    }

    [Fact]
    public void Classify_BehindLiveWindowFlagWins()
    {
        Assert.Equal(RecoveryTrigger.BehindLiveWindow, PlaybackRecoveryClassifier.Classify(
            new PlaybackFailureSignal(null, BehindLiveWindow: true)));
    }

    [Fact]
    public void Classify_EndReachedMapsToStreamEnded()
    {
        Assert.Equal(RecoveryTrigger.StreamEnded, PlaybackRecoveryClassifier.Classify(
            new PlaybackFailureSignal("end_reached", EndReached: true)));
    }

    [Fact]
    public void Classify_EmptyReasonDefaultsToTransient()
    {
        Assert.Equal(RecoveryTrigger.Transient, PlaybackRecoveryClassifier.Classify(new PlaybackFailureSignal(null)));
    }
}
