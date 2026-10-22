using System.Net.Sockets;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0225: the ladder of <c>DEVICE-EXCHANGE</c> item CY, the rows of item CZ this product raises, and the walk.
/// The expected values are the contract's table written out, so a change to either side fails here.
/// </summary>
public sealed class ExchangeDiagnosisTests
{
    [Theory]
    [InlineData(ExchangeDiagnosisStep.PcAsleep, 1, "pc-asleep", ExchangeDiagnosisApplies.Any)]
    [InlineData(ExchangeDiagnosisStep.AddressLan, 2, "address-lan", ExchangeDiagnosisApplies.Network)]
    [InlineData(ExchangeDiagnosisStep.GuestNetwork, 3, "guest-network", ExchangeDiagnosisApplies.Network)]
    [InlineData(ExchangeDiagnosisStep.Vpn, 4, "vpn", ExchangeDiagnosisApplies.Any)]
    [InlineData(ExchangeDiagnosisStep.WindowsPublicNetwork, 5, "windows-public-network", ExchangeDiagnosisApplies.Network)]
    [InlineData(ExchangeDiagnosisStep.IpFamily, 6, "ip-family", ExchangeDiagnosisApplies.Any)]
    [InlineData(ExchangeDiagnosisStep.ServerInternet, 7, "server-internet", ExchangeDiagnosisApplies.Internet)]
    [InlineData(ExchangeDiagnosisStep.PortForward, 8, "port-forward", ExchangeDiagnosisApplies.Internet)]
    [InlineData(ExchangeDiagnosisStep.DeviceClock, 9, "device-clock", ExchangeDiagnosisApplies.Any)]
    [InlineData(ExchangeDiagnosisStep.Certificate, 10, "certificate", ExchangeDiagnosisApplies.Any)]
    [InlineData(ExchangeDiagnosisStep.Credentials, 11, "credentials", ExchangeDiagnosisApplies.Any)]
    [InlineData(ExchangeDiagnosisStep.SignInRate, 12, "sign-in-rate", ExchangeDiagnosisApplies.Any)]
    [InlineData(ExchangeDiagnosisStep.Invitation, 13, "invitation", ExchangeDiagnosisApplies.Any)]
    [InlineData(ExchangeDiagnosisStep.DeviceRemoved, 14, "device-removed", ExchangeDiagnosisApplies.Any)]
    [InlineData(ExchangeDiagnosisStep.Operator, 15, "operator", ExchangeDiagnosisApplies.Any)]
    public void TheLadderIsItemCyInOrder(ExchangeDiagnosisStep step, int number, string id, ExchangeDiagnosisApplies applies)
    {
        Assert.Equal(number, (int)step);
        Assert.Equal(id, ExchangeDiagnosisSteps.IdOf(step));
        Assert.Equal(applies, ExchangeDiagnosisSteps.AppliesOf(step));
        Assert.Equal(step, ExchangeDiagnosisSteps.All[number - 1]);
    }

    [Fact]
    public void ThereAreFifteenStepsAndEveryKeyIsUnique()
    {
        Assert.Equal(15, ExchangeDiagnosisSteps.All.Count);
        Assert.Equal(15, ExchangeDiagnosisSteps.All.Select(ExchangeDiagnosisSteps.MessageKey).Distinct().Count());
        Assert.Equal(15, ExchangeDiagnosisSteps.All.Select(ExchangeDiagnosisSteps.IdOf).Distinct().Count());
    }

    [Theory]
    [InlineData("bad-credentials", ExchangeFailureKind.Wire, ExchangeDiagnosisStep.Credentials, false)]
    [InlineData("bad-credentials-code", ExchangeFailureKind.Wire, ExchangeDiagnosisStep.Credentials, false)]
    [InlineData("device-revoked", ExchangeFailureKind.Wire, ExchangeDiagnosisStep.DeviceRemoved, false)]
    [InlineData("signed-out", ExchangeFailureKind.Wire, ExchangeDiagnosisStep.Credentials, false)]
    [InlineData("version-unsupported", ExchangeFailureKind.Wire, ExchangeDiagnosisStep.Operator, false)]
    [InlineData("tls-required", ExchangeFailureKind.Wire, ExchangeDiagnosisStep.Operator, false)]
    [InlineData("id-collision", ExchangeFailureKind.Wire, null, false)]
    [InlineData("capacity", ExchangeFailureKind.Wire, ExchangeDiagnosisStep.Operator, false)]
    [InlineData("port-unavailable", ExchangeFailureKind.Wire, ExchangeDiagnosisStep.Operator, false)]
    [InlineData("unavailable", ExchangeFailureKind.Wire, ExchangeDiagnosisStep.PcAsleep, false)]
    [InlineData("rate-limited", ExchangeFailureKind.Wire, ExchangeDiagnosisStep.SignInRate, false)]
    [InlineData("password-policy", ExchangeFailureKind.Wire, null, false)]
    [InlineData("limit-reached", ExchangeFailureKind.Wire, ExchangeDiagnosisStep.Operator, false)]
    [InlineData("not-permitted", ExchangeFailureKind.Wire, null, false)]
    [InlineData("refused", ExchangeFailureKind.Wire, ExchangeDiagnosisStep.Operator, false)]
    [InlineData("declined", ExchangeFailureKind.Cast, null, false)]
    [InlineData("timeout", ExchangeFailureKind.Cast, null, false)]
    [InlineData("unsupported", ExchangeFailureKind.Cast, null, false)]
    [InlineData("unreachable-server", ExchangeFailureKind.Local, ExchangeDiagnosisStep.PcAsleep, true)]
    [InlineData("unanswered", ExchangeFailureKind.Local, ExchangeDiagnosisStep.Operator, true)]
    [InlineData("tls-failed", ExchangeFailureKind.Local, ExchangeDiagnosisStep.DeviceClock, true)]
    [InlineData("certificate-changed", ExchangeFailureKind.Local, ExchangeDiagnosisStep.Certificate, false)]
    [InlineData("empty-credential", ExchangeFailureKind.Local, null, false)]
    [InlineData("format-unsupported", ExchangeFailureKind.Local, null, false)]
    public void EveryRowIsItsRowOfItemCz(string code, ExchangeFailureKind kind, ExchangeDiagnosisStep? step, bool walked)
    {
        var row = ExchangeFailureRows.ByCode(code);

        Assert.NotNull(row);
        Assert.Equal(kind, row.Kind);
        Assert.Equal(step, row.Step);
        Assert.Equal(walked, row.Walked);
    }

    [Fact]
    public void TheRowsAreTheTwentyFourThisProductRaisesWithUniqueCodesAndKeys()
    {
        Assert.Equal(24, ExchangeFailureRows.All.Count);
        Assert.Equal(24, ExchangeFailureRows.All.Select(row => row.Code).Distinct().Count());
        Assert.Equal(24, ExchangeFailureRows.All.Select(row => row.MessageKey).Distinct().Count());
    }

    [Theory]
    [InlineData("bad-credentials", false, "bad-credentials")]
    [InlineData("bad-credentials", true, "bad-credentials-code")]
    [InlineData("device-revoked", false, "device-revoked")]
    [InlineData("signed-out", false, "signed-out")]
    [InlineData("a-reason-from-the-future", false, "refused")]
    [InlineData("", false, "refused")]
    [InlineData(null, false, "refused")]
    // Known to the contract, but raised only by surfaces this product does not have.
    [InlineData("protection-unsupported", false, "refused")]
    [InlineData("invitation-invalid", false, "refused")]
    public void AWireReasonIsItsRowAndAnUnknownOneIsTheGenericRefusal(string? reason, bool oneTimeCode, string code)
    {
        Assert.Equal(code, ExchangeFailureRows.OfWire(reason, oneTimeCode).Code);
    }

    [Theory]
    [InlineData("declined", "declined")]
    [InlineData("timeout", "timeout")]
    [InlineData("unsupported", "unsupported")]
    [InlineData("unavailable", "unavailable")]
    [InlineData("anything-else", "unavailable")]
    [InlineData(null, "unavailable")]
    public void ACastReasonIsItsRow(string? reason, string code)
    {
        Assert.Equal(code, ExchangeFailureRows.OfCast(reason).Code);
    }

    [Theory]
    [InlineData("127.0.0.1", ExchangeDiagnosisApplies.Network)]
    [InlineData("[::1]", ExchangeDiagnosisApplies.Network)]
    [InlineData("localhost", ExchangeDiagnosisApplies.Network)]
    [InlineData("10.1.2.3", ExchangeDiagnosisApplies.Network)]
    [InlineData("172.16.0.9", ExchangeDiagnosisApplies.Network)]
    [InlineData("172.32.0.9", ExchangeDiagnosisApplies.Internet)]
    [InlineData("192.168.1.20", ExchangeDiagnosisApplies.Network)]
    [InlineData("169.254.3.4", ExchangeDiagnosisApplies.Network)]
    [InlineData("100.64.1.1", ExchangeDiagnosisApplies.Network)]
    [InlineData("8.8.8.8", ExchangeDiagnosisApplies.Internet)]
    [InlineData("fe80::1", ExchangeDiagnosisApplies.Network)]
    [InlineData("fd12:3456::1", ExchangeDiagnosisApplies.Network)]
    [InlineData("2001:db8::1", ExchangeDiagnosisApplies.Internet)]
    [InlineData("studio", ExchangeDiagnosisApplies.Network)]
    [InlineData("nas.local", ExchangeDiagnosisApplies.Network)]
    [InlineData("box.home.arpa", ExchangeDiagnosisApplies.Network)]
    [InlineData("exchange.example.org", ExchangeDiagnosisApplies.Internet)]
    [InlineData("exchange.example.org.", ExchangeDiagnosisApplies.Internet)]
    [InlineData("", ExchangeDiagnosisApplies.Network)]
    public void TheRouteIsDecidedFromTheWrittenAddressAlone(string host, ExchangeDiagnosisApplies expected)
    {
        Assert.Equal(expected, ExchangeDiagnosisRoute.Of(host));
    }

    [Fact]
    public void TheFamilyIsKnownForALiteralOnly()
    {
        Assert.Equal(AddressFamily.InterNetwork, ExchangeDiagnosisRoute.FamilyOf("10.0.0.1"));
        Assert.Equal(AddressFamily.InterNetworkV6, ExchangeDiagnosisRoute.FamilyOf("[fe80::1]"));
        Assert.Null(ExchangeDiagnosisRoute.FamilyOf("example.org"));
    }

    // ---- the walk ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AWalkedRowStopsAtTheFirstFailedStepAndMarksTheRestNotReached()
    {
        var probes = new ScriptedProbes(ExchangeDiagnosisStep.Vpn, ExchangeProbeOutcome.Failed);

        var result = await ExchangeDiagnosisEngine.RunAsync(ExchangeFailureRows.Unreachable, "192.168.1.20", 443, probes,
            CancellationToken.None);

        Assert.Equal(ExchangeDiagnosisStep.Vpn, result.AdviceStep);
        Assert.Equal(ExchangeDiagnosisState.Passed, result.StateOf(ExchangeDiagnosisStep.PcAsleep));
        Assert.Equal(ExchangeDiagnosisState.Passed, result.StateOf(ExchangeDiagnosisStep.AddressLan));
        Assert.Equal(ExchangeDiagnosisState.CouldNotCheck, result.StateOf(ExchangeDiagnosisStep.GuestNetwork));
        Assert.Equal(ExchangeDiagnosisState.Failed, result.StateOf(ExchangeDiagnosisStep.Vpn));
        Assert.Equal(ExchangeDiagnosisState.NotReached, result.StateOf(ExchangeDiagnosisStep.WindowsPublicNetwork));
        Assert.Equal(ExchangeDiagnosisState.NotReached, result.StateOf(ExchangeDiagnosisStep.Operator));
        // No probe ran past the failure.
        Assert.DoesNotContain(ExchangeDiagnosisStep.IpFamily, probes.Probed);
        Assert.Equal(ExchangeDiagnosisStep.Vpn, probes.Probed[^1]);
    }

    [Fact]
    public async Task AStepThatDoesNotBelongToTheRouteIsNotApplicableAndNeverPassed()
    {
        var probes = new ScriptedProbes();

        var result = await ExchangeDiagnosisEngine.RunAsync(ExchangeFailureRows.Unreachable, "exchange.example.org", 443,
            probes, CancellationToken.None);

        Assert.Equal(ExchangeDiagnosisApplies.Internet, result.Route);
        foreach (var step in new[] { ExchangeDiagnosisStep.AddressLan, ExchangeDiagnosisStep.GuestNetwork,
                     ExchangeDiagnosisStep.WindowsPublicNetwork })
        {
            Assert.Equal(ExchangeDiagnosisState.NotApplicable, result.StateOf(step));
            Assert.DoesNotContain(step, probes.Probed);
        }

        Assert.Equal(ExchangeDiagnosisState.CouldNotCheck, result.StateOf(ExchangeDiagnosisStep.ServerInternet));
    }

    [Fact]
    public async Task TheInternetStepsAreNotApplicableOnTheOwnNetwork()
    {
        var result = await ExchangeDiagnosisEngine.RunAsync(ExchangeFailureRows.Unreachable, "192.168.1.20", 443,
            new ScriptedProbes(), CancellationToken.None);

        Assert.Equal(ExchangeDiagnosisState.NotApplicable, result.StateOf(ExchangeDiagnosisStep.ServerInternet));
        Assert.Equal(ExchangeDiagnosisState.NotApplicable, result.StateOf(ExchangeDiagnosisStep.PortForward));
    }

    [Fact]
    public async Task AnInvitationIsNeverPutToAServerHereSoItsStepIsSkipped()
    {
        var result = await ExchangeDiagnosisEngine.RunAsync(ExchangeFailureRows.BadCredentials, "192.168.1.20", 443,
            new ScriptedProbes(), CancellationToken.None);

        Assert.Equal(ExchangeDiagnosisState.NotApplicable, result.StateOf(ExchangeDiagnosisStep.Invitation));
    }

    [Fact]
    public async Task AWireRefusalProvesTheLinkAndProbesNothing()
    {
        var probes = new ScriptedProbes();

        var result = await ExchangeDiagnosisEngine.RunAsync(ExchangeFailureRows.BadCredentials, "exchange.example.org",
            443, probes, CancellationToken.None);

        Assert.Empty(probes.Probed);
        Assert.Equal(ExchangeDiagnosisStep.Credentials, result.AdviceStep);
        foreach (var step in ExchangeDiagnosisSteps.All.Where(step => step <= ExchangeDiagnosisStep.Certificate
                     && ExchangeDiagnosisSteps.AppliesOf(step) != ExchangeDiagnosisApplies.Network))
        {
            Assert.Equal(ExchangeDiagnosisState.Passed, result.StateOf(step));
        }

        Assert.Equal(ExchangeDiagnosisState.Failed, result.StateOf(ExchangeDiagnosisStep.Credentials));
        Assert.Equal(ExchangeDiagnosisState.NotReached, result.StateOf(ExchangeDiagnosisStep.SignInRate));
        Assert.Equal(ExchangeDiagnosisState.NotReached, result.StateOf(ExchangeDiagnosisStep.Operator));
    }

    [Fact]
    public async Task ARowWithNoStepShowsItsOwnTextAloneAndWalksNothing()
    {
        var probes = new ScriptedProbes();

        var result = await ExchangeDiagnosisEngine.RunAsync(ExchangeFailureRows.FormatUnsupported, "studio", 443, probes,
            CancellationToken.None);

        Assert.Empty(result.Steps);
        Assert.Null(result.AdviceStep);
        Assert.Empty(probes.Probed);
    }

    [Fact]
    public async Task AStreamClosedWithNoAnswerIsWalkedFromTheOperatorOnlyAfterTheLinkWasProven()
    {
        var probes = new ScriptedProbes();

        var result = await ExchangeDiagnosisEngine.RunAsync(ExchangeFailureRows.Unanswered, "192.168.1.20", 443, probes,
            CancellationToken.None);

        Assert.Equal([ExchangeDiagnosisStep.Operator], probes.Probed);
        Assert.Equal(ExchangeDiagnosisStep.Operator, result.AdviceStep);
        Assert.Equal(ExchangeDiagnosisState.Passed, result.StateOf(ExchangeDiagnosisStep.Certificate));
        Assert.Equal(ExchangeDiagnosisState.NotReached, result.StateOf(ExchangeDiagnosisStep.Credentials));
        Assert.Equal(ExchangeDiagnosisState.CouldNotCheck, result.StateOf(ExchangeDiagnosisStep.Operator));
    }

    [Fact]
    public async Task AFailedSecureConnectionIsWalkedFromTheClock()
    {
        var probes = new ScriptedProbes();

        var result = await ExchangeDiagnosisEngine.RunAsync(ExchangeFailureRows.TlsFailed, "192.168.1.20", 443, probes,
            CancellationToken.None);

        Assert.Equal(ExchangeDiagnosisStep.DeviceClock, result.AdviceStep);
        Assert.Equal(ExchangeDiagnosisState.Passed, result.StateOf(ExchangeDiagnosisStep.PcAsleep));
        Assert.Equal(ExchangeDiagnosisState.NotReached, result.StateOf(ExchangeDiagnosisStep.Certificate));
        Assert.Equal([ExchangeDiagnosisStep.DeviceClock, ExchangeDiagnosisStep.Operator], probes.Probed);
    }

    [Fact]
    public async Task WhenNothingCanBeSeenTheFirstStepOfferedIsTheAdviceAndNextWalksTheLadder()
    {
        var result = await ExchangeDiagnosisEngine.RunAsync(ExchangeFailureRows.Unreachable, "192.168.1.20", 443,
            new ScriptedProbes(), CancellationToken.None);

        Assert.DoesNotContain(result.Steps, item => item.State == ExchangeDiagnosisState.Failed);
        Assert.Equal(ExchangeDiagnosisStep.GuestNetwork, result.AdviceStep);
        Assert.Equal(ExchangeDiagnosisStep.WindowsPublicNetwork, result.NextAfter(ExchangeDiagnosisStep.GuestNetwork));
        Assert.Equal(ExchangeDiagnosisStep.DeviceClock, result.NextAfter(ExchangeDiagnosisStep.WindowsPublicNetwork));
        Assert.Equal(ExchangeDiagnosisStep.Operator, result.NextAfter(ExchangeDiagnosisStep.DeviceClock));
        Assert.Null(result.NextAfter(ExchangeDiagnosisStep.Operator));
    }

    [Fact]
    public async Task ACancelledDiagnosisThrowsAndRunsNoFurtherProbe()
    {
        using var cancellation = new CancellationTokenSource();
        var probes = new ScriptedProbes { OnProbe = _ => cancellation.Cancel() };

        await Assert.ThrowsAsync<OperationCanceledException>(() => ExchangeDiagnosisEngine.RunAsync(
            ExchangeFailureRows.Unreachable, "192.168.1.20", 443, probes, cancellation.Token));

        Assert.Single(probes.Probed);
    }

    [Fact]
    public async Task TheReportLineHoldsCodesOnlyAndNeverTheAddress()
    {
        var result = await ExchangeDiagnosisEngine.RunAsync(ExchangeFailureRows.Unreachable, "secret-host.example.org",
            48443, new ScriptedProbes(ExchangeDiagnosisStep.ServerInternet, ExchangeProbeOutcome.Failed),
            CancellationToken.None);

        var line = result.ToRedactedLine();

        Assert.StartsWith("exchange-diagnosis msg=unreachable-server;route=internet;fail=server-internet;walk=pc-asleep:passed",
            line);
        Assert.DoesNotContain("secret-host", line, StringComparison.Ordinal);
        Assert.DoesNotContain("48443", line, StringComparison.Ordinal);
        Assert.DoesNotContain("example.org", line, StringComparison.Ordinal);
        Assert.DoesNotContain("address-lan", line, StringComparison.Ordinal);
    }

    private sealed class ScriptedProbes : IExchangeDiagnosisProbes
    {
        private readonly Dictionary<ExchangeDiagnosisStep, ExchangeProbeOutcome> _script = [];
        public List<ExchangeDiagnosisStep> Probed { get; } = [];
        public Action<ExchangeDiagnosisStep>? OnProbe { get; init; }

        public ScriptedProbes()
        {
        }

        public ScriptedProbes(ExchangeDiagnosisStep step, ExchangeProbeOutcome outcome) => _script[step] = outcome;

        public Task<ExchangeProbeOutcome> ProbeAsync(ExchangeDiagnosisStep step, ExchangeDiagnosisContext context,
            CancellationToken cancellationToken)
        {
            Probed.Add(step);
            OnProbe?.Invoke(step);
            if (_script.TryGetValue(step, out var outcome))
            {
                return Task.FromResult(outcome);
            }

            // Unscripted: the three steps no PC can see are offered, the rest pass.
            return Task.FromResult(step is ExchangeDiagnosisStep.GuestNetwork or ExchangeDiagnosisStep.WindowsPublicNetwork
                or ExchangeDiagnosisStep.DeviceClock or ExchangeDiagnosisStep.Operator or ExchangeDiagnosisStep.ServerInternet
                ? ExchangeProbeOutcome.CouldNotCheck : ExchangeProbeOutcome.Passed);
        }
    }
}

internal static class ExchangeDiagnosisResultExtensions
{
    internal static ExchangeDiagnosisState StateOf(this ExchangeDiagnosisResult result, ExchangeDiagnosisStep step) =>
        result.Steps.Single(item => item.Step == step).State;
}
