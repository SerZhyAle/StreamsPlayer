using System.Text.RegularExpressions;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0284 (STREAM-BANK 2.10, item AB): a stream whose security certificate the engine refuses is a hard failure
/// surfaced at once, on the radio and on the video path, and spends no recovery attempt. The engine's own log is
/// the source of the finding - measured on both LibVLC engines against a local self-signed https endpoint on
/// 2026-11-17 - so the Core pieces are pinned with those exact lines, and the App's wiring is held over its own
/// sources (the App has no test project).
/// </summary>
public sealed class PlaybackUntrustedCertificateTests
{
    // What LibVLC 3.0.23 logged at error level, in order, before it raised the error event (radio and video alike).
    private static readonly (string Module, string Message)[] MeasuredTrail =
    [
        ("main", "connection error: No such file or directory"),
        ("main", "TLS session handshake error"),
        ("gnutls", "Certificate verification failure: The certificate is NOT trusted. The certificate issuer is unknown. "),
        ("access", "HTTP connection failure"),
        ("main", "Your input can't be opened"),
        ("main", "VLC is unable to open the MRL 'https://127.0.0.1:18443/live.mp3'. Check the log for details."),
    ];

    private static EngineFailureTrail Fed(params (string Module, string Message)[] lines)
    {
        var trail = new EngineFailureTrail();
        foreach (var (module, message) in lines)
        {
            trail.Observe(module, message);
        }

        return trail;
    }

    private static PlaybackFailureSignal CertificateSignal(string reason = PlaybackFailureSignal.UntrustedCertificateReason) =>
        new(reason, UntrustedCertificate: true);

    // ---- the engine's finding -------------------------------------------------------------------------------

    [Fact]
    public void TheMeasuredTrail_NamesARefusedCertificate_AndKeepsItsCauseForTheLog()
    {
        var trail = Fed(MeasuredTrail);

        Assert.True(trail.UntrustedCertificate);
        Assert.Contains("Certificate verification failure: The certificate is NOT trusted.", trail.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusedConnection_IsNotARefusedCertificate()
    {
        var trail = Fed(
            ("access", "HTTP connection failure"),
            ("main", "connection failed: Connection refused by peer"),
            ("http", "cannot connect to 127.0.0.1:54458"),
            ("main", "Your input can't be opened"));

        Assert.False(trail.UntrustedCertificate);
    }

    [Theory]
    [InlineData("main", "Certificate verification failure: The certificate is NOT trusted.")] // only the TLS module's verdict counts
    [InlineData("gnutls", "TLS session handshake error")] // a handshake that failed for another reason says nothing of trust
    [InlineData("http", "cannot resolve certificate-verification-failure.example: No such host is known.")] // a host's name is not a finding
    [InlineData("access", "HTTP 404 error")]
    public void AnythingElseTheEngineSays_IsNotARefusedCertificate(string module, string message)
    {
        Assert.False(Fed((module, message)).UntrustedCertificate);
        Assert.False(EngineFailureTrail.IsCertificateRefusal(module, message));
    }

    [Fact]
    public void Clear_ForgetsTheFinding_SoOneConnectionNeverEndsTheNext()
    {
        var trail = Fed(MeasuredTrail);

        trail.Clear();

        Assert.False(trail.UntrustedCertificate);
        Assert.Null(trail.Describe());
    }

    [Fact]
    public void TheDetector_NeverThrowsOnMissingText()
    {
        Assert.False(EngineFailureTrail.IsCertificateRefusal(null, null));
        Assert.False(EngineFailureTrail.IsCertificateRefusal("gnutls", null));
        Assert.False(EngineFailureTrail.IsCertificateRefusal(null, "Certificate verification failure"));
    }

    // ---- the verdict -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(PlaybackFailureSignal.UntrustedCertificateReason)]
    [InlineData("encountered_error")]
    [InlineData("connection refused")]
    [InlineData("timeout")]
    public void Classify_AnUntrustedCertificateIsFinalWhateverItsReason(string reason)
    {
        Assert.Equal(RecoveryTrigger.HardFail, PlaybackRecoveryClassifier.Classify(CertificateSignal(reason)));
    }

    [Fact]
    public void Classify_AnUntrustedCertificateOutranksARetryableStatus()
    {
        // The rule is ahead of the HTTP-status rules: a 503 found by a probe cannot reopen a refused certificate.
        Assert.Equal(RecoveryTrigger.HardFail, PlaybackRecoveryClassifier.Classify(
            new PlaybackFailureSignal("encountered_error", HttpStatusCode: 503, UntrustedCertificate: true)));
    }

    [Fact]
    public void Classify_TheCertificateWordInAReasonIsNotTheFinding()
    {
        // The flag is the engine's own finding; free text that happens to say "certificate" keeps the ladder.
        Assert.Equal(RecoveryTrigger.Transient, PlaybackRecoveryClassifier.Classify(new PlaybackFailureSignal("certificate")));
    }

    [Fact]
    public void Decide_ACertificateRefusalHardFailsAtOnce_AndSpendsNoAttempt()
    {
        var policy = new LivePlaybackRecoveryPolicy();

        var refused = policy.Decide(CertificateSignal());
        var next = policy.Decide(new PlaybackFailureSignal("encountered_error"));

        Assert.Equal(RecoveryActionKind.HardFail, refused.Kind);
        Assert.Equal(RecoveryTrigger.HardFail, refused.Trigger);
        Assert.Equal(0, refused.Attempt);
        // The refusal left the transient budget whole: the next ordinary failure is still its first of two.
        Assert.Equal(RecoveryActionKind.Reconnect, next.Kind);
        Assert.Equal(1, next.Attempt);
    }

    [Fact]
    public void TheShareableReport_FilesARefusedCertificateUnderNetwork()
    {
        Assert.Equal(PlaybackErrorCategory.Network, PlaybackErrorClassifier.Classify(PlaybackFailureSignal.UntrustedCertificateReason));
    }

    // ---- the App's wiring, over its own sources -------------------------------------------------------------

    private static string Code(string file) =>
        Regex.Replace(Assert.Single(AppSourceFile.LoadAll(file)).Masked, @"\s+", " ");

    [Fact]
    public void TheRadioEngine_ReadsTheFindingFromItsTrail_AndNamesTheFailureByIt()
    {
        var engine = Code("StandardAudioPlayback.cs");

        Assert.Contains("_logFailureTrail.UntrustedCertificate", engine, StringComparison.Ordinal);
        Assert.Contains("EngineError(connection, cause, untrustedCertificate)", engine, StringComparison.Ordinal);
        Assert.Contains("untrustedCertificate ? PlaybackFailureSignal.UntrustedCertificateReason : EngineErrorReason", engine, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRadioRecovery_PassesTheFindingToThePolicy_AndSkipsTheProbes()
    {
        var failed = Code("MainWindow.xaml.cs");
        Assert.Contains("untrustedCertificate: e.UntrustedCertificate", failed, StringComparison.Ordinal);

        var recovery = Code("MainWindow.AudioRecovery.cs");
        Assert.Contains("UntrustedCertificate: untrustedCertificate", recovery, StringComparison.Ordinal);
        Assert.Contains("&& !stall && !untrustedCertificate)", recovery, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRadioVerdict_NamesTheCertificate_InTheDialogAndOnTheCompactLine()
    {
        var text = Assert.Single(AppSourceFile.LoadAll("MainWindow.xaml.cs")).Text;

        Assert.Contains("LocalizationService.Format(\"PlaybackUntrustedCertificate\", displayTitle)", text, StringComparison.Ordinal);
        Assert.Contains("certificateRefused ? \"PlaybackUntrustedCertificate\" : \"CompactPanelStreamFailed\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheVideoEngine_SetsTheFindingFromItsLog_AndClearsItWithEachOpen()
    {
        var text = Assert.Single(AppSourceFile.LoadAll("LibVlcVideoBackend.cs")).Text;

        Assert.Contains("EngineFailureTrail.IsCertificateRefusal(e.Module, e.Message)", text, StringComparison.Ordinal);
        var play = text.IndexOf("public bool Play(", StringComparison.Ordinal);
        var reset = text.IndexOf("_certificateRefused = false;", play, StringComparison.Ordinal);
        var start = text.IndexOf("_mediaPlayer.Play(next)", play, StringComparison.Ordinal);
        Assert.True(play >= 0 && reset > play && reset < start, "Play must clear the finding before it starts the new media");
    }

    [Fact]
    public void TheVideoPlayer_ReadsTheFindingWhereTheErrorIsRaised_FeedsTheSignal_AndSkipsTheProbes()
    {
        var window = Code("PlayerWindow.xaml.cs");

        // Before the dispatcher hop: the next open clears the finding.
        var read = window.IndexOf("var certificateRefused = source.RejectedCertificate;", StringComparison.Ordinal);
        var hop = window.IndexOf("Dispatcher.BeginInvoke", read, StringComparison.Ordinal);
        Assert.True(read >= 0 && hop > read, "the finding must be read before the hop to the UI thread");
        Assert.Contains("new PlaybackFailureSignal(PlaybackFailureSignal.UntrustedCertificateReason, UntrustedCertificate: true)", window, StringComparison.Ordinal);
        Assert.Contains("!signal.UntrustedCertificate", window, StringComparison.Ordinal);
        Assert.Contains("LocalizationService.Format(\"PlaybackUntrustedCertificate\"", Assert.Single(AppSourceFile.LoadAll("PlayerWindow.xaml.cs")).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryVideoBackend_AnswersTheQuestion()
    {
        Assert.Contains("bool RejectedCertificate { get; }", Code("IVideoBackend.cs"), StringComparison.Ordinal);
        Assert.Contains("public bool RejectedCertificate =>", Code("LibVlcVideoBackend.cs"), StringComparison.Ordinal);
        Assert.Contains("public bool RejectedCertificate => false;", Code("FlyleafVideoBackend.cs"), StringComparison.Ordinal);
    }
}
