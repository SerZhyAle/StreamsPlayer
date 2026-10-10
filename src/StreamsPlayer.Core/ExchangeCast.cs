using System.Text;
using System.Text.Json;

namespace StreamsPlayer.Core;

/// <summary>The bounds a cast frame's values are held to before they are echoed back or shown.</summary>
internal static class ExchangeCastLimits
{
    /// <summary>
    /// The longest id accepted from a cast frame. Ids are echoed into the answer, so an unbounded one could push
    /// the answer past the authenticated frame limit and make the write itself fail.
    /// </summary>
    internal const int MaximumIdLength = 128;

    /// <summary>The longest sender device name shown in the prompt; a longer one is treated as absent.</summary>
    internal const int MaximumDeviceNameLength = 128;

    /// <summary>The same 16 KiB ceiling a directory record is held to (DEVICE-EXCHANGE 6).</summary>
    internal static bool IsOversizeRecord(JsonElement record) =>
        Encoding.UTF8.GetByteCount(record.GetRawText()) > ExchangeDirectoryState.MaximumRecordBytes;

    /// <summary>Reads an optional id member: absent stays null, one over the bound is reported as invalid.</summary>
    internal static bool TryReadId(JsonElement element, string member, out string? id)
    {
        id = ExchangeProtocol.String(element, member);
        return id is null || id.Length <= MaximumIdLength;
    }
}

/// <summary>
/// A cast-offer received over the exchange control stream (DEVICE-EXCHANGE 7.8, SP-0205).
/// </summary>
/// <remarks>
/// <para>Section 7.8 defines the offer as <c>broadcast</c> (the section 6.3 record, whose
/// <c>descriptor</c> is the <c>LIVE-BROADCAST</c> descriptor) plus <c>fromDeviceId</c>, and the answer as
/// <c>broadcastId</c>, <c>accepted</c>, <c>reason</c>. The broadcast id is therefore the offer's identity;
/// <see cref="CastId"/> is only an optional extra a sender may add, echoed back when present.</para>
/// </remarks>
public sealed record ExchangeCastOffer(
    string BroadcastId,
    string DeviceId,
    string? DeviceName,
    string Title,
    FastMediaSorterBroadcast? Descriptor,
    ExchangeBroadcastSupport Support,
    string? CastId = null)
{
    public static bool TryParse(JsonElement root, out ExchangeCastOffer? offer)
    {
        offer = null;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var type = ExchangeProtocol.String(root, "type");
        if (type != "cast-offer")
        {
            return false;
        }

        // Without the record there is no broadcast to answer for; the contract's offer always carries it.
        if (!root.TryGetProperty("broadcast", out var record) || record.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var broadcastId = ExchangeProtocol.String(record, "broadcastId");
        if (string.IsNullOrWhiteSpace(broadcastId) || broadcastId.Length > ExchangeCastLimits.MaximumIdLength)
        {
            return false;
        }

        // Every id is echoed into the answer (or looked up), so one over the bound makes the whole offer
        // unanswerable rather than risking an answer the frame limit would refuse.
        if (!ExchangeCastLimits.TryReadId(root, "castId", out var castId)
            || !ExchangeCastLimits.TryReadId(root, "offerId", out var offerId)
            || !ExchangeCastLimits.TryReadId(root, "fromDeviceId", out var fromDeviceId)
            || !ExchangeCastLimits.TryReadId(record, "deviceId", out var recordDeviceId))
        {
            return false;
        }

        castId ??= offerId;
        var deviceId = fromDeviceId ?? recordDeviceId ?? "";
        var deviceName = ExchangeProtocol.String(root, "fromDeviceName");
        if (deviceName is { Length: > ExchangeCastLimits.MaximumDeviceNameLength })
        {
            deviceName = null;
        }

        FastMediaSorterBroadcast? descriptor = null;
        var support = ExchangeBroadcastSupport.Unsupported;
        // A record past the ceiling is not read at all: the offer stays answerable (unsupported) and nothing
        // in it - title, descriptor - reaches the prompt.
        var readable = !ExchangeCastLimits.IsOversizeRecord(record);
        var title = readable ? ExchangeProtocol.String(record, "title") : null;

        if (readable && record.TryGetProperty("descriptor", out var descElement) && descElement.ValueKind == JsonValueKind.Object)
        {
            var read = FastMediaSorterBroadcastDescriptor.Read(descElement.GetRawText());
            if (read.IsAccepted && read.Broadcast is not null)
            {
                descriptor = read.Broadcast;
                support = ExchangeDirectoryState.SupportOf(read.Broadcast);
                title ??= read.Broadcast.Title;
            }
            else if (read.Status == FastMediaSorterBroadcastReadStatus.UnsupportedSchema)
            {
                support = ExchangeBroadcastSupport.UnsupportedSchema;
            }
        }

        title ??= "Live Broadcast";
        offer = new ExchangeCastOffer(broadcastId, deviceId, deviceName, title, descriptor, support, castId);
        return true;
    }
}

/// <summary>
/// Helper to construct cast-answer frames for the exchange control stream (DEVICE-EXCHANGE 7.8).
/// </summary>
/// <remarks>
/// The answer is keyed by <c>broadcastId</c>. A <c>castId</c> the offer carried as an optional extra is echoed
/// back so a sender that correlates on it still can, but it is never the identity.
/// </remarks>
public static class ExchangeCastAnswer
{
    public static object Accept(string broadcastId, string? castId = null) =>
        Build(broadcastId, castId, accepted: true, reason: null);

    public static object Decline(string broadcastId, string reason = "declined", string? castId = null) =>
        Build(broadcastId, castId, accepted: false, reason);

    public static object Unsupported(string broadcastId, string? castId = null) =>
        Decline(broadcastId, "unsupported", castId);

    /// <summary>DEVICE-EXCHANGE item R: the reason when no answer was given inside the offer window.</summary>
    public static object Timeout(string broadcastId, string? castId = null) =>
        Decline(broadcastId, "timeout", castId);

    /// <summary>The frame that answers <paramref name="offer"/> with what the user or the clock decided.</summary>
    public static object For(ExchangeCastOffer offer, ExchangeCastDecision decision)
    {
        ArgumentNullException.ThrowIfNull(offer);
        return decision switch
        {
            ExchangeCastDecision.Accepted => Accept(offer.BroadcastId, offer.CastId),
            ExchangeCastDecision.TimedOut => Timeout(offer.BroadcastId, offer.CastId),
            _ => Decline(offer.BroadcastId, castId: offer.CastId)
        };
    }

    private static Dictionary<string, object> Build(string broadcastId, string? castId, bool accepted, string? reason)
    {
        var frame = new Dictionary<string, object>
        {
            ["schemaVersion"] = 2,
            ["type"] = "cast-answer",
            ["broadcastId"] = broadcastId
        };
        if (!string.IsNullOrWhiteSpace(castId))
        {
            frame["castId"] = castId;
        }

        frame["accepted"] = accepted;
        if (reason is not null)
        {
            frame["reason"] = reason;
        }

        return frame;
    }
}

/// <summary>
/// A cast-stop notification received over the exchange control stream (DEVICE-EXCHANGE 7.8, SP-0205).
/// </summary>
public sealed record ExchangeCastStop(string? CastId, string? BroadcastId)
{
    public static bool TryParse(JsonElement root, out ExchangeCastStop? stop)
    {
        stop = null;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var type = ExchangeProtocol.String(root, "type");
        if (type != "cast-stop")
        {
            return false;
        }

        if (!ExchangeCastLimits.TryReadId(root, "castId", out var castId)
            || !ExchangeCastLimits.TryReadId(root, "offerId", out var offerId)
            || !ExchangeCastLimits.TryReadId(root, "broadcastId", out var broadcastId))
        {
            return false;
        }

        stop = new ExchangeCastStop(castId ?? offerId, broadcastId);
        return true;
    }
}

/// <summary>
/// One cast this device accepted: what a later <c>cast-stop</c> is allowed to end. It is registered before the
/// accept's import is saved, because a stop can arrive during that save; the accept's continuation reads
/// <see cref="StopRequested"/> to learn that the cast is already over.
/// </summary>
public sealed class ExchangeActiveCast
{
    internal ExchangeActiveCast(string broadcastId, string? castId)
    {
        BroadcastId = broadcastId;
        CastId = castId;
    }

    public string BroadcastId { get; }

    public string? CastId { get; internal set; }

    /// <summary>The channel the cast plays on; unknown until its import has produced the row.</summary>
    public Guid? ChannelId { get; set; }

    /// <summary>The sender's stop was received; whatever the accept starts afterwards must be undone.</summary>
    public bool StopRequested { get; internal set; }

    /// <summary>The playback start has returned, so a channel that is no longer playing means it ended.</summary>
    public bool Settled { get; set; }
}

/// <summary>
/// The casts this device started, oldest first. A <c>cast-stop</c> matches only an entry here - it can never
/// end playback the user chose themselves - so the ledger must neither lose a live cast to its own cap nor
/// keep the dead ones. Used from one thread (the UI's).
/// </summary>
public sealed class ExchangeCastLedger(int capacity = ExchangeCastLedger.DefaultCapacity)
{
    public const int DefaultCapacity = 16;

    private readonly List<ExchangeActiveCast> _casts = [];

    public int Count => _casts.Count;

    public IReadOnlyList<ExchangeActiveCast> Casts => _casts;

    /// <summary>
    /// Registers an accepted cast, or returns the entry the broadcast already has: a repeated offer is the same
    /// cast, so one stop reaches every continuation of it. Entries whose playback ended are dropped first, and
    /// a full ledger gives up its oldest entry only.
    /// </summary>
    public ExchangeActiveCast Track(string broadcastId, string? castId, Func<Guid, bool> isPlaying)
    {
        ArgumentException.ThrowIfNullOrEmpty(broadcastId);
        ArgumentNullException.ThrowIfNull(isPlaying);
        _casts.RemoveAll(item => item.Settled && item.ChannelId is { } channelId && !isPlaying(channelId));
        var cast = _casts.Find(item => string.Equals(item.BroadcastId, broadcastId, StringComparison.Ordinal));
        if (cast is not null)
        {
            _casts.Remove(cast);
            cast.CastId = castId ?? cast.CastId;
            cast.Settled = false;
        }
        else
        {
            cast = new ExchangeActiveCast(broadcastId, castId);
        }

        while (_casts.Count >= Math.Max(1, capacity))
        {
            _casts.RemoveAt(0);
        }

        _casts.Add(cast);
        return cast;
    }

    /// <summary>
    /// Takes the entry a stop names (by broadcast id, else by the optional cast id) out of the ledger and
    /// flags it stopped; null when the stop is not for a cast this device started.
    /// </summary>
    public ExchangeActiveCast? Stop(ExchangeCastStop stop)
    {
        ArgumentNullException.ThrowIfNull(stop);
        var cast = stop.BroadcastId is { } broadcastId
            ? _casts.Find(item => string.Equals(item.BroadcastId, broadcastId, StringComparison.Ordinal))
            : stop.CastId is { } castId
                ? _casts.Find(item => string.Equals(item.CastId, castId, StringComparison.Ordinal))
                : null;
        if (cast is null)
        {
            return null;
        }

        cast.StopRequested = true;
        _casts.Remove(cast);
        return cast;
    }

    /// <summary>Forgets a cast that never started (nothing was imported, or the start failed).</summary>
    public void Remove(ExchangeActiveCast cast) => _casts.Remove(cast);
}