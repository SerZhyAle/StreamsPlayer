using System.Net;
using System.Net.Sockets;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0250: the order a producer ends a broadcast in (requirement 9), and the places where the windows read the end
/// (requirements 1 to 8), held over the App's own sources - the App has no test project, and what a window does with a
/// verdict can only be seen here without a phone. The behaviour the sources call is held by <c>BroadcastEndWatchTests</c>.
/// </summary>
public sealed class BroadcastEndWiringTests
{
    private static string Method(string file, string signature)
    {
        var text = Assert.Single(AppSourceFile.LoadAll(file)).Text;
        var start = text.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{file} has no '{signature}'");
        var after = start + signature.Length;
        var end = new[] { "\n    private ", "\n    internal ", "\n    public " }
            .Select(marker => text.IndexOf(marker, after, StringComparison.Ordinal))
            .Where(index => index > 0).DefaultIfEmpty(text.Length).Min();
        return text[start..end];
    }

    [Fact]
    public void TheProducerWritesBroadcastEndBeforeAnyStreamOfTheBroadcastCloses()
    {
        var teardown = Method("BroadcastProducerService.cs", "private async Task TeardownAsync(");

        var end = teardown.IndexOf("type = \"broadcast-end\"", StringComparison.Ordinal);
        Assert.True(end >= 0, "the teardown no longer writes broadcast-end");
        foreach (var closing in new[] { "Video.DisposeAsync", "state.Dispose", "UploadChunks?.Writer.TryComplete", "UploadRetire?.Cancel", "LanServer.StopAsync" })
        {
            var at = teardown.IndexOf(closing, StringComparison.Ordinal);
            Assert.True(at > end, $"'{closing}' (at {at}) must come after broadcast-end (at {end}) - DEVICE-EXCHANGE item DX");
        }
    }

    [Fact]
    public void TheEndIsWrittenWithABoundAndNeverWaitsForAnAbsentControlStream()
    {
        var teardown = Method("BroadcastProducerService.cs", "private async Task TeardownAsync(");

        Assert.Contains("new CancellationTokenSource(EndWriteTimeout)", teardown, StringComparison.Ordinal);
        Assert.Contains("PostControlEnvelopeAsync(", teardown, StringComparison.Ordinal);
        // The post itself returns false at once when no session is up; the guard is the broadcast having been announced.
        Assert.Contains("if (state.ExchangeStarted)", teardown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStoppedLanShareLeavesItsPortClosedSoAReconnectIsRefusedAtOnce()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var server = new BroadcastLanServer(new BroadcastLanServerOptions("t", 128, 0, ["/live-audio"], "audio/aac", false));
        server.Start(null, port);
        using (var open = new TcpClient())
        {
            await open.ConnectAsync(IPAddress.Loopback, port);
        }

        await server.StopAsync();

        var answer = await BroadcastLanProbe.ProbeAsync(new Uri($"http://127.0.0.1:{port}/live-audio"), CancellationToken.None,
            TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(300));
        Assert.Equal(BroadcastEndAnswer.LanRefused, answer);
    }

    [Fact]
    public void OnlyTheFollowerReachesTheDirectoryForAWindowThatPlaysABroadcast()
    {
        foreach (var file in new[] { "PlayerWindow.BroadcastEnd.cs", "BroadcastAudioSession.cs", "PlayerWindow.Legs.cs", "PlayerWindow.Attempts.cs" })
        {
            var text = Assert.Single(AppSourceFile.LoadAll(file)).Masked;
            Assert.DoesNotContain("AcquireDirectory", text, StringComparison.Ordinal);
            Assert.DoesNotContain("OpenGuestViewer", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ThePlayerReadsEveryStopAndEveryAnswerAndLetsNoBackoffReopenBehindAVerdict()
    {
        var recover = Method("PlayerWindow.xaml.cs", "private async Task RecoverAsync(");
        Assert.True(recover.IndexOf("NoteBroadcastStopped()", StringComparison.Ordinal) is var stopped and > 0
            && stopped < recover.IndexOf("_recoveryInFlight = true;", StringComparison.Ordinal),
            "a stop must be read before the recovery decides");
        Assert.Contains("_closing || _broadcastEnded", recover, StringComparison.Ordinal);

        Assert.Contains("NoteBroadcastAnswer(reason)", Method("PlayerWindow.Attempts.cs", "private void ReportOpenFailure("), StringComparison.Ordinal);
        Assert.Contains("ProbeLanAsync(endpoint)", Method("PlayerWindow.Legs.cs", "private async Task OpenLegAsync("), StringComparison.Ordinal);
        Assert.Contains("NoteBroadcastLive();", Assert.Single(AppSourceFile.LoadAll("PlayerWindow.xaml.cs")).Text, StringComparison.Ordinal);
    }

    // S6-2: the window tells the watch which transport went live and asks the watch's own reading before it probes the LAN.
    [Fact]
    public void ThePlayerTellsTheWatchTheTransportThatWentLiveAndProbesTheLanOnlyWhereItsAnswerCouldEndTheBroadcast()
    {
        var live = Method("PlayerWindow.BroadcastEnd.cs", "private void NoteBroadcastLive(");
        Assert.Contains("BroadcastLiveTransports.Of(_attemptPlan[_attemptIndex])", live, StringComparison.Ordinal);
        Assert.Contains("watch.NoteLive(", live, StringComparison.Ordinal);

        var probe = Assert.Single(AppSourceFile.LoadAll("PlayerWindow.BroadcastEnd.cs")).Masked;
        var probeBody = probe[probe.IndexOf("private async Task<BroadcastEndAnswer> ProbeLanAsync(", StringComparison.Ordinal)..];
        Assert.Contains("ReadsLanAnswers: true", probeBody, StringComparison.Ordinal);
        Assert.DoesNotContain("PlayedBefore", probeBody, StringComparison.Ordinal);
    }

    // RRA-1: the audio leg records the transport of each attempt before it starts that attempt's engine.
    [Fact]
    public void TheAudioLegRecordsTheTransportOfAnAttemptBeforeItStartsItsEngine()
    {
        var leg = Assert.Single(AppSourceFile.LoadAll("FastMediaSorterAudioLeg.cs")).Masked;

        var record = leg.IndexOf("_attemptTransport = BroadcastLiveTransports.Of(endpoint);", StringComparison.Ordinal);
        var start = leg.IndexOf("opening = _playback.StartAsync(", StringComparison.Ordinal);
        Assert.True(record > 0 && record < start, "the attempt's transport must be recorded before its engine starts");
        Assert.Contains("internal BroadcastLiveTransport AttemptTransport => _attemptTransport;", leg, StringComparison.Ordinal);
    }

    // S6-1 / RRA-2 / RRA-3: with no endpoint left, the Core rule decides whether a refusal goes to the bounded recovery (through
    // AdvanceOrRecover, which knows a playlist window), after the guards that stay verdicts (the stale-answer check, the end
    // reading, the password) and the endpoint advance, and before the verdict. The rule itself is held by BroadcastRecoveryHandOffTests.
    [Fact]
    public void AnOpenFailureOnALiveLegOrARelayStallGoesToTheBoundedRecoveryAfterTheGuardsAndTheAdvance()
    {
        var report = Assert.Single(AppSourceFile.LoadAll("PlayerWindow.Attempts.cs")).Masked;
        var body = report[report.IndexOf("private void ReportOpenFailure(", StringComparison.Ordinal)..];

        var stale = body.IndexOf("epoch != _attemptEpoch", StringComparison.Ordinal);
        var ended = body.IndexOf("NoteBroadcastAnswer(reason)", StringComparison.Ordinal);
        var password = body.IndexOf("IsPasswordRefusal(reason)", StringComparison.Ordinal);
        var advance = body.IndexOf("CanAdvanceAttempt()", StringComparison.Ordinal);
        var handOff = body.IndexOf("if (BroadcastAttemptAdvance.HandsToRecovery(_reachedLive, reason))", StringComparison.Ordinal);
        var call = body.IndexOf("AdvanceOrRecover(reason, new PlaybackFailureSignal(reason));", StringComparison.Ordinal);
        var verdict = body.LastIndexOf("ShowPlaybackFailure(reason);", StringComparison.Ordinal);

        Assert.True(stale >= 0 && stale < ended && ended < password && password < advance && advance < handOff && handOff < call && call < verdict,
            "ReportOpenFailure must judge the stale answer, the end, the password and the advance before the hand-off, and the hand-off before the verdict");
        Assert.DoesNotContain("RecoverAsync(", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAudioSessionReadsEveryStopAndEveryAnswerAndStopsFollowingWhenItIsNotPlaying()
    {
        var text = Assert.Single(AppSourceFile.LoadAll("BroadcastAudioSession.cs")).Text;

        // RRA-1: the leg tells which transport played, so a relay-played listener does not read a foreign LAN host as the end.
        Assert.Contains("_end.NoteLive(sender is FastMediaSorterAudioLeg leg ? leg.AttemptTransport : BroadcastLiveTransport.Unknown);",
            Method("BroadcastAudioSession.cs", "private void Playback_Playing("), StringComparison.Ordinal);
        Assert.DoesNotContain("_end.NoteLive();", text, StringComparison.Ordinal);
        Assert.Contains("EndedBy(_end.NoteStopped())", Method("BroadcastAudioSession.cs", "private void Playback_Ended("), StringComparison.Ordinal);
        Assert.Contains("_end.NoteAnswer(BroadcastEndAnswers.FromReason(reason))", Method("BroadcastAudioSession.cs", "private void HandleFailure("),
            StringComparison.Ordinal);
        Assert.Contains("ReleaseFollowLocked();", Method("BroadcastAudioSession.cs", "internal void Stop("), StringComparison.Ordinal);
        Assert.Contains("ReleaseFollowLocked();", Method("BroadcastAudioSession.cs", "private void End("), StringComparison.Ordinal);
    }

    [Fact]
    public void TheEndedStateIsOneLogLineThatNamesItsCauseForBothKindsOfWindow()
    {
        var player = Assert.Single(AppSourceFile.LoadAll("PlayerWindow.BroadcastEnd.cs")).Text;
        var audio = Assert.Single(AppSourceFile.LoadAll("BroadcastAudioSession.cs")).Text;

        Assert.Contains("\"PLAYBACK END\", $\"cause={BroadcastEndCauses.Name(verdict.Cause)}\", \"kind=video\"", player, StringComparison.Ordinal);
        Assert.Contains("[$\"cause={BroadcastEndCauses.Name(verdict.Cause)}\", \"kind=audio\"]", audio, StringComparison.Ordinal);
    }
}
