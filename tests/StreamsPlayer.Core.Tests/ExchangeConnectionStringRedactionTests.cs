using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class ExchangeConnectionStringRedactionTests
{
    [Theory]
    [InlineData("parse FMSEXCH1:x=secret-host.test:80&fp=secret-pin&n=Secret name&u=secret-user")]
    [InlineData("parse FMSEXCH2:x=secret-host.test:80")]
    [InlineData("Unable to read C:\\Secret folder\\Private server.fmsexch")]
    [InlineData("Unable to read \"C:\\Secret folder\\Private server.FMSEXCH\"")]
    public void CarrierOrPath_MasksWholeLineButKeepsOtherDiagnostics(string text)
    {
        Assert.Equal("before\n[REDACTED]\nafter", ExchangeDiagnosticRedactor.Redact("before\n" + text + "\nafter"));
    }

    [Theory]
    [InlineData("x=secret-host.test:80 fp=secret-pin n=SecretName u=secret-user")]
    [InlineData("{\"x\":\"secret-host.test:80\",\"fp\":\"secret-pin\",\"n\":\"SecretName\",\"u\":\"secret-user\"}")]
    [InlineData("x = secret-host.test:80 &fp=secret-pin&n=SecretName&u=secret-user")]
    public void DetachedKnownFields_AreMasked(string text)
    {
        var redacted = ExchangeDiagnosticRedactor.Redact(text);
        foreach (var value in new[] { "secret-host", "secret-pin", "SecretName", "secret-user" })
            Assert.DoesNotContain(value, redacted);
        Assert.Contains("[REDACTED]", redacted);
    }

    [Fact]
    public void OrdinaryDiagnostics_ArePreserved() =>
        Assert.Equal("index=3 duration=25 source=radio", ExchangeDiagnosticRedactor.Redact("index=3 duration=25 source=radio"));

    /// <summary>
    /// SP-0293: the 32-hex path of a loopback leg - the route token an engine can echo inside its own error
    /// message - is masked in free text with the head (scheme, loopback host, port) kept.
    /// </summary>
    [Theory]
    [InlineData(
        "VLC | level=Error | module=http | msg=http stream error: cannot open http://127.0.0.1:51722/0123456789ABCDEF0123456789ABCDEF",
        "VLC | level=Error | module=http | msg=http stream error: cannot open http://127.0.0.1:51722/[REDACTED]")]
    [InlineData(
        "open rtsp://127.0.0.1:8054/0123456789ABCDEF0123456789ABCDEF now",
        "open rtsp://127.0.0.1:8054/[REDACTED] now")]
    [InlineData(
        "open http://[::1]/0123456789ABCDEF0123456789ABCDEF now",
        "open http://[::1]/[REDACTED] now")]
    [InlineData(
        "open http://localhost:2/0123456789abcdef0123456789abcdef now",
        "open http://localhost:2/[REDACTED] now")]
    [InlineData(
        "open http://127.0.0.1:2/0123456789ABCDEF0123456789ABCDEF/stream now",
        "open http://127.0.0.1:2/[REDACTED]/stream now")]
    public void TheLoopbackRouteTokenIsMaskedWhereverItSits(string line, string expected) =>
        Assert.Equal(expected, ExchangeDiagnosticRedactor.Redact(line));

    [Theory]
    [InlineData("open http://127.0.0.1:51722/welcome now")]
    [InlineData("open http://127.0.0.1:51722/0123456789ABCDEF0123456789ABCDE now")]
    [InlineData("open http://192.168.1.4:51722/0123456789ABCDEF0123456789ABCDEF now")]
    [InlineData("open http://127.0.0.1:51722/0123456789ABCDEF0123456789ABCDEF012 now")]
    public void AnOrdinaryPathOrAnotherHostIsLeftAsItWas(string line) =>
        Assert.DoesNotContain("[REDACTED]", ExchangeDiagnosticRedactor.Redact(line));

    [Fact]
    public void ALoopbackEchoMasksItsLineAndKeepsTheLinesAroundIt() =>
        Assert.Equal(
            "before\r\nurl=http://127.0.0.1:51722/[REDACTED]\r\nafter",
            ExchangeDiagnosticRedactor.Redact(
                "before\r\nurl=http://127.0.0.1:51722/0123456789ABCDEF0123456789ABCDEF\r\nafter"));
}
