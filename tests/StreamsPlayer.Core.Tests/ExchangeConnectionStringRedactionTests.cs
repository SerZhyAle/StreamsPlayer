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
}
