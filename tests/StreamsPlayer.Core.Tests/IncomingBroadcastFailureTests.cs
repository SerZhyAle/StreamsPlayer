using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0222 requirement 3 and 4: the failures of an incoming broadcast that end its leg at once with a message
/// (and so are never retried in the background), against the ones that spend the bounded recovery budget.
/// </summary>
public sealed class IncomingBroadcastFailureTests
{
    [Theory]
    [InlineData(ExchangeBroadcastAuth.PasswordRequiredReason, null, "BroadcastPasswordRequired")]
    [InlineData(ExchangeBroadcastAuth.PasswordRefusedReason, 401, "BroadcastPasswordRefused")]
    [InlineData(ExchangeBroadcastCertificateException.Refused, null, "BroadcastCertificateMismatch")]
    [InlineData(ExchangeBroadcastCertificateException.UntrustedWithoutFingerprint, null, "BroadcastCertificateUntrusted")]
    [InlineData(ExchangeBroadcastResources.CapacityReason, null, IncomingBroadcastFailure.BoundReachedKey)]
    [InlineData("HttpRequestException", 503, IncomingBroadcastFailure.ListenerLimitKey)]
    public void ARefusalNoReconnectCanMendEndsTheLegWithItsMessage(string reason, int? status, string expectedKey)
    {
        Assert.Equal(expectedKey, IncomingBroadcastFailure.TerminalMessageKey(reason, status));
    }

    [Theory]
    [InlineData("end_reached", null)]
    [InlineData("HttpRequestException", null)]
    [InlineData("HttpRequestException", 500)]
    [InlineData("decoder_error", 200)]
    [InlineData("refused", 404)]
    [InlineData(null, null)]
    public void AnythingElseSpendsTheRecoveryBudgetInstead(string? reason, int? status)
    {
        Assert.Null(IncomingBroadcastFailure.TerminalMessageKey(reason, status));
    }

    [Fact]
    public void OnlyARefusedOrMissingPasswordIsAPasswordRefusal()
    {
        Assert.True(IncomingBroadcastFailure.IsPasswordRefusal(ExchangeBroadcastAuth.PasswordRefusedReason));
        Assert.True(IncomingBroadcastFailure.IsPasswordRefusal(ExchangeBroadcastAuth.PasswordRequiredReason));
        Assert.False(IncomingBroadcastFailure.IsPasswordRefusal(ExchangeBroadcastCertificateException.Refused));
        Assert.False(IncomingBroadcastFailure.IsPasswordRefusal(null));
    }

    [Fact]
    public void TheBoundRefusalIsATypeAndItsTextIsTheReason()
    {
        var refusal = new ExchangeBroadcastCapacityException();

        Assert.IsAssignableFrom<InvalidOperationException>(refusal);
        Assert.Equal(ExchangeBroadcastResources.CapacityReason, refusal.Message);
    }

    [Fact]
    public void EveryMessageKeyTheWindowsMayShowExistsInEveryLanguage()
    {
        var keys = new[]
        {
            IncomingBroadcastFailure.NoLongerAvailableKey, IncomingBroadcastFailure.BoundReachedKey,
            IncomingBroadcastFailure.ListenerLimitKey, "BroadcastPasswordRequired", "BroadcastPasswordRefused",
            "BroadcastCertificateMismatch", "BroadcastCertificateUntrusted"
        };

        foreach (var dictionary in LocalizationDictionary.LoadAll())
        {
            foreach (var key in keys)
            {
                Assert.True(dictionary.Values.ContainsKey(key), $"{dictionary.Code}/{key}");
            }
        }
    }
}
