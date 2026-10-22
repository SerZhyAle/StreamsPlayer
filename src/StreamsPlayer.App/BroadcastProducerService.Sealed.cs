using System.Security.Cryptography;
using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0226 / `DEVICE-EXCHANGE` section 27, the producer's side: a broadcast through the exchange server is sealed to the
/// account's own devices this program lists (item DG), its relay upload carries records (item DL), and its tunnel
/// runs TLS inside (item DM). The layer knows the media type only as a string, so a video source joins it the day
/// SP-0223 builds one. A server that cannot carry sealed content is sent nothing (item DP); the LAN share is unchanged.
/// </summary>
internal sealed partial class BroadcastProducerService
{
    /// <summary>How long a starting broadcast waits for the account's device list before it seals to whom it has (item DG).</summary>
    private static readonly TimeSpan DirectoryWait = TimeSpan.FromSeconds(5);

    /// <summary>How long a replacement upload may take to be accepted before the old one is retired anyway.</summary>
    private static readonly TimeSpan ReplacementWait = TimeSpan.FromSeconds(8);

    private static string MediaTypeOf(BroadcastSource source) =>
        BroadcastSources.IsAudioOnly(source) ? BroadcastDescriptorWriter.AudioContentType : BroadcastDescriptorWriter.VideoContentType;

    private BroadcastWirePlan PlanOf(BroadcastRunningSource state, string? publicEndpoint, bool relay, bool tunnel,
        BroadcastSealMode mode, string? linkGroupKey = null, string? tunnelPin = null) => new(
        state.BroadcastId, _exchange.Account.DeviceId, state.Title, state.Source,
        state.LanAddress, state.LocalPort, null, publicEndpoint, relay, tunnel,
        // SP-0200's owner policy pins every leaf, so the descriptor always carries the pin a receiver needs.
        _exchange.Account.Pin,
        Settings.SampleRate,
        state.Source == BroadcastSource.Microphone ? Settings.MicrophoneChannels : Settings.SystemSoundChannels,
        Settings.BitrateKbps,
        Seal: mode, LinkGroupKey: linkGroupKey, TunnelPin: tunnelPin);

    /// <summary>
    /// Makes the keys of a broadcast and takes the lease that keeps the device list current while it runs. The list is
    /// fetched because a person started a broadcast (SP-0222 holds): the lease is the producer's own follow of it.
    /// </summary>
    private async Task<BroadcastSealing> PrepareSealingAsync(BroadcastRunningSource state)
    {
        var sealing = state.Sealing ??= new BroadcastSealing(state.BroadcastId, MediaTypeOf(state.Source));
        sealing.DirectoryLease ??= _exchange.AcquireDirectory(follow: true);
        _ = await _exchange.WaitForDirectoryAsync(DirectoryWait, CancellationToken.None).ConfigureAwait(false);
        return sealing;
    }

    /// <summary>
    /// Seals the broadcast's keys - the relay group key of the own devices and the pin of its tunnel leaf - to the devices
    /// the program itself lists, with the capability and a trusted key (items DG and DK). The box goes in the record.
    /// </summary>
    private ExchangeSealedBox? SealRecordBox(BroadcastRunningSource state, BroadcastSealing sealing)
    {
        using var self = _exchange.OwnE2eKey();
        return self is null
            ? null
            : sealing.Sealer.Seal(_exchange.Account.DeviceId, self, _exchange.SealRecipients());
    }
    /// <summary>The record of `broadcast-start` or `broadcast-update`, with the box it currently carries (item DK).</summary>
    private Task<bool> PostRecordAsync(BroadcastRunningSource state, string type, CancellationToken cancellationToken)
    {
        if (state.Plan is not { } plan || state.DescriptorJson is not { } descriptor)
        {
            return Task.FromResult(false);
        }

        var envelope = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 2,
            ["type"] = type,
            ["broadcast"] = BroadcastDescriptorWriter.BroadcastRecord(plan, descriptor, state.StartedAt, state.Sealing?.Sealer.Box),
            ["maxListeners"] = state.AnnouncedMaxListeners
        };
        return type == "broadcast-update"
            ? _exchange.PostBroadcastUpdateAsync(envelope, cancellationToken)
            : _exchange.PostControlEnvelopeAsync(envelope, cancellationToken);
    }

    // --- recipients change ----------------------------------------------------------

    private void OnDirectoryChanged()
    {
        List<BroadcastRunningSource> states;
        lock (_gate)
        {
            states = [.. _running.Values.Where(state => state.Sealing is not null && state.ExchangeStarted)];
        }

        foreach (var state in states)
        {
            _ = RefreshSealingAsync(state);
        }
    }

    /// <summary>
    /// A device joined, gained the capability, was removed or changed its key (items DG and DK): the box is sealed anew
    /// and sent with `broadcast-update`. When a device is gone the sealer made the own group key anew and began a new
    /// epoch, so what the device held opens nothing from here on; the new ring then goes out by a replacement upload (item DL).
    /// </summary>
    internal async Task RefreshSealingAsync(BroadcastRunningSource state)
    {
        if (state.Sealing is not { } sealing)
        {
            return;
        }

        await sealing.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!state.ExchangeStarted || state.Plan is null)
            {
                return;
            }

            using var self = _exchange.OwnE2eKey();
            if (self is null)
            {
                return;
            }

            var refresh = sealing.Sealer.Refresh(_exchange.Account.DeviceId, self, _exchange.SealRecipients());
            if (!refresh.Changed)
            {
                return;
            }

            using var timeout = new CancellationTokenSource(EndWriteTimeout);
            _ = await PostRecordAsync(state, "broadcast-update", timeout.Token).ConfigureAwait(false);
            if (refresh.NewEpoch)
            {
                await ReplaceUploadAsync(state).ConfigureAwait(false);
            }
        }
        finally
        {
            sealing.Gate.Release();
        }
    }
    /// <summary>
    /// Item N: a second upload from the same device replaces the first; listeners stay connected and continue after the new
    /// header chunk. The new upload is accepted and has written its header chunk before the old one is retired - and the
    /// old one is closed without the zero-length end, which would end the broadcast.
    /// </summary>
    private async Task ReplaceUploadAsync(BroadcastRunningSource state)
    {
        var oldChunks = state.UploadChunks;
        var oldRetire = state.UploadRetire;
        if (oldChunks is null)
        {
            return;
        }

        state.UploadChunks = null;
        StartUpload(state);
        try
        {
            await state.UploadReady.Task.WaitAsync(ReplacementWait).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The new stream did not come up in time; the old one is retired all the same, and the next session repairs it.
        }

        oldChunks.Writer.TryComplete();
        oldRetire?.Cancel();
    }

    // --- the description a person hands out -------------------------------------------

    /// <summary>
    /// The description of a running broadcast for a link, a file or a barcode: the one the record carries, with the keys
    /// of the link's own group key and the tunnel's pin in its sealed endpoints (`LIVE-BROADCAST` item Q). It is a secret
    /// that opens the broadcast - handed by the person, never through the server, never logged.
    /// </summary>
    private string? OutwardDescriptor(BroadcastRunningSource state)
    {
        if (state.Sealing is not { } sealing || state.Plan is not { } plan)
        {
            return state.DescriptorJson;
        }

        var linkKey = sealing.Sealer.LinkKey();
        try
        {
            return BroadcastDescriptorWriter.Write(plan with
            {
                Seal = BroadcastSealMode.Outward,
                LinkGroupKey = linkKey is null ? null : ExchangeBase64Url.Encode(linkKey),
                TunnelPin = sealing.Sealer.Tunnel.Pin
            });
        }
        finally
        {
            if (linkKey is not null)
            {
                CryptographicOperations.ZeroMemory(linkKey);
            }
        }
    }

    /// <summary>
    /// Withdraws the outward link: its group key is dropped, a new one is made for the next link, and a new epoch begins, so
    /// the link already handed out opens nothing from here on (item DL). The new ring goes out by a replacement upload.
    /// </summary>
    internal async Task WithdrawLinkAsync(BroadcastSource source)
    {
        BroadcastRunningSource? state;
        lock (_gate)
        {
            if (!_running.TryGetValue(source, out state) || state.Sealing is null)
            {
                return;
            }
        }

        var sealing = state.Sealing;
        await sealing.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            sealing.Sealer.WithdrawLink();
            await ReplaceUploadAsync(state).ConfigureAwait(false);
        }
        finally
        {
            sealing.Gate.Release();
        }
    }

}
