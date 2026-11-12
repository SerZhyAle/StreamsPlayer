using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>One engine-facing address and its owned security transport, shared by playback and grid capture.</summary>
internal sealed class BroadcastVideoRoute : IDisposable
{
    private readonly IDisposable[] _transports;

    private BroadcastVideoRoute(Uri address, params IDisposable[] transports)
    {
        Address = address;
        _transports = transports;
    }

    internal Uri Address { get; }

    internal static BroadcastVideoRoute? Open(FastMediaSorterBroadcastEndpoint endpoint,
        ExchangeRelayCredentials? credentials, Action<string> failed, bool passwordProtected = false, bool verifyHttp = false)
    {
        var decision = ExchangeBroadcastKeys.Decide(endpoint);
        if (!decision.Allowed)
        {
            failed(decision.Reason);
            return null;
        }

        credentials = decision.Credentials ?? credentials;
        if (passwordProtected && credentials is null)
        {
            failed(ExchangeBroadcastAuth.PasswordRequiredReason);
            return null;
        }

        if (endpoint.Transport?.Equals("TUNNEL", StringComparison.OrdinalIgnoreCase) == true
            || endpoint.Url.StartsWith("fmsx:", StringComparison.OrdinalIgnoreCase))
        {
            if (endpoint.Inner is not { Length: > 0 } inner || !ExchangeTunnelUrl.TryParse(endpoint.Url, out var tunnel))
            {
                failed("unsupported_address");
                return null;
            }

            var forwarder = ExchangeTunnelForwarder.Start(tunnel, inner, endpoint.CertFingerprint, tunnelPin: decision.TunnelPin,
                admitPeer: LoopbackPeerOwner.IsThisProcess);
            forwarder.Refused += failed;
            ListenerBindLog.Loopback("video-tunnel", forwarder.BoundPort);
            if (passwordProtected)
            {
                try { return Protected(inner, forwarder.LoopbackUrl, credentials!, failed, null, forwarder); }
                catch { forwarder.Dispose(); throw; }
            }
            return WithTransport(forwarder.LoopbackUrl, forwarder);
        }

        if (!LaunchableAddress.TryParse(endpoint.Url, out var address))
        {
            failed("unsupported_address");
            return null;
        }

        if (passwordProtected && decision.RelayOptions(endpoint.Url) is null
            && endpoint.Transport?.Equals("RELAY", StringComparison.OrdinalIgnoreCase) != true)
        {
            if (address.Scheme is not ("http" or "https" or "rtsp"))
            {
                failed(ExchangeBroadcastAuth.UnsupportedEndpointReason);
                return null;
            }
            return Protected(endpoint.Url, null, credentials!, failed, endpoint.CertFingerprint);
        }

        if (decision.RelayOptions(endpoint.Url) is not null
            || endpoint.Transport?.Equals("RELAY", StringComparison.OrdinalIgnoreCase) == true
            || address.Scheme == Uri.UriSchemeHttps && (verifyHttp || endpoint.CertFingerprint is not null))
        {
            var proxy = ExchangeRelayProxy.Start(endpoint.Url, endpoint.CertFingerprint,
                credentials: decision.Credentials ?? credentials, sealedRelay: decision.RelayOptions(endpoint.Url));
            proxy.Failed += failed;
            ListenerBindLog.Loopback("video-relay", proxy.BoundPort);
            return WithTransport(proxy.LoopbackUrl, proxy);
        }

        return new BroadcastVideoRoute(address);
    }

    public void Dispose()
    {
        foreach (var transport in _transports) transport.Dispose();
    }

    private static BroadcastVideoRoute Protected(string target, string? connect, ExchangeRelayCredentials credentials,
        Action<string> failed, string? pin, IDisposable? tunnel = null)
    {
        var proxy = BroadcastDigestProxy.Start(target, credentials, connect, pin, LoopbackPeerOwner.IsThisProcess,
            connectTimeout: connect is null ? null : TimeSpan.FromSeconds(15));
        proxy.Failed += failed;
        ListenerBindLog.Loopback("video-digest", proxy.BoundPort);
        return WithTransport(proxy.LoopbackUrl, tunnel is null ? [proxy] : [proxy, tunnel]);
    }

    private static BroadcastVideoRoute WithTransport(string loopbackUrl, params IDisposable[] transports)
    {
        if (LaunchableAddress.TryParse(loopbackUrl, out var address))
        {
            return new BroadcastVideoRoute(address, transports);
        }

        foreach (var transport in transports) transport.Dispose();
        throw new InvalidOperationException("The local playback transport has no playable address.");
    }
}
