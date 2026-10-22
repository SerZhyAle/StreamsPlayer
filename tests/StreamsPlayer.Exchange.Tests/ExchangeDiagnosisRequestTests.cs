using StreamsPlayer.App;
using StreamsPlayer.Core;

namespace StreamsPlayer.Exchange.Tests;

/// <summary>SP-0225: which failure the check walks, chosen from the lines the screens already show.</summary>
public sealed class ExchangeDiagnosisRequestTests
{
    [Fact]
    public void TheLastAttemptsFailureOutranksAConnectionLineThatReadsOnline()
    {
        // A failed enrollment puts the previous receiver back, and that receiver reads "online" a second later.
        var request = ExchangeDiagnosisRequest.ForStatus("ExchangeBadCredentials", "ExchangeOnline", "studio", 443);

        Assert.Equal(ExchangeFailureRows.BadCredentials, request.Row);
        Assert.False(request.Connected);
        Assert.Equal("ExchangeBadCredentials", request.HappenedKey);
    }

    [Fact]
    public void AnOutcomeThatIsNotAFailureLeavesTheConnectionLineToDecide()
    {
        var cancelled = ExchangeDiagnosisRequest.ForStatus("ExchangeJoinCancelled", "ExchangeUnanswered", "studio", 443);

        Assert.Equal(ExchangeFailureRows.Unanswered, cancelled.Row);
    }

    [Fact]
    public void AConnectedServerNeedsNoWalk()
    {
        var request = ExchangeDiagnosisRequest.ForStatus(null, "ExchangeOnline", "studio", 443);

        Assert.True(request.Connected);
        Assert.Equal("ExchangeOnline", request.HappenedKey);
    }

    [Theory]
    [InlineData("ExchangeOff")]
    [InlineData("ExchangeConnecting")]
    [InlineData("ExchangeStorageFailed")]
    public void WhateverElseIsThePlainQuestionWhyItWontConnect(string status)
    {
        var request = ExchangeDiagnosisRequest.ForStatus(null, status, "studio", 443);

        Assert.Equal(ExchangeFailureRows.Unreachable, request.Row);
        Assert.False(request.Connected);
        Assert.Equal(ExchangeFailureRows.Unreachable.MessageKey, request.HappenedKey);
    }

    [Fact]
    public void ACertificateWaitingForItsFirstApprovalKeepsItsOwnLineAndTakesTheCertificateStep()
    {
        var request = ExchangeDiagnosisRequest.ForStatus(null, "ExchangeCertificate", "studio", 443);

        Assert.Equal(ExchangeDiagnosisStep.Certificate, request.Row.Step);
        Assert.Equal("ExchangeCertificate", request.HappenedKey);
    }

    [Fact]
    public void AServerThatIsNotTheAccountsIsAskedAboutFreshAndNeverReadsTheAccountsLines()
    {
        var request = ExchangeDiagnosisRequest.ForStatus("ExchangeBadCredentials", "ExchangeOnline", "other.example.org", 443,
            sameAsAccount: false);

        Assert.Equal(ExchangeFailureRows.Unreachable, request.Row);
        Assert.False(request.Connected);
    }

    [Theory]
    [InlineData("studio", 443, "studio:443")]
    [InlineData("::1", 8443, "[::1]:8443")]
    [InlineData("[::1]", 8443, "[::1]:8443")]
    public void TheAddressIsWrittenAsAPersonWritesIt(string host, int port, string expected)
    {
        Assert.Equal(expected, ExchangeDiagnosisRequest.ForStatus(null, "ExchangeOff", host, port).Endpoint);
    }
}
