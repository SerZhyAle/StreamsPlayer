using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>One engine-facing address and its owned security transport, shared by playback and grid capture.</summary>
internal sealed class BroadcastVideoRoute : IDisposable
{
    private readonly IDisposable? _transport;

    private BroadcastVideoRoute(Uri address, IDisposable? transport = null)
    {
        Address = address;
        _transport = transport;
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

        if (passwordProtected && decision.RelayOptions(endpoint.Url) is null)
        {
            // The engines do not answer the producer's Digest challenge. Refuse before exposing a
            // password in a URL or turning an unsupported protected route into a generic engine error.
            failed(ExchangeBroadcastAuth.UnsupportedEndpointReason);
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

            var forwarder = ExchangeTunnelForwarder.Start(tunnel, inner, endpoint.CertFingerprint, tunnelPin: decision.TunnelPin);
            forwarder.Refused += failed;
            return WithTransport(forwarder.LoopbackUrl, forwarder);
        }

        if (!LaunchableAddress.TryParse(endpoint.Url, out var address))
        {
            failed("unsupported_address");
            return null;
        }

        if (decision.RelayOptions(endpoint.Url) is not null
            || endpoint.Transport?.Equals("RELAY", StringComparison.OrdinalIgnoreCase) == true
            || address.Scheme == Uri.UriSchemeHttps && (verifyHttp || endpoint.CertFingerprint is not null))
        {
            var proxy = ExchangeRelayProxy.Start(endpoint.Url, endpoint.CertFingerprint,
                credentials: decision.Credentials ?? credentials, sealedRelay: decision.RelayOptions(endpoint.Url));
            proxy.Failed += failed;
            return WithTransport(proxy.LoopbackUrl, proxy);
        }

        return new BroadcastVideoRoute(address);
    }

    public void Dispose() => _transport?.Dispose();

    private static BroadcastVideoRoute WithTransport(string loopbackUrl, IDisposable transport)
    {
        if (LaunchableAddress.TryParse(loopbackUrl, out var address))
        {
            return new BroadcastVideoRoute(address, transport);
        }

        transport.Dispose();
        throw new InvalidOperationException("The local playback transport has no playable address.");
    }
}
