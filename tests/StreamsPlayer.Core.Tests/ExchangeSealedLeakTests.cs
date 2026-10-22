using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0226 requirement 3 / DEVICE-EXCHANGE item DQ: no key and no box reaches a log line or a diagnostic text, and the
/// `enroll` frame carries a key only when there is one.
/// </summary>
public sealed class ExchangeSealedLeakTests
{
    [Theory]
    [InlineData("""{"type":"enroll","e2eKey":{"v":1,"kem":"BMHBNOi24fTvs6FVK82lwF9b1Kmd1yjW"}}""")]
    [InlineData("""{"sealed":{"v":1,"from":"d","fromKey":"AAAA","n":"y","ct":"SECRETCIPHERTEXT","keys":[]}}""")]
    [InlineData("""{"relayKey":"SECRETKEYMATERIAL","tunnelPin":"SHA256:abc"}""")]
    [InlineData("""endpoint seal={"v":1,"key":"SECRETKEYMATERIAL"}""")]
    [InlineData("""{"keys":[{"to":"x","enc":"SECRETENC","wk":"SECRETWK"}]}""")]
    [InlineData("""content-type: application/x-fmsx-sealed for broadcast""")]
    [InlineData("""device privateKey=8de947620593691cc83586e8db24bfa306b9d554e7434d453692909a828cfb7c""")]
    [InlineData("""sealedAccess box for resource""")]
    public void ALineThatNamesAKeyOrABoxIsMaskedWhole(string line)
    {
        var redacted = ExchangeDiagnosticRedactor.Redact("before\n" + line + "\nafter");

        Assert.Equal("before\n[REDACTED]\nafter", redacted);
    }

    [Fact]
    public void AnOrdinaryLineIsLeftAlone() =>
        Assert.Equal("playback started in 412 ms", ExchangeDiagnosticRedactor.Redact("playback started in 412 ms"));

    [Fact]
    public void TheEnrollFrameCarriesTheCapabilityAndKeyTogetherOrNeither()
    {
        using var pair = ExchangeE2eKeyPair.Generate();

        var keyed = ExchangeEnrollmentFields.Build("alice", "dev1", "PC", "streamsplayer-windows", "1", "windows",
            ["receiver"], new { modes = Array.Empty<string>() }, 30, pair);
        var legacy = ExchangeEnrollmentFields.Build("alice", "dev1", "PC", "streamsplayer-windows", "1", "windows",
            ["receiver"], new { modes = Array.Empty<string>() }, 30, null);

        using var keyedDocument = JsonSerializer.SerializeToDocument(keyed, ExchangeProtocol.JsonOptions);
        using var legacyDocument = JsonSerializer.SerializeToDocument(legacy, ExchangeProtocol.JsonOptions);
        Assert.Equal("e2e-v1", keyedDocument.RootElement.GetProperty("capabilities")[0].GetString());
        Assert.Equal(1, keyedDocument.RootElement.GetProperty("e2eKey").GetProperty("v").GetInt32());
        Assert.Equal(pair.Kem, keyedDocument.RootElement.GetProperty("e2eKey").GetProperty("kem").GetString());
        Assert.False(legacyDocument.RootElement.TryGetProperty("capabilities", out _));
        Assert.False(legacyDocument.RootElement.TryGetProperty("e2eKey", out _));
        Assert.Equal("enroll", legacyDocument.RootElement.GetProperty("type").GetString());
        Assert.Equal(2, legacyDocument.RootElement.GetProperty("schemaVersion").GetInt32());
        // The credential is added by the writer, never here.
        Assert.False(keyedDocument.RootElement.TryGetProperty("password", out _));
    }
}
