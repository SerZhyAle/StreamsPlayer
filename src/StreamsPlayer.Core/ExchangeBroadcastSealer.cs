using System.Security.Cryptography;
using System.Text.Json;

namespace StreamsPlayer.Core;

/// <summary>What a change of the account's devices asks of the producer (`DEVICE-EXCHANGE` items DG, DK and DL).</summary>
/// <param name="Changed">The set of recipients differs from the one the current box is addressed to: send the box.</param>
/// <param name="NewEpoch">Someone left, so the own group key was made anew and a new epoch began: send the box first, then replace the upload.</param>
public readonly record struct ExchangeSealRefresh(bool Changed, bool NewEpoch, ExchangeSealedBox? Box);

/// <summary>
/// The producer's sealing decisions for one broadcast, free of any socket or window (`DEVICE-EXCHANGE` section 27): the
/// relay's group keys and epoch, the box that hands the own devices their keys (item DK), what a change of those devices
/// requires (item DL), and the link's key. One object per running broadcast; the application feeds it the recipients it
/// lists and does what it answers. The media type is a string - audio and video use it the same way.
/// </summary>
public sealed class ExchangeBroadcastSealer : IDisposable
{
    /// <summary>The group key the account's own devices open the ring with.</summary>
    public const string OwnPurpose = "own";

    /// <summary>The group key of the outward link the person hands out.</summary>
    public const string LinkPurpose = "link";

    private readonly string _broadcastId;
    private IReadOnlyList<string> _recipients = [];

    public ExchangeBroadcastSealer(string broadcastId, string mediaType, ExchangeTunnelCertificate tunnel)
    {
        _broadcastId = broadcastId;
        Tunnel = tunnel;
        Relay = new ExchangeSealedRelayEncoder(broadcastId, mediaType);
        Relay.NewGroupKey(OwnPurpose);
        Relay.NewGroupKey(LinkPurpose);
    }

    public ExchangeSealedRelayEncoder Relay { get; }

    /// <summary>The certificate of the inner tunnel TLS (item DM); its pin is in the box and in the outward link.</summary>
    public ExchangeTunnelCertificate Tunnel { get; }

    /// <summary>The box the record carries now; null before the first <see cref="Seal"/>.</summary>
    public ExchangeSealedBox? Box { get; private set; }

    /// <summary>The recipients the current box is addressed to, as `id:point` entries in id order.</summary>
    public IReadOnlyList<string> Recipients => _recipients;

    /// <summary>
    /// Seals the broadcast's keys - the own devices' relay group key and the pin of the tunnel leaf - to
    /// <paramref name="recipients"/> (most recently seen first; a box cuts to forty). The device that made the broadcast
    /// is not among them: it holds the keys. A protected broadcast's pair joins the payload when there is one.
    /// </summary>
    public ExchangeSealedBox Seal(
        string senderDeviceId, ExchangeE2eKeyPair sender, IReadOnlyList<ExchangeSealedRecipient> recipients,
        ExchangeRelayCredentials? credentials = null)
    {
        var ownKey = Relay.GroupKey(OwnPurpose)
            ?? throw new InvalidOperationException("The own group key is missing.");
        var members = new Dictionary<string, object>
        {
            ["relayKey"] = ExchangeBase64Url.Encode(ownKey),
            ["tunnelPin"] = Tunnel.Pin
        };
        if (credentials is not null)
        {
            members["credentials"] = new Dictionary<string, string> { ["user"] = credentials.User, ["password"] = credentials.Password };
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(members);
        try
        {
            Box = ExchangeSealedBoxes.Seal(payload, ExchangeSealedContext.Broadcast(_broadcastId), senderDeviceId, sender, recipients);
            _recipients = Fingerprint(recipients.Where(recipient => Box.Keys.Any(key => key.To == recipient.DeviceId)));
            return Box;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ownKey);
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    /// <summary>
    /// The account's devices changed. A device that joined or changed only means a new box. A device that left means more:
    /// the own group key is made anew and a new epoch begins, so what that device holds opens nothing from here on
    /// (item DL); the caller sends the box first and then replaces the upload with the new ring.
    /// </summary>
    public ExchangeSealRefresh Refresh(
        string senderDeviceId, ExchangeE2eKeyPair sender, IReadOnlyList<ExchangeSealedRecipient> recipients,
        ExchangeRelayCredentials? credentials = null)
    {
        recipients = recipients.Where(recipient => ExchangeE2eKeyPair.IsValidPublicPoint(recipient.PublicPoint))
            .Take(ExchangeSealedBox.MaximumRecipients).ToArray();
        var current = Fingerprint(recipients);
        if (Box is not null && current.SequenceEqual(_recipients))
        {
            return new ExchangeSealRefresh(false, false, Box);
        }

        var someoneLeft = Ids(_recipients).Except(Ids(current)).Any();
        if (someoneLeft)
        {
            Relay.NewGroupKey(OwnPurpose);
            Relay.NewEpoch();
        }

        return new ExchangeSealRefresh(true, someoneLeft, Seal(senderDeviceId, sender, recipients, credentials));
    }

    /// <summary>The outward link is withdrawn: its group key is dropped and a new one made for the next link, and a new epoch begins.</summary>
    public void WithdrawLink()
    {
        Relay.NewGroupKey(LinkPurpose);
        Relay.NewEpoch();
    }

    /// <summary>A copy of the link's group key for the outward description; the caller zeroes it after use.</summary>
    public byte[]? LinkKey() => Relay.GroupKey(LinkPurpose);

    // Order-free: the list is re-sorted by `lastSeen` all the time, and a box must change when the set does, not then.
    private static IReadOnlyList<string> Fingerprint(IEnumerable<ExchangeSealedRecipient> recipients) =>
        [.. recipients.Select(recipient => recipient.DeviceId + ":" + ExchangeBase64Url.Encode(recipient.PublicPoint))
            .OrderBy(entry => entry, StringComparer.Ordinal)];

    private static HashSet<string> Ids(IEnumerable<string> fingerprint) =>
        fingerprint.Select(entry => entry[..entry.IndexOf(':')]).ToHashSet(StringComparer.Ordinal);

    public void Dispose()
    {
        Relay.Dispose();
        Tunnel.Dispose();
    }
}
