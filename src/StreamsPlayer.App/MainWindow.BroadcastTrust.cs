using System.IO;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Windows;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public partial class MainWindow
{
    private async Task<StreamChannel> ResolveBroadcastTrustAsync(StreamChannel channel, bool interactive = true)
    {
        if (!interactive || _shuttingDown || channel.FastMediaSorterBroadcast is not { } info)
        {
            return channel;
        }

        var pins = new Dictionary<string, string>(info.CertificatePins, StringComparer.Ordinal);
        var examined = new HashSet<string>(StringComparer.Ordinal);
        foreach (var endpoint in info.PlaybackAttemptEndpoints(channel.Url))
        {
            if (endpoint.CertFingerprint is not null || BroadcastCertificateTrust.EndpointIdentity(endpoint.Url) is not { } identity
                || !examined.Add(identity) || !ExchangeBroadcastKeys.Decide(endpoint).Allowed)
            {
                continue;
            }

            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var leaf = await BroadcastCertificateTrust.ProbeAsync(endpoint.Url, timeout.Token);
                if (!_shuttingDown && leaf is { PlatformTrusted: false }
                    && MessageBox.Show(DialogOwner, LocalizationService.Format("BroadcastTrustPrompt", identity, leaf.Fingerprint),
                        LocalizationService.Get("ExchangeTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning,
                        MessageBoxResult.No) == MessageBoxResult.Yes)
                {
                    pins[identity] = leaf.Fingerprint;
                }
            }
            catch (Exception exception) when (exception is IOException or SocketException or AuthenticationException or OperationCanceledException)
            {
                // An unreachable endpoint remains the playback attempt's failure; a probe never changes its fallback order.
                _log.Event("BROADCAST TRUST PROBE", $"outcome={exception.GetType().Name}");
            }
        }

        if (_shuttingDown || pins.Count == info.CertificatePins.Count)
        {
            return channel;
        }

        channel = channel with { FastMediaSorterBroadcast = info with { CertificatePins = pins } };
        var accepted = channel;
        await PersistAsync(state => state with
        {
            Channels = state.Channels.Select(row => row.Id == accepted.Id && row.FastMediaSorterBroadcast is { } current
                ? row with { FastMediaSorterBroadcast = current with { CertificatePins = pins } } : row).ToList()
        });
        return channel;
    }
}
