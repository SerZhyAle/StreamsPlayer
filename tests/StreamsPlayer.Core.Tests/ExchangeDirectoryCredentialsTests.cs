using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0222 reading 3: the directory reader takes the pair a record hands an enrolled device for a protected
/// broadcast (DEVICE-EXCHANGE 6.3, items BF and BD), and that pair is a secret - read into memory, printed
/// nowhere.
/// </summary>
public sealed class ExchangeDirectoryCredentialsTests
{
    private const string Password = "p4ss-w0rd-that-must-not-leak";

    [Fact]
    public void ARecordThatCarriesAPairHandsItToTheBroadcast()
    {
        var view = Single(Record($$"""{"user":"fms","password":"{{Password}}"}"""));

        Assert.Equal(new ExchangeRelayCredentials("fms", Password), view.Record.Credentials);
        Assert.Equal(ExchangeBroadcastSupport.Supported, view.Support);
    }

    [Theory]
    [InlineData("""null""")]
    [InlineData("\"a pair\"")]
    [InlineData("""{"user":"fms"}""")]
    [InlineData("""{"password":"x"}""")]
    [InlineData("""{"user":"","password":"x"}""")]
    [InlineData("""{"user":"fms","password":""}""")]
    [InlineData("""{"user":7,"password":"x"}""")]
    [InlineData("""{"user":"fms","password":["x"]}""")]
    [InlineData("""{"sealed":"opaque-box"}""")]
    public void AnyOtherShapeReadsAsNoPairAndKeepsTheRecord(string credentials)
    {
        var view = Single(Record(credentials));

        Assert.Null(view.Record.Credentials);
        Assert.Equal("b1", view.Record.BroadcastId);
    }

    [Fact]
    public void APairOutsideTheLimitsReadsAsNoPair()
    {
        var longUser = new string('u', ExchangeRelayCredentials.MaximumUserLength + 1);
        var longPassword = new string('p', ExchangeRelayCredentials.MaximumPasswordLength + 1);

        Assert.Null(Single(Record($$"""{"user":"{{longUser}}","password":"x"}""")).Record.Credentials);
        Assert.Null(Single(Record($$"""{"user":"fms","password":"{{longPassword}}"}""")).Record.Credentials);
    }

    [Fact]
    public void ARecordWithoutAPairIsUnchanged()
    {
        Assert.Null(Single(Record(null)).Record.Credentials);
    }

    [Fact]
    public void NothingThatPrintsTheRecordPrintsThePassword()
    {
        var view = Single(Record($$"""{"user":"fms","password":"{{Password}}"}"""));

        Assert.DoesNotContain(Password, view.Record.Credentials!.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Password, view.Record.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Password, view.ToString(), StringComparison.Ordinal);
    }

    private static ExchangeBroadcastView Single(string broadcastJson)
    {
        var state = new ExchangeDirectoryState();
        using var document = JsonDocument.Parse($$"""
            {"schemaVersion":2,"type":"directory","revision":7,
             "devices":[{"deviceId":"phone","deviceName":"Pixel 8","presence":"online"}],
             "broadcasts":[{{broadcastJson}}]}
            """);
        state.ApplyFull(document.RootElement);
        return Assert.Single(Assert.Single(state.Snapshot.Groups).Broadcasts);
    }

    private static string Record(string? credentials) => $$$"""
        {"broadcastId":"b1","deviceId":"phone","title":"Kitchen","mode":"AUDIO_ONLY"
         {{{(credentials is null ? "" : ",\"credentials\":" + credentials)}}},
         "descriptor":{"schemaVersion":1,"url":"https://exchange.example.net:44022/v2/b/ICEiIyQlJicoKSorLC0uLw/stream",
                       "title":"Kitchen","mode":"AUDIO_ONLY","sourceId":"AAECAwQFBgcICQoLDA0ODw","isLive":true,
                       "endpoints":[{"url":"https://exchange.example.net:44022/v2/b/ICEiIyQlJicoKSorLC0uLw/stream",
                                     "transport":"RELAY","mode":"AUDIO_ONLY","targetLatencyMs":2000,"seal":{"v":1}}]}}
        """;
}
