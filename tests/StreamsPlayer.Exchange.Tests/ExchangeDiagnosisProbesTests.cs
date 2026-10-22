using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using StreamsPlayer.App;
using StreamsPlayer.Core;

namespace StreamsPlayer.Exchange.Tests;

/// <summary>
/// SP-0225: the checks this PC makes itself. The name lookup and the adapters are scripted, so what the machine
/// running the test has installed cannot change an answer; the connection is a real loopback socket.
/// </summary>
public sealed class ExchangeDiagnosisProbesTests
{
    private static ExchangeAdapterFact Adapter(string name, string description, bool up = true,
        NetworkInterfaceType type = NetworkInterfaceType.Ethernet, params (string Address, string? Mask)[] addresses) =>
        new(name, description, type, up,
            [.. addresses.Select(item => (IPAddress.Parse(item.Address), item.Mask is null ? null : IPAddress.Parse(item.Mask)))]);

    private static ExchangeDiagnosisProbes Probes(IReadOnlyList<ExchangeAdapterFact>? adapters = null, bool unresolvable = false) =>
        new(() => adapters ?? [], TimeSpan.FromSeconds(5),
            unresolvable
                ? (_, _) => Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound))
                : null);

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private static Task<ExchangeProbeOutcome> Probe(ExchangeDiagnosisProbes probes, ExchangeDiagnosisStep step, string host, int port) =>
        probes.ProbeAsync(step, new ExchangeDiagnosisContext(host, port), CancellationToken.None);

    [Theory]
    [InlineData("WireGuard Tunnel", "WireGuard Tunnel", NetworkInterfaceType.Ethernet, true)]
    [InlineData("Proton VPN", "Proton VPN TAP Adapter", NetworkInterfaceType.Ethernet, true)]
    [InlineData("Local Area Connection", "TAP-Windows Adapter V9", NetworkInterfaceType.Ethernet, true)]
    [InlineData("Tailscale", "Tailscale Tunnel", NetworkInterfaceType.Ethernet, true)]
    [InlineData("Office", "Contoso", NetworkInterfaceType.Ppp, true)]
    [InlineData("Ethernet", "Intel(R) Ethernet Connection", NetworkInterfaceType.Ethernet, false)]
    [InlineData("Wi-Fi", "Intel(R) Wi-Fi 6 AX201", NetworkInterfaceType.Wireless80211, false)]
    // Windows' own IPv6 transition tunnels are up on a PC with no VPN, and a bare "tun" match blames them.
    [InlineData("Teredo Tunneling Pseudo-Interface", "Teredo Tunneling Pseudo-Interface", NetworkInterfaceType.Tunnel, false)]
    [InlineData("isatap.{8F0A}", "Microsoft ISATAP Adapter", NetworkInterfaceType.Tunnel, false)]
    [InlineData("vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter", NetworkInterfaceType.Ethernet, false)]
    public void AVpnIsTakenByItsKindAndNeverByAWindowsTransitionTunnel(string name, string description,
        NetworkInterfaceType type, bool vpn)
    {
        Assert.Equal(vpn, ExchangeDiagnosisProbes.LooksLikeVpn(Adapter(name, description, type: type)));
    }

    [Fact]
    public async Task TheVpnStepFailsOnlyWhileAVpnAdapterIsUp()
    {
        var up = Probes([Adapter("WireGuard Tunnel", "WireGuard Tunnel")]);
        var down = Probes([Adapter("WireGuard Tunnel", "WireGuard Tunnel", up: false)]);

        Assert.Equal(ExchangeProbeOutcome.Failed, await Probe(up, ExchangeDiagnosisStep.Vpn, "192.168.1.5", 443));
        Assert.Equal(ExchangeProbeOutcome.Passed, await Probe(down, ExchangeDiagnosisStep.Vpn, "192.168.1.5", 443));
    }

    [Theory]
    [InlineData("192.168.1.20", "192.168.1.5", "255.255.255.0", true)]
    [InlineData("192.168.2.20", "192.168.1.5", "255.255.255.0", false)]
    [InlineData("192.168.2.20", "192.168.1.5", "255.255.0.0", true)]
    [InlineData("10.0.0.9", "192.168.1.5", "255.255.255.0", false)]
    public void ASubnetIsSharedWhenTheMaskedAddressesMatch(string host, string local, string mask, bool expected)
    {
        Assert.Equal(expected, ExchangeDiagnosisProbes.SharesSubnet(IPAddress.Parse(host), IPAddress.Parse(local), IPAddress.Parse(mask)));
    }

    [Fact]
    public void ALinkLocalAddressIsNotAnAddressOfTheFamily()
    {
        var onlyLinkLocal = new[]
        {
            Adapter("Ethernet", "Ethernet", addresses: [("fe80::1", null), ("169.254.7.7", "255.255.0.0")])
        };
        var routable = new[] { Adapter("Ethernet", "Ethernet", addresses: [("2001:db8::5", null), ("192.168.1.5", "255.255.255.0")]) };
        var down = new[] { Adapter("Ethernet", "Ethernet", up: false, addresses: [("2001:db8::5", null)]) };

        Assert.False(ExchangeDiagnosisProbes.HasAddressOfFamily(onlyLinkLocal, AddressFamily.InterNetworkV6));
        Assert.False(ExchangeDiagnosisProbes.HasAddressOfFamily(onlyLinkLocal, AddressFamily.InterNetwork));
        Assert.True(ExchangeDiagnosisProbes.HasAddressOfFamily(routable, AddressFamily.InterNetworkV6));
        Assert.True(ExchangeDiagnosisProbes.HasAddressOfFamily(routable, AddressFamily.InterNetwork));
        Assert.False(ExchangeDiagnosisProbes.HasAddressOfFamily(down, AddressFamily.InterNetworkV6));
    }

    [Fact]
    public async Task ARefusedPortMeansTheMachineIsAwakeAndTheAddressOrPortIsOff()
    {
        var port = FreePort();
        var probes = Probes();

        Assert.Equal(ExchangeProbeOutcome.Passed, await Probe(probes, ExchangeDiagnosisStep.PcAsleep, "127.0.0.1", port));
        Assert.Equal(ExchangeProbeOutcome.Failed, await Probe(probes, ExchangeDiagnosisStep.AddressLan, "127.0.0.1", port));
        Assert.Equal(ExchangeProbeOutcome.Failed, await Probe(probes, ExchangeDiagnosisStep.PortForward, "127.0.0.1", port));
        Assert.Equal(ExchangeProbeOutcome.Passed, await Probe(probes, ExchangeDiagnosisStep.ServerInternet, "127.0.0.1", port));
    }

    [Fact]
    public async Task AnAnsweringPortPassesTheReachSteps()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var probes = Probes();

        Assert.Equal(ExchangeProbeOutcome.Passed, await Probe(probes, ExchangeDiagnosisStep.PcAsleep, "127.0.0.1", port));
        Assert.Equal(ExchangeProbeOutcome.Passed, await Probe(probes, ExchangeDiagnosisStep.AddressLan, "127.0.0.1", port));
        Assert.Equal(ExchangeProbeOutcome.Passed, await Probe(probes, ExchangeDiagnosisStep.PortForward, "127.0.0.1", port));
    }

    [Fact]
    public async Task ANameThatDoesNotResolveFailsTheAddressStepOrTheInternetStepAndTellsNothingAboutTheMachine()
    {
        var probes = Probes(unresolvable: true);

        Assert.Equal(ExchangeProbeOutcome.CouldNotCheck, await Probe(probes, ExchangeDiagnosisStep.PcAsleep, "studio", 443));
        Assert.Equal(ExchangeProbeOutcome.Failed, await Probe(probes, ExchangeDiagnosisStep.AddressLan, "studio", 443));
        Assert.Equal(ExchangeProbeOutcome.Failed, await Probe(probes, ExchangeDiagnosisStep.ServerInternet, "x.example.invalid", 443));
        Assert.Equal(ExchangeProbeOutcome.CouldNotCheck, await Probe(probes, ExchangeDiagnosisStep.IpFamily, "x.example.invalid", 443));
    }

    [Fact]
    public async Task APrivateAddressNoAdapterSharesIsAWrongNetworkWhenNothingAnswers()
    {
        // The host is a literal on a subnet this PC is not on, and the connection (to a port nobody listens on, on
        // an address this PC cannot reach) does not connect: the address step fails with the subnet as its reason.
        var elsewhere = Probes([Adapter("Ethernet", "Ethernet", addresses: [("10.20.30.40", "255.255.255.0")])]);
        var sameNetwork = Probes([Adapter("Ethernet", "Ethernet", addresses: [("172.31.254.5", "255.255.255.0")])]);

        Assert.Equal(ExchangeProbeOutcome.Failed, await Probe(elsewhere, ExchangeDiagnosisStep.AddressLan, "172.31.254.253", 9));
        Assert.NotEqual(ExchangeProbeOutcome.Failed, await Probe(sameNetwork, ExchangeDiagnosisStep.AddressLan, "172.31.254.253", 9));
    }

    [Theory]
    [InlineData(ExchangeDiagnosisStep.GuestNetwork)]
    [InlineData(ExchangeDiagnosisStep.WindowsPublicNetwork)]
    [InlineData(ExchangeDiagnosisStep.DeviceClock)]
    [InlineData(ExchangeDiagnosisStep.Operator)]
    public async Task WhatThisPcCannotSeeIsOfferedAndNeverAsserted(ExchangeDiagnosisStep step)
    {
        Assert.Equal(ExchangeProbeOutcome.CouldNotCheck, await Probe(Probes(), step, "127.0.0.1", 443));
    }
}
