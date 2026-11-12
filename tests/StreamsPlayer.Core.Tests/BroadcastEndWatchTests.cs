using System.Net.Sockets;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0250: the reading of an ended broadcast that <c>LIVE-BROADCAST</c> items T and U ask of a consumer - when the
/// directory's removal ends the stream, which answers of a reconnect end it, and which never do.
/// </summary>
public sealed class BroadcastEndWatchTests
{
    private static BroadcastEndWatch PlayedWatch()
    {
        var watch = new BroadcastEndWatch();
        watch.NoteLive();
        return watch;
    }

    [Fact]
    public void ARemovalWhileTheStreamStillDeliversEndsNothing()
    {
        var watch = PlayedWatch();

        Assert.False(watch.NoteRemoval().IsEnd);
        Assert.Null(watch.Ended);
    }

    [Fact]
    public void ARemovalThatWaitedEndsTheBroadcastAtTheMomentTheStreamStopsWithNoReconnectFirst()
    {
        var watch = PlayedWatch();
        watch.NoteRemoval();

        var verdict = watch.NoteStopped();

        Assert.True(verdict.IsEnd);
        Assert.Equal(BroadcastEndCause.Directory, verdict.Cause);
    }

    [Fact]
    public void ARemovalThatArrivesAfterTheStreamStoppedEndsTheReconnectAtOnce()
    {
        var watch = PlayedWatch();
        Assert.False(watch.NoteStopped().IsEnd);

        var verdict = watch.NoteRemoval();

        Assert.True(verdict.IsEnd);
        Assert.Equal(BroadcastEndCause.Directory, verdict.Cause);
    }

    [Fact]
    public void AStopWithNoRemovalAndNoAnswerKeepsTheBoundedRecovery()
    {
        var watch = PlayedWatch();

        Assert.False(watch.NoteStopped().IsEnd);
        Assert.False(watch.NoteAnswer(BroadcastEndAnswer.None).IsEnd);
        Assert.Null(watch.Ended);
    }

    [Theory]
    [InlineData(BroadcastEndAnswer.RelayGone, BroadcastEndCause.RelayGone)]
    [InlineData(BroadcastEndAnswer.TunnelEnded, BroadcastEndCause.TunnelEnded)]
    [InlineData(BroadcastEndAnswer.RelayNotFound, BroadcastEndCause.RelayNotFoundAfterPlay)]
    [InlineData(BroadcastEndAnswer.LanRefused, BroadcastEndCause.LanRefused)]
    [InlineData(BroadcastEndAnswer.LanNotFound, BroadcastEndCause.LanNotFound)]
    [InlineData(BroadcastEndAnswer.LanGone, BroadcastEndCause.LanGone)]
    public void ADefiniteAnswerAfterAPlayEndsTheBroadcastWithItsCause(BroadcastEndAnswer answer, BroadcastEndCause cause)
    {
        var watch = PlayedWatch();
        watch.NoteStopped();

        var verdict = watch.NoteAnswer(answer);

        Assert.True(verdict.IsEnd);
        Assert.Equal(cause, verdict.Cause);
    }

    [Theory]
    [InlineData(BroadcastEndAnswer.RelayNotFound)]
    [InlineData(BroadcastEndAnswer.LanRefused)]
    [InlineData(BroadcastEndAnswer.LanNotFound)]
    [InlineData(BroadcastEndAnswer.LanGone)]
    public void ARelay404AndEveryLanAnswerBeforeTheFirstPlayEndNothing(BroadcastEndAnswer answer)
    {
        // Item S: a 404 before the broadcast was ever played is "not started"; a closed LAN port is a stale address.
        var watch = new BroadcastEndWatch();

        Assert.False(watch.NoteAnswer(answer).IsEnd);
        Assert.False(watch.WouldEnd(answer));
    }

    [Theory]
    [InlineData(BroadcastEndAnswer.RelayGone)]
    [InlineData(BroadcastEndAnswer.TunnelEnded)]
    public void ARelay410AndATunnelEndedEndTheBroadcastEvenBeforeItWasPlayed(BroadcastEndAnswer answer)
    {
        var watch = new BroadcastEndWatch();

        Assert.True(watch.WouldEnd(answer));
        Assert.True(watch.NoteAnswer(answer).IsEnd);
    }

    [Theory]
    [InlineData(BroadcastEndAnswer.RelayStalled)]
    [InlineData(BroadcastEndAnswer.None)]
    public void AnUploadStallAndAnAnswerThatSaysNothingNeverEnd(BroadcastEndAnswer answer)
    {
        var watch = PlayedWatch();
        watch.NoteStopped();

        Assert.False(watch.NoteAnswer(answer).IsEnd);
        Assert.False(watch.WouldEnd(answer));
        Assert.Null(watch.Ended);
    }

    [Fact]
    public void AnEndIsStickyAndANewPlayDoesNotRevive()
    {
        var watch = PlayedWatch();
        watch.NoteStopped();
        watch.NoteAnswer(BroadcastEndAnswer.RelayGone);

        watch.NoteLive();

        Assert.Equal(BroadcastEndCause.RelayGone, watch.Ended);
        Assert.Equal(BroadcastEndCause.RelayGone, watch.NoteStopped().Cause);
        Assert.Equal(BroadcastEndCause.RelayGone, watch.NoteAnswer(BroadcastEndAnswer.LanRefused).Cause);
    }

    [Fact]
    public void ReconnectClearsTheEndAndTheWaitingRemovalButKeepsWhatWasPlayed()
    {
        var watch = PlayedWatch();
        watch.NoteRemoval();
        watch.NoteStopped();

        watch.Reset();

        Assert.Null(watch.Ended);
        Assert.True(watch.PlayedBefore);
        Assert.False(watch.NoteStopped().IsEnd);
    }

    [Fact]
    public void ABroadcastThatPlaysAgainAfterReconnectDropsTheRemovalItHadRead()
    {
        // Item DX: an ended id announced again is live again. The window that reconnected and plays must not end on
        // the removal of the announcement it replaced.
        var watch = PlayedWatch();
        watch.NoteRemoval();
        watch.NoteStopped();
        watch.Reset();

        watch.NoteLive();

        Assert.False(watch.NoteStopped().IsEnd);
    }

    [Fact]
    public void ADuplicateLiveReportOfTheSameLegKeepsAWaitingRemoval()
    {
        var watch = PlayedWatch();
        watch.NoteRemoval();

        watch.NoteLive();

        Assert.True(watch.NoteStopped().IsEnd);
    }

    [Fact]
    public void ARecoveryThatRanOutAfterAnEndedStreamIsTheBudgetCause()
    {
        var watch = PlayedWatch();
        watch.NoteStopped();

        var verdict = watch.NoteBudgetExhausted();

        Assert.Equal(BroadcastEndCause.BudgetExhausted, verdict.Cause);
        Assert.Equal("budget-exhausted", BroadcastEndCauses.Name(verdict.Cause));
    }

    [Theory]
    [InlineData(BroadcastEndCause.Directory, "directory")]
    [InlineData(BroadcastEndCause.RelayGone, "relay-410")]
    [InlineData(BroadcastEndCause.RelayNotFoundAfterPlay, "relay-404-after-play")]
    [InlineData(BroadcastEndCause.TunnelEnded, "tunnel-ended")]
    [InlineData(BroadcastEndCause.LanRefused, "lan-refused")]
    [InlineData(BroadcastEndCause.LanNotFound, "lan-404")]
    [InlineData(BroadcastEndCause.LanGone, "lan-410")]
    [InlineData(BroadcastEndCause.Bye, "bye")]
    [InlineData(BroadcastEndCause.BudgetExhausted, "budget-exhausted")]
    public void EveryCauseHasItsWordInTheDiagnosticLine(BroadcastEndCause cause, string word)
    {
        Assert.Equal(word, BroadcastEndCauses.Name(cause));
    }

    [Theory]
    [InlineData("relay-refused-410", BroadcastEndAnswer.RelayGone)]
    [InlineData("relay-refused-404", BroadcastEndAnswer.RelayNotFound)]
    [InlineData("relay-refused-503", BroadcastEndAnswer.RelayStalled)]
    [InlineData("ended", BroadcastEndAnswer.TunnelEnded)]
    [InlineData("unavailable", BroadcastEndAnswer.None)]
    [InlineData("relay-refused-502", BroadcastEndAnswer.None)]
    [InlineData("SocketException", BroadcastEndAnswer.None)]
    [InlineData("lan-refused", BroadcastEndAnswer.LanRefused)]
    [InlineData("lan-404", BroadcastEndAnswer.LanNotFound)]
    [InlineData("lan-410", BroadcastEndAnswer.LanGone)]
    [InlineData(null, BroadcastEndAnswer.None)]
    public void TheReasonsTheTransportsRaiseAreReadAsTheirAnswers(string? reason, BroadcastEndAnswer answer)
    {
        Assert.Equal(answer, BroadcastEndAnswers.FromReason(reason));
    }

    [Theory]
    [InlineData(BroadcastEndAnswer.LanRefused)]
    [InlineData(BroadcastEndAnswer.LanNotFound)]
    [InlineData(BroadcastEndAnswer.LanGone)]
    [InlineData(BroadcastEndAnswer.RelayGone)]
    [InlineData(BroadcastEndAnswer.RelayNotFound)]
    [InlineData(BroadcastEndAnswer.RelayStalled)]
    [InlineData(BroadcastEndAnswer.TunnelEnded)]
    public void AnAnswerSurvivesBeingCarriedAsAReason(BroadcastEndAnswer answer)
    {
        Assert.Equal(answer, BroadcastEndAnswers.FromReason(BroadcastEndAnswers.ReasonOf(answer)));
    }

    [Fact]
    public void ALanHttpAnswerIsReadFromItsStatusOrItsRefusedConnection()
    {
        var refused = new HttpRequestException("x", new SocketException((int)SocketError.ConnectionRefused));
        var reset = new HttpRequestException("x", new SocketException((int)SocketError.ConnectionReset));
        var unreachable = new SocketException((int)SocketError.NetworkUnreachable);

        Assert.Equal(BroadcastEndAnswer.LanNotFound, BroadcastEndAnswers.FromLanHttp(404, null));
        Assert.Equal(BroadcastEndAnswer.LanGone, BroadcastEndAnswers.FromLanHttp(410, null));
        Assert.Equal(BroadcastEndAnswer.LanRefused, BroadcastEndAnswers.FromLanHttp(null, refused));
        Assert.Equal(BroadcastEndAnswer.None, BroadcastEndAnswers.FromLanHttp(null, reset));
        Assert.Equal(BroadcastEndAnswer.None, BroadcastEndAnswers.FromLanHttp(null, unreachable));
        Assert.Equal(BroadcastEndAnswer.None, BroadcastEndAnswers.FromLanHttp(503, null));
        Assert.Equal(BroadcastEndAnswer.None, BroadcastEndAnswers.FromLanHttp(200, null));
        Assert.Equal(BroadcastEndAnswer.None, BroadcastEndAnswers.FromLanHttp(null, new TaskCanceledException()));
    }

    [Fact]
    public void ARemovalIsReadOnlyAfterAPresenceAndNeverFromAConnectionThatIsDown()
    {
        var reader = new BroadcastRemovalReader("b1");
        var present = new ExchangeDirectorySnapshot(1, [], [], ["b1", "b2"]);
        var absent = new ExchangeDirectorySnapshot(2, [], [], ["b2"]);

        Assert.False(reader.Observe(absent));        // a baseline: never listed, nothing to remove
        Assert.False(reader.Observe(present));
        Assert.False(reader.Observe(null));          // the control stream is down: unknown, not a removal
        Assert.False(reader.Observe(present));
        Assert.True(reader.Observe(absent));         // present, then absent in the next list or push
        Assert.False(reader.Observe(absent));        // told once per absence
    }

    [Fact]
    public void ABroadcastThatComesBackAndLeavesAgainIsARemovalAgain()
    {
        var reader = new BroadcastRemovalReader("b1");
        var present = new ExchangeDirectorySnapshot(1, [], [], ["b1"]);
        var absent = new ExchangeDirectorySnapshot(2, [], [], []);

        reader.Observe(present);
        Assert.True(reader.Observe(absent));
        Assert.False(reader.Observe(present));
        Assert.True(reader.Observe(absent));
    }
}
