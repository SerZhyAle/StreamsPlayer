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

    // S6-2: a foreign host at the producer's LAN address must not end a broadcast the viewer plays over a relay.
    [Theory]
    [InlineData(BroadcastEndAnswer.LanRefused)]
    [InlineData(BroadcastEndAnswer.LanNotFound)]
    [InlineData(BroadcastEndAnswer.LanGone)]
    public void ALanAnswerAfterARelayOrTunnelPlayIsNotAnEnd(BroadcastEndAnswer answer)
    {
        var watch = new BroadcastEndWatch();
        watch.NoteLive(BroadcastLiveTransport.Exchange);
        watch.NoteStopped();

        Assert.False(watch.ReadsLanAnswers);
        Assert.False(watch.WouldEnd(answer));
        Assert.False(watch.NoteAnswer(answer).IsEnd);
        Assert.Null(watch.Ended);
    }

    [Theory]
    [InlineData(BroadcastEndAnswer.LanRefused, BroadcastEndCause.LanRefused)]
    [InlineData(BroadcastEndAnswer.LanNotFound, BroadcastEndCause.LanNotFound)]
    [InlineData(BroadcastEndAnswer.LanGone, BroadcastEndCause.LanGone)]
    public void ALanAnswerAfterALanPlayEndsTheBroadcast(BroadcastEndAnswer answer, BroadcastEndCause cause)
    {
        var watch = new BroadcastEndWatch();
        watch.NoteLive(BroadcastLiveTransport.Lan);
        watch.NoteStopped();

        Assert.True(watch.ReadsLanAnswers);
        Assert.True(watch.WouldEnd(answer));
        var verdict = watch.NoteAnswer(answer);
        Assert.True(verdict.IsEnd);
        Assert.Equal(cause, verdict.Cause);
    }

    [Fact]
    public void APlayThatSaysNothingOfItsTransportKeepsTheOlderReadingOfLanAnswers()
    {
        // A leg that cannot say which transport played (the audio session when its leg has started no engine) keeps the older
        // reading: a LAN answer counts once the broadcast has played at all.
        var watch = new BroadcastEndWatch();
        Assert.False(watch.ReadsLanAnswers);

        watch.NoteLive();

        Assert.True(watch.ReadsLanAnswers);
        Assert.True(watch.NoteAnswer(BroadcastEndAnswer.LanRefused).IsEnd);
    }

    [Theory]
    [InlineData(BroadcastLiveTransport.Lan, BroadcastEndAnswer.RelayGone, BroadcastEndCause.RelayGone)]
    [InlineData(BroadcastLiveTransport.Lan, BroadcastEndAnswer.TunnelEnded, BroadcastEndCause.TunnelEnded)]
    [InlineData(BroadcastLiveTransport.Lan, BroadcastEndAnswer.RelayNotFound, BroadcastEndCause.RelayNotFoundAfterPlay)]
    [InlineData(BroadcastLiveTransport.Exchange, BroadcastEndAnswer.RelayGone, BroadcastEndCause.RelayGone)]
    [InlineData(BroadcastLiveTransport.Exchange, BroadcastEndAnswer.TunnelEnded, BroadcastEndCause.TunnelEnded)]
    [InlineData(BroadcastLiveTransport.Exchange, BroadcastEndAnswer.RelayNotFound, BroadcastEndCause.RelayNotFoundAfterPlay)]
    public void RelayAndTunnelAnswersKeepTheirRulesWhicheverTransportPlayed(
        BroadcastLiveTransport transport, BroadcastEndAnswer answer, BroadcastEndCause cause)
    {
        var watch = new BroadcastEndWatch();
        watch.NoteLive(transport);

        var verdict = watch.NoteAnswer(answer);

        Assert.True(verdict.IsEnd);
        Assert.Equal(cause, verdict.Cause);
    }

    [Fact]
    public void ReconnectKeepsTheTransportUntilTheNextPlayReplacesIt()
    {
        // The person's Reconnect after a relay-played broadcast must not meet the LAN reading again (PlayedBefore survives Reset).
        var watch = new BroadcastEndWatch();
        watch.NoteLive(BroadcastLiveTransport.Exchange);
        watch.NoteStopped();
        watch.Reset();

        Assert.True(watch.PlayedBefore);
        Assert.False(watch.ReadsLanAnswers);
        Assert.False(watch.NoteAnswer(BroadcastEndAnswer.LanNotFound).IsEnd);

        watch.NoteLive(BroadcastLiveTransport.Lan);
        Assert.True(watch.ReadsLanAnswers);

        watch.Reset();
        watch.NoteLive(BroadcastLiveTransport.Exchange);
        Assert.False(watch.ReadsLanAnswers);
    }

    [Theory]
    [InlineData("http://192.168.1.20:8080/live", null, BroadcastLiveTransport.Lan)]
    [InlineData("rtsp://192.168.1.20:8554/live", "RTSP", BroadcastLiveTransport.Lan)]
    [InlineData("https://relay.example/v2/b/abc/stream", "RELAY", BroadcastLiveTransport.Exchange)]
    [InlineData("https://relay.example/v2/b/abc/stream", null, BroadcastLiveTransport.Exchange)]
    [InlineData("fmsx://relay.example/b/abc/https", "TUNNEL", BroadcastLiveTransport.Exchange)]
    [InlineData("fmsx://relay.example/b/abc/https", null, BroadcastLiveTransport.Exchange)]
    [InlineData("http://192.168.1.20:8080/live", "RELAY", BroadcastLiveTransport.Exchange)]
    public void AnEndpointIsToldAsLanOrExchangeByTheRuleTheProbeUses(string url, string? transport, BroadcastLiveTransport expected)
    {
        var endpoint = new FastMediaSorterBroadcastEndpoint(url, transport, null, null, null, null, null, null, null);

        Assert.Equal(expected, BroadcastLiveTransports.Of(endpoint));
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
