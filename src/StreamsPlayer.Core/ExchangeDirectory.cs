using System.Text;
using System.Text.Json;

namespace StreamsPlayer.Core;

/// <summary>One account device of the exchange directory (DEVICE-EXCHANGE 6.1), as far as this product reads it.</summary>
public sealed record ExchangeDeviceRecord(
    string DeviceId,
    string DeviceName,
    bool IsOnline);

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
    DateTimeOffset? UpdatedAt = null);

/// <summary>Whether this build can play a directory broadcast's addresses at all (SP-0201 requirement 2).</summary>
public enum ExchangeBroadcastSupport
{
    /// <summary>A LAN endpoint this build plays is declared (HTTP audio or RTSP video), or a legacy top-level address.</summary>
    Supported,

    /// <summary>Every declared endpoint is a transport this build does not implement yet (relay, tunnel).</summary>
    Unsupported,

    /// <summary>The descriptor's <c>schemaVersion</c> is higher than this build reads ("update the application").</summary>
    UnsupportedSchema
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
    IReadOnlyList<ExchangeBroadcastView> Broadcasts);

/// <summary>The whole directory as one immutable answer for the view.</summary>
public sealed record ExchangeDirectorySnapshot(
    long Revision,
    IReadOnlyList<ExchangeDeviceGroup> Groups,
    IReadOnlyCollection<string> LiveSourceIds,
    IReadOnlyCollection<string> LiveBroadcastIds);

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
public sealed class ExchangeDirectoryState
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
        if (ExchangeProtocol.String(root, "type") != "directory" || !TryReadRevision(root, out var revision))
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
        if (ExchangeProtocol.String(root, "type") != "changed" || !TryReadRevision(root, out var revision))
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
            LiveBroadcastIds = broadcastIds
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
                groups.Add(new ExchangeDeviceGroup(device.DeviceId, device.DeviceName, device.IsOnline, owned));
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
            (IReadOnlyCollection<string>)_broadcasts.Keys.ToArray());
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
                || ExchangeProtocol.String(device, "deviceId") is not { } id
                || ExchangeProtocol.String(device, "deviceName") is not { } name)
            {
                continue;
            }

            // The contract's tolerated shape: a presence outside the closed set reads as offline.
            devices[id] = new ExchangeDeviceRecord(id, name,
                ExchangeProtocol.String(device, "presence") == "online");
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
                || ExchangeProtocol.String(record, "broadcastId") is not { } id
                || ExchangeProtocol.String(record, "deviceId") is not { } deviceId
                || ExchangeProtocol.String(record, "mode") is not { } mode
                || !record.TryGetProperty("descriptor", out var descriptor)
                || descriptor.ValueKind != JsonValueKind.Object)
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
                            ExchangeProtocol.String(record, "title"), mode, Descriptor: null,
                            ReadUpdatedAt(record)),
                        ExchangeBroadcastSupport.UnsupportedSchema);
                }

                continue;
            }

            broadcasts[id] = new ExchangeBroadcastView(
                new ExchangeBroadcastRecord(id, deviceId,
                    ExchangeProtocol.String(record, "title") ?? read.Broadcast.Title,
                    mode, read.Broadcast, ReadUpdatedAt(record)),
                SupportOf(read.Broadcast));
        }

        return broadcasts;
    }

    /// <summary>
    /// SP-0201 / SP-0204: this build plays LAN HTTP audio, LAN RTSP video, and TUNNEL endpoints for both
    /// audio and video (and the legacy top-level address of a descriptor with no endpoints).
    /// </summary>
    internal static ExchangeBroadcastSupport SupportOf(FastMediaSorterBroadcast descriptor)
    {
        var declared = descriptor.IsVideoMode
            ? descriptor.Endpoints.FirstOrDefault(ep => IsDeclaredRtspEndpoint(ep) || IsTunnelVideoEndpoint(ep, descriptor.Mode))
            : descriptor.Endpoints.FirstOrDefault(ep => IsHttpAudioEndpoint(ep) || IsTunnelAudioEndpoint(ep));
        return declared is not null || descriptor.Endpoints.Count == 0
            ? ExchangeBroadcastSupport.Supported
            : ExchangeBroadcastSupport.Unsupported;
    }

    private static bool IsHttpAudioEndpoint(FastMediaSorterBroadcastEndpoint endpoint) =>
        string.Equals(endpoint.Mode, FastMediaSorterBroadcastDescriptor.AudioOnlyMode, StringComparison.Ordinal) &&
        string.Equals(endpoint.Transport, "HTTP", StringComparison.OrdinalIgnoreCase) &&
        Uri.TryCreate(endpoint.Url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static bool IsDeclaredRtspEndpoint(FastMediaSorterBroadcastEndpoint endpoint) =>
        string.Equals(endpoint.Transport, "RTSP", StringComparison.OrdinalIgnoreCase) &&
        Uri.TryCreate(endpoint.Url, UriKind.Absolute, out var uri) &&
        uri.Scheme.Equals("rtsp", StringComparison.OrdinalIgnoreCase);

    private static bool IsTunnelAudioEndpoint(FastMediaSorterBroadcastEndpoint endpoint) =>
        (endpoint.Mode is null || string.Equals(endpoint.Mode, FastMediaSorterBroadcastDescriptor.AudioOnlyMode, StringComparison.Ordinal)) &&
        string.Equals(endpoint.Transport, "TUNNEL", StringComparison.OrdinalIgnoreCase) &&
        ExchangeTunnelUrl.TryParse(endpoint.Url, out var tunnel) &&
        tunnel.Scheme == "http" &&
        endpoint.Inner is not null &&
        LaunchableAddress.TryParseHttp(endpoint.Inner, out _);

    private static bool IsTunnelVideoEndpoint(FastMediaSorterBroadcastEndpoint endpoint, string descriptorMode) =>
        (endpoint.Mode is null || string.Equals(endpoint.Mode, descriptorMode, StringComparison.Ordinal)) &&
        string.Equals(endpoint.Transport, "TUNNEL", StringComparison.OrdinalIgnoreCase) &&
        ExchangeTunnelUrl.TryParse(endpoint.Url, out var tunnel) &&
        (tunnel.Scheme is "rtsp" or "http") &&
        endpoint.Inner is not null &&
        LaunchableAddress.TryParse(endpoint.Inner, out _);

    private static HashSet<string> CollectIds(JsonElement removals, string member)
    {
        var ids = new HashSet<string>();
        if (removals.TryGetProperty(member, out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var id in list.EnumerateArray())
            {
                if (id.ValueKind == JsonValueKind.String && id.GetString() is { } value)
                {
                    ids.Add(value);
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
        ExchangeProtocol.String(record, "updatedAt") is { } stamp &&
        DateTimeOffset.TryParse(stamp, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var updatedAt)
            ? updatedAt
            : null;

    private static bool IsOversize(JsonElement record) =>
        StrictUtf8.GetByteCount(record.GetRawText()) > MaximumRecordBytes;
}
