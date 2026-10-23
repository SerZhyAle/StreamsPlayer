using System.Text;
using System.Text.Json;

namespace StreamsPlayer.Core;

/// <summary>One account device of the exchange directory (DEVICE-EXCHANGE 6.1), as far as this product reads it.</summary>
public sealed record ExchangeDeviceRecord(
    string DeviceId,
    string DeviceName,
    bool IsOnline,
    /// <summary>SP-0217: the receiver pairs the device declared (`receiver.plays`, or the modes and
    /// transports when it declared no pairs) - what a cast offer is gated on. Null when it declared
    /// nothing, which is the case of a device that is not a receiver.</summary>
    IReadOnlyList<ExchangePlayPair>? Plays = null,
    /// <summary>SP-0226 / item DF: the public key (the 65-byte point) of `e2eKey` when the record carries a
    /// usable one, else null. Public by design - it lets others seal to the device and opens nothing.</summary>
    byte[]? E2eKey = null,
    /// <summary>SP-0226 / item DF: whether `capabilities` lists `e2e-v1`.</summary>
    bool HasE2eCapability = false,
    /// <summary>SP-0226 / item DH: `lastSeen`, which orders the devices a box cuts to forty.</summary>
    DateTimeOffset? LastSeen = null);

/// <summary>One live broadcast of the exchange directory (DEVICE-EXCHANGE 6.3).</summary>
/// <remarks>
/// <para><see cref="Descriptor"/> is the <c>LIVE-BROADCAST</c> descriptor read through the one reader every
/// hand-off uses, so a directory channel and a link channel can never diverge. It is <c>null</c> only for a
/// record whose descriptor carried a higher <c>schemaVersion</c> than this build reads - shown, with the
/// "update the application" reason, and never imported.</para>
/// <para>A record that did not read at all is absent from the directory, never an exception: the contract
/// makes a reader skip a record of an unknown kind and refuse an oversize one, and a malformed descriptor
/// is the same class of foreign input (SP-0201 requirement 2).</para>
/// </remarks>
public sealed record ExchangeBroadcastRecord(
    string BroadcastId,
    string DeviceId,
    string? Title,
    string Mode,
    FastMediaSorterBroadcast? Descriptor,
    DateTimeOffset? UpdatedAt = null,
    /// <summary>
    /// SP-0222: the pair of a protected broadcast the server handed this device in the record (item BF for an own
    /// device, item BD for a guest's grant), so the person's own broadcast plays with no question. A secret held
    /// in memory with the snapshot: it is never printed, logged, shown, stored or copied into the library.
    /// </summary>
    ExchangeRelayCredentials? Credentials = null,
    /// <summary>SP-0226 / item DK: the record's `sealed` box - the keys and the credentials of a sealed
    /// broadcast. Opened by whoever plays the broadcast; never opened, logged or altered by the directory.</summary>
    ExchangeSealedBox? Sealed = null,
    /// <summary>SP-0226: the record carries a `sealed` member this build could not read as a box.</summary>
    bool SealedUnreadable = false,
    /// <summary>SP-0226 / item DJ: the grant a guest's directory carries this record under; its box has the context `grant:&lt;id&gt;`.</summary>
    string? GrantId = null);

/// <summary>Whether this build can play a directory broadcast's addresses at all (SP-0201 requirement 2).</summary>
public enum ExchangeBroadcastSupport
{
    /// <summary>An endpoint this build plays is declared (LAN HTTP, RTSP, relay or tunnel), or a legacy top-level address.</summary>
    Supported,

    /// <summary>Every declared endpoint is a transport or shape this build does not implement (for example P2P).</summary>
    Unsupported,

    /// <summary>The descriptor's <c>schemaVersion</c> is higher than this build reads ("update the application").</summary>
    UnsupportedSchema,

    /// <summary>
    /// SP-0226 / item DP: the only exchange endpoints declared are not sealed (an older producer, or a device with
    /// no key). They are never played as clear bytes; the state is shown, not hidden.
    /// </summary>
    Unsealed
}

/// <summary>One broadcast as the view lists it, with the support verdict already made.</summary>
public sealed record ExchangeBroadcastView(
    ExchangeBroadcastRecord Record,
    ExchangeBroadcastSupport Support);

/// <summary>The account's live broadcasts grouped by device, with presence (SP-0201 requirement 2).</summary>
public sealed record ExchangeDeviceGroup(
    string DeviceId,
    string DeviceName,
    bool IsOnline,
    IReadOnlyList<ExchangeBroadcastView> Broadcasts,
    /// <summary>SP-0217: the receiver pairs this device declared, when it declared any - the gate a cast
    /// offer is checked against. Null is a device that is not a receiver.</summary>
    IReadOnlyList<ExchangePlayPair>? Plays = null);

/// <summary>The whole directory as one immutable answer for the view.</summary>
public sealed record ExchangeDirectorySnapshot(
    long Revision,
    IReadOnlyList<ExchangeDeviceGroup> Groups,
    IReadOnlyCollection<string> LiveSourceIds,
    IReadOnlyCollection<string> LiveBroadcastIds,
    /// <summary>
    /// SP-0217: every device of the account, not only the ones with live broadcasts - the cast menu
    /// offers a receiver by name, and a receiver broadcasts nothing. The own device is hidden from it
    /// by the view's own-device rule, like everywhere else.
    /// </summary>
    IReadOnlyList<ExchangeDeviceRecord>? Devices = null);

/// <summary>What one directory frame did to the state, and what the client owes the server next.</summary>
/// <remarks>
/// <see cref="RelistNeeded"/> is the revision rule of DEVICE-EXCHANGE 7.4: a gap in <c>revision</c> is
/// answered with <c>list</c> again, because a delta applied on a gap would present a partial directory as
/// the whole one. The state is handed back untouched in that case - the next full list repairs it.
/// </remarks>
public sealed record ExchangeDirectoryTransition(
    ExchangeDirectorySnapshot Snapshot,
    bool RelistNeeded);

/// <summary>
/// The account's live directory state (SP-0201). The wire rules live here, not in the window: skip a record
/// of an unknown kind, refuse an oversize record, ignore unknown members, track <c>revision</c>, and never
/// let a foreign record raise into the connection.
/// </summary>
public sealed partial class ExchangeDirectoryState
{
    /// <summary>DEVICE-EXCHANGE 6: a record is at most 16 KiB of JSON; a bigger one is skipped, never an error.</summary>
    public const int MaximumRecordBytes = 16 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly Dictionary<string, ExchangeDeviceRecord> _devices = [];
    private readonly Dictionary<string, ExchangeBroadcastView> _broadcasts = [];
    private long _revision;

    public ExchangeDirectorySnapshot Snapshot { get; private set; } = new(0, [], [], []);

    /// <summary>Applies the <c>directory</c> answer to <c>list</c>: the whole record set at one revision.</summary>
    public ExchangeDirectoryTransition ApplyFull(JsonElement root)
    {
        if (ReadString(root, "type") != "directory" || !TryReadRevision(root, out var revision))
        {
            return new(Snapshot, RelistNeeded: false);
        }

        var devices = ReadDevices(root);
        var broadcasts = ReadBroadcastMap(root, "broadcasts");
        Replace(devices, broadcasts, revision);
        return new(Snapshot, RelistNeeded: false);
    }

    /// <summary>
    /// Applies one <c>changed</c> push. A revision that is not exactly one step ahead of the state is a
    /// missed push in one direction or a stale repeat in the other; both leave the state untouched and ask
    /// for a full <c>list</c>, which is also what repairs a view that would otherwise miss records.
    /// </summary>
    public ExchangeDirectoryTransition ApplyChange(JsonElement root)
    {
        if (ReadString(root, "type") != "changed" || !TryReadRevision(root, out var revision))
        {
            return new(Snapshot, RelistNeeded: false);
        }

        if (revision != _revision + 1)
        {
            return new(Snapshot, RelistNeeded: true);
        }

        var devices = new Dictionary<string, ExchangeDeviceRecord>(_devices);
        var broadcasts = new Dictionary<string, ExchangeBroadcastView>(_broadcasts);
        if (root.TryGetProperty("upserts", out var upserts) && upserts.ValueKind == JsonValueKind.Object)
        {
            MergeDevices(devices, upserts);
            foreach (var view in ReadBroadcasts(upserts, "broadcasts"))
            {
                broadcasts[view.Record.BroadcastId] = view;
            }
        }

        if (root.TryGetProperty("removals", out var removals) && removals.ValueKind == JsonValueKind.Object)
        {
            var removedBroadcasts = CollectIds(removals, "broadcastIds");
            var removedDevices = CollectIds(removals, "deviceIds");
            // A device that leaves the directory takes its broadcasts with it (DEVICE-EXCHANGE 5.2), and a
            // broadcaster that goes offline has ended them (7.3) - the server says so, this only agrees.
            removedBroadcasts.UnionWith(broadcasts.Values
                .Where(view => removedDevices.Contains(view.Record.DeviceId))
                .Select(view => view.Record.BroadcastId));
            foreach (var id in removedBroadcasts)
            {
                broadcasts.Remove(id);
            }

            foreach (var id in removedDevices)
            {
                devices.Remove(id);
            }
        }

        Replace(devices, broadcasts, revision);
        return new(Snapshot, RelistNeeded: false);
    }

    /// <summary>Applies the view's own-device rule: this device's records are never listed to itself.</summary>
    /// <remarks>
    /// The whole snapshot answers to the rule, not its group list alone: the live-id sets are what the
    /// ended marking reads, and a record of this device would keep a foreign row un-ended from beyond the
    /// view that never showed it.
    /// </remarks>
    public static ExchangeDirectorySnapshot HideOwnDevice(ExchangeDirectorySnapshot snapshot, string ownDeviceId)
    {
        var hidden = snapshot.Groups.Where(group => group.DeviceId == ownDeviceId).ToList();
        if (hidden.Count == 0)
        {
            return snapshot;
        }

        var broadcastIds = new HashSet<string>(snapshot.LiveBroadcastIds);
        var sourceIds = new HashSet<string>(snapshot.LiveSourceIds);
        foreach (var group in hidden)
        {
            foreach (var broadcast in group.Broadcasts)
            {
                broadcastIds.Remove(broadcast.Record.BroadcastId);
                if (broadcast.Record.Descriptor?.SourceId is { } sourceId)
                {
                    sourceIds.Remove(sourceId);
                }
            }
        }

        return snapshot with
        {
            Groups = [.. snapshot.Groups.Where(group => group.DeviceId != ownDeviceId)],
            LiveSourceIds = sourceIds,
            LiveBroadcastIds = broadcastIds,
            Devices = [.. snapshot.Devices.Where(device => device.DeviceId != ownDeviceId)]
        };
    }

    private void Replace(
        Dictionary<string, ExchangeDeviceRecord> devices,
        Dictionary<string, ExchangeBroadcastView> broadcasts,
        long revision)
    {
        _devices.Clear();
        foreach (var device in devices)
        {
            _devices[device.Key] = device.Value;
        }

        _broadcasts.Clear();
        foreach (var broadcast in broadcasts)
        {
            _broadcasts[broadcast.Key] = broadcast.Value;
        }

        _revision = revision;
        Snapshot = BuildSnapshot(revision);
    }

    private ExchangeDirectorySnapshot BuildSnapshot(long revision)
    {
        var byDevice = _broadcasts.Values.GroupBy(view => view.Record.DeviceId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<ExchangeBroadcastView>)[.. group]);
        var groups = new List<ExchangeDeviceGroup>();
        foreach (var device in _devices.Values)
        {
            if (byDevice.Remove(device.DeviceId, out var owned))
            {
                groups.Add(new ExchangeDeviceGroup(device.DeviceId, device.DeviceName, device.IsOnline, owned, device.Plays));
            }
        }

        // A broadcast whose device record never arrived is still listed, under the id its own record names -
        // the device-name rule covers naming, not existence.
        foreach (var stray in byDevice)
        {
            groups.Add(new ExchangeDeviceGroup(stray.Key, stray.Key, IsOnline: false, stray.Value));
        }

        var sourceIds = new HashSet<string>();
        foreach (var view in _broadcasts.Values)
        {
            if (view.Record.Descriptor?.SourceId is { } sourceId)
            {
                sourceIds.Add(sourceId);
            }
        }

        return new ExchangeDirectorySnapshot(revision, groups, sourceIds,
            (IReadOnlyCollection<string>)_broadcasts.Keys.ToArray(),
            [.. _devices.Values]);
    }

    /// <summary>
    /// A string member, or <c>null</c> when it is absent, not a string, or not readable as text. A string
    /// escape that is a lone surrogate (<c>"\ud800"</c>) parses as JSON but <c>GetString</c> throws
    /// <see cref="InvalidOperationException"/> on it; foreign input must cost the record, never the
    /// connection (SP-0201 requirement 2).
    /// </summary>
    private static string? ReadString(JsonElement element, string member)
    {
        try
        {
            return ExchangeProtocol.String(element, member);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool TryReadRevision(JsonElement root, out long revision)
    {
        revision = 0;
        return root.TryGetProperty("revision", out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out revision) && revision > 0;
    }

    private static Dictionary<string, ExchangeDeviceRecord> ReadDevices(JsonElement root)
    {
        var devices = new Dictionary<string, ExchangeDeviceRecord>();
        MergeDevices(devices, root);
        return devices;
    }

    private static void MergeDevices(Dictionary<string, ExchangeDeviceRecord> devices, JsonElement container)
    {
        if (!container.TryGetProperty("devices", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var device in list.EnumerateArray())
        {
            if (device.ValueKind != JsonValueKind.Object || IsOversize(device)
                || ReadString(device, "deviceId") is not { } id
                || ReadString(device, "deviceName") is not { } name)
            {
                continue;
            }

            // The contract's tolerated shape: a presence outside the closed set reads as offline.
            var (hasCapability, e2eKey) = ReadE2e(device);
            devices[id] = new ExchangeDeviceRecord(id, name,
                ReadString(device, "presence") == "online", ReadPlays(device), e2eKey, hasCapability, ReadLastSeen(device));
        }
    }

    /// <summary>
    /// SP-0217 / `DEVICE-EXCHANGE` item E: the receiver pairs a device declared - `plays` when present,
    /// else the cross product of `modes` and `transports`. A receiver member that is not an object, or
    /// holds none of the three, is a device that declared nothing as a receiver.
    /// </summary>
    private static IReadOnlyList<ExchangePlayPair>? ReadPlays(JsonElement device)
    {
        if (!device.TryGetProperty("receiver", out var receiver) || receiver.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var pairs = new List<ExchangePlayPair>();
        if (receiver.TryGetProperty("plays", out var plays) && plays.ValueKind == JsonValueKind.Array)
        {
            foreach (var play in plays.EnumerateArray())
            {
                var mode = ReadString(play, "mode");
                var transport = ReadString(play, "transport");
                if (mode is not null && transport is not null)
                {
                    pairs.Add(new ExchangePlayPair(mode, transport));
                }
            }

            return pairs;
        }

        var modes = ReadStrings(receiver, "modes");
        var transports = ReadStrings(receiver, "transports");
        foreach (var mode in modes)
        {
            foreach (var transport in transports)
            {
                pairs.Add(new ExchangePlayPair(mode, transport));
            }
        }

        return pairs.Count > 0 ? pairs : null;
    }

    private static List<string> ReadStrings(JsonElement container, string member)
    {
        var values = new List<string>();
        if (container.TryGetProperty(member, out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in list.EnumerateArray())
            {
                if (ReadStringValue(value) is { } text)
                {
                    values.Add(text);
                }
            }
        }

        return values;
    }

    private static string? ReadStringValue(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        try
        {
            return value.GetString();
        }
        catch (InvalidOperationException)
        {
            // A lone-surrogate escape names nothing a device can play; see ReadString.
            return null;
        }
    }

    private static IEnumerable<ExchangeBroadcastView> ReadBroadcasts(JsonElement container, string member)
    {
        return ReadBroadcastMap(container, member).Values;
    }

    private static Dictionary<string, ExchangeBroadcastView> ReadBroadcastMap(JsonElement container, string member)
    {
        if (!container.TryGetProperty(member, out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var broadcasts = new Dictionary<string, ExchangeBroadcastView>();
        foreach (var record in list.EnumerateArray())
        {
            if (record.ValueKind != JsonValueKind.Object || IsOversize(record)
                || ReadString(record, "broadcastId") is not { } id
                || ReadString(record, "deviceId") is not { } deviceId
                || ReadString(record, "mode") is not { } mode
                || !record.TryGetProperty("descriptor", out var descriptor)
                || descriptor.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var sealedBox = ReadSealedBox(record, out var sealedUnreadable);
            var grantId = ReadString(record, "grantId");
            if (grantId is { Length: > 0 } && sealedBox is { Keys.Count: 0 })
            {
                continue;
            }

            // The one reader, with its own size cap and its own refusals - a record's descriptor never gets
            // a second, gentler path because it arrived over a socket instead of a link (SP-0201 req 3).
            var read = FastMediaSorterBroadcastDescriptor.Read(descriptor.GetRawText());
            if (!read.IsAccepted || read.Broadcast is null)
            {
                // A higher schemaVersion is what a link shows as "update the application"; the record is
                // listed with that reason rather than hidden. Anything else malformed stays skipped.
                if (read.Status == FastMediaSorterBroadcastReadStatus.UnsupportedSchema)
                {
                    broadcasts[id] = new ExchangeBroadcastView(
                        new ExchangeBroadcastRecord(id, deviceId,
                            ReadString(record, "title"), mode, Descriptor: null,
                            ReadUpdatedAt(record)),
                        ExchangeBroadcastSupport.UnsupportedSchema);
                }

                continue;
            }

            broadcasts[id] = new ExchangeBroadcastView(
                new ExchangeBroadcastRecord(id, deviceId,
                    ReadString(record, "title") ?? read.Broadcast.Title,
                    mode, read.Broadcast, ReadUpdatedAt(record), ReadCredentials(record),
                    sealedBox, sealedUnreadable, grantId),
                SupportOf(read.Broadcast));
        }

        return broadcasts;
    }

    /// <summary>
    /// <c>credentials</c> of a broadcast record (<c>DEVICE-EXCHANGE</c> 6.3): <c>user</c> and <c>password</c>, both
    /// strings. Any other shape - absent, not an object, a member missing or outside the limits - reads as no pair,
    /// and the broadcast then asks the person as any protected broadcast does. A <c>sealed</c> box (section 27) is
    /// carried beside it unopened (<see cref="ReadSealedBox"/>) and wins at the point of use (item DP).
    /// </summary>
    private static ExchangeRelayCredentials? ReadCredentials(JsonElement record) =>
        !record.TryGetProperty("sealed", out _) && record.TryGetProperty("credentials", out var pair) && pair.ValueKind == JsonValueKind.Object
            ? ExchangeRelayCredentials.Create(ReadString(pair, "user"), ReadString(pair, "password"))
            : null;

    /// <summary>
    /// SP-0201 / SP-0204 / SP-0203 / SP-0212 (L30): a broadcast is supported when playback has an attempt
    /// to try - at least one declared endpoint the legs play (LAN HTTP audio, LAN RTSP and HTTP video,
    /// relay, tunnel), or, when the list holds none of them, the legacy top-level address that
    /// <see cref="FastMediaSorterBroadcast.PlaybackAttemptEndpoints"/> falls back to. The judgment and
    /// the fallback agree on the launchability of that address, so what the directory lists, what a push
    /// refreshes and what a cast offer accepts cannot disagree with what playback plays.
    /// </summary>
    internal static ExchangeBroadcastSupport SupportOf(FastMediaSorterBroadcast descriptor)
    {
        if (FastMediaSorterBroadcastAttempts.PlaybackAttempts(descriptor.Mode, descriptor.Endpoints).Count > 0)
        {
            return ExchangeBroadcastSupport.Supported;
        }

        // SP-0226: an exchange endpoint dropped only for want of a seal is a state to show, not an address to
        // fall back to - the top-level address of such a descriptor is the same relay or tunnel.
        if (ExchangeEndpointKinds.IsExchangeAddress(descriptor.Url))
        {
            return ExchangeBroadcastSupport.Unsealed;
        }

        if (descriptor.Endpoints.Count == 0 || StreamMediaKindClassifier.IsLaunchable(descriptor.Url))
        {
            return ExchangeBroadcastSupport.Supported;
        }

        return FastMediaSorterBroadcastAttempts.HasUnsealedExchangeEndpoint(descriptor.Mode, descriptor.Endpoints)
            ? ExchangeBroadcastSupport.Unsealed
            : ExchangeBroadcastSupport.Unsupported;
    }

    private static HashSet<string> CollectIds(JsonElement removals, string member)
    {
        var ids = new HashSet<string>();
        if (removals.TryGetProperty(member, out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var id in list.EnumerateArray())
            {
                if (id.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                try
                {
                    if (id.GetString() is { } value)
                    {
                        ids.Add(value);
                    }
                }
                catch (InvalidOperationException)
                {
                    // A lone-surrogate id names no record this build could hold; see ReadString.
                }
            }
        }

        return ids;
    }

    /// <summary>
    /// LIVE-BROADCAST item G (0.18): of the two records one <c>sourceId</c> keeps, the later
    /// <c>updatedAt</c> wins. The member is informational everywhere else, and a value that does not
    /// read as a UTC timestamp is absent, never an error.
    /// </summary>
    private static DateTimeOffset? ReadUpdatedAt(JsonElement record) =>
        ReadString(record, "updatedAt") is { } stamp &&
        DateTimeOffset.TryParse(stamp, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var updatedAt)
            ? updatedAt
            : null;

    private static bool IsOversize(JsonElement record) =>
        StrictUtf8.GetByteCount(record.GetRawText()) > MaximumRecordBytes;
}
