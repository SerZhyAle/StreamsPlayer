using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0226 / `DEVICE-EXCHANGE` section 27, the account entry's side: its own device key, what the server says it
/// can carry (`welcome.features` `sealed`, item DP), and the keys of the other devices this PC has seen (item DG).
/// Nothing here opens or seals content; it holds the facts the producer and the players ask for.
/// </summary>
internal sealed partial class ExchangeSourceService
{
    /// <summary>Whether the server advertises `sealed`: it keeps and delivers keys, boxes and sealed uploads (item DP).</summary>
    internal bool ServerSeals => Features.Contains("sealed", StringComparer.Ordinal);

    /// <summary>
    /// The own entry was enrolled before keys existed. It keeps working as a receiver of what needs no key, cannot
    /// open a sealed box and is nobody's recipient, and the person is told to sign out and in on this device.
    /// </summary>
    internal bool OwnKeyMissing => HasAccount && !Account.Guest && Account.Token is not null && Account.E2ePrivateKey is null;

    /// <summary>The own entry's key pair, or null for a legacy entry. The caller disposes it.</summary>
    internal ExchangeE2eKeyPair? OwnE2eKey() => Account.LoadE2eKey();

    /// <summary>The keys the own entry has seen (first sight trusts; a changed key is never used).</summary>
    internal ExchangeDeviceTrust Trust => _trustBook.For(Account.DeviceId);

    /// <summary>
    /// Every other device the account lists, in a state content may be sealed to, with the key first seen for it.
    /// A device the server added shows here like any other - that is the point of item DG: the person sees it.
    /// </summary>
    internal IReadOnlyList<ExchangeSealedRecipient> SealRecipients() =>
        _directory.Snapshot.Devices is { } devices ? ExchangeRecipients.For(devices, Account.DeviceId, Trust) : [];

    /// <summary>The sealed state of one listed device (`e2e-missing`, `key-changed`, or none), as the person is shown it.</summary>
    internal ExchangeSealedState StateOfDevice(ExchangeDeviceRecord device) => ExchangeRecipients.Classify(device, Trust);

    /// <summary>
    /// Opens the box of every sealed broadcast the directory lists, with this entry's key, and keeps what is in it for
    /// the playback legs (item DK). A legacy entry holds no key, so it can open nothing: its broadcasts say `e2e-missing`.
    /// </summary>
    private void OpenDirectoryBoxes(ExchangeDirectorySnapshot snapshot)
    {
        using var self = OwnE2eKey();
        foreach (var record in snapshot.Groups.SelectMany(group => group.Broadcasts).Select(view => view.Record))
        {
            if (record.Sealed is null && !record.SealedUnreadable)
            {
                continue;
            }

            if (self is null)
            {
                ExchangeBroadcastKeys.Fail(record.BroadcastId, ExchangeSealedState.E2eMissing);
                continue;
            }

            _ = ExchangeBroadcastKeys.OpenRecord(record, Account.DeviceId, self, Trust);
        }
    }

    /// <summary>
    /// A cast offer's box, opened with this entry's key. The sender may be a device this PC has not listed yet; the
    /// directory is then asked once, because a box from a key nobody vouched for opens nothing (item DG).
    /// </summary>
    private async Task OpenOfferBoxAsync(ExchangeCastOffer offer, CancellationToken cancellationToken)
    {
        if (offer.Descriptor is null || offer.Sealed is null && !offer.SealedUnreadable)
        {
            return;
        }

        using var self = OwnE2eKey();
        if (self is null)
        {
            ExchangeBroadcastKeys.Fail(offer.BroadcastId, ExchangeSealedState.E2eMissing);
            return;
        }

        if (offer.Sealed is { } box && Trust.TrustedDevice(box.From) is null)
        {
            using var lease = AcquireDirectory(follow: false);
            _ = await WaitForDirectoryAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }

        var record = new ExchangeBroadcastRecord(offer.BroadcastId, offer.DeviceId, offer.Title, offer.Descriptor.Mode,
            offer.Descriptor, Sealed: offer.Sealed, SealedUnreadable: offer.SealedUnreadable);
        _ = ExchangeBroadcastKeys.OpenRecord(record, Account.DeviceId, self, Trust);
    }

    /// <summary>
    /// Applies a directory to the trust: every listed device is observed (so a first key is remembered and a
    /// changed one is noticed), and a device that is no longer listed is forgotten, which is what lets a removed and
    /// re-added device come back with a new key (item DG).
    /// </summary>
    private void ObserveDirectoryKeys(ExchangeDirectorySnapshot snapshot)
    {
        if (snapshot.Devices is not { Count: > 0 } devices)
        {
            return;
        }

        var trust = Trust;
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var device in devices)
        {
            listed.Add(device.DeviceId);
            if (!string.Equals(device.DeviceId, Account.DeviceId, StringComparison.Ordinal))
            {
                _ = ExchangeRecipients.Classify(device, trust);
            }
        }

        foreach (var gone in trust.DeviceIds.Where(id => !listed.Contains(id)).ToArray())
        {
            trust.ForgetDevice(gone);
        }

        _trustBook.SaveIfDirty();
    }
}
