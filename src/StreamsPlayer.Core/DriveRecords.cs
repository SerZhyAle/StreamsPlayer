using System.Text.Json;
using System.Globalization;

namespace StreamsPlayer.Core;

public sealed record DriveRecord(string Type, string DeviceId, int TtlSeconds, JsonElement Record)
{
    public override string ToString() => $"DriveRecord {{ Type = {Type} }}";
}

public static class DriveRecords
{
    public const int MaximumBytes = ExchangeDirectoryState.MaximumRecordBytes;
    public const int BroadcastTtlSeconds = 90;
    public const int DeviceTtlSeconds = 300;
    public static bool IsRecordName(string name) => name.EndsWith(".json", StringComparison.Ordinal)
        && (name.StartsWith("fmsx-broadcast-", StringComparison.Ordinal) || name.StartsWith("fmsx-device-", StringComparison.Ordinal));

    public static DriveRecord? Parse(ReadOnlyMemory<byte> bytes, string fileName)
    {
        if (bytes.Length > MaximumBytes || !IsRecordName(fileName))
            return null;
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var schema)
                || !schema.TryGetInt32(out var version) || version != 1
                || ExchangeProtocol.String(root, "type") is not ("broadcast" or "device")
                || !root.TryGetProperty("record", out var record) || record.ValueKind != JsonValueKind.Object
                || ExchangeProtocol.String(record, "deviceId") is not { Length: > 0 } deviceId)
                return null;
            var type = ExchangeProtocol.String(root, "type")!;
            var id = type == "device" ? deviceId : ExchangeProtocol.String(record, "broadcastId");
            if (id is null || fileName != $"fmsx-{type}-{id}.json")
                return null;
            var ttl = type == "device" ? DeviceTtlSeconds : BroadcastTtlSeconds;
            if (root.TryGetProperty("ttlSeconds", out var value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out var seconds) && seconds is >= 1 and <= 86400)
                ttl = seconds;
            return new DriveRecord(type, deviceId, ttl, record.Clone());
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    public static bool IsExpired(DriveRecord record, DateTimeOffset modifiedTime, DateTimeOffset serverTime) =>
        serverTime - modifiedTime >= TimeSpan.FromSeconds(record.TtlSeconds);

    public static byte[] Wrap(string type, object record, DateTimeOffset now) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        schemaVersion = 1, type, writtenAt = now.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        ttlSeconds = type == "broadcast" ? BroadcastTtlSeconds : DeviceTtlSeconds, record
    }, ExchangeProtocol.JsonOptions);
}

/// <summary>A poll stages a whole view before committing, and downloads only changed records.</summary>
public sealed class DriveDirectory(DriveRestClient client)
{
    private Dictionary<string, (DateTimeOffset Modified, string Name, DriveRecord? Record)> _cache = [];
    private long _revision;
    private string? _content;
    public ExchangeDirectorySnapshot Snapshot { get; private set; } = new(0, [], [], []);

    public void Clear()
    {
        _cache.Clear();
        _content = null;
        Snapshot = new(0, [], [], []);
    }

    public static ExchangeDirectorySnapshot? Merge(ExchangeDirectorySnapshot? server, ExchangeDirectorySnapshot? drive)
    {
        if (server is null) return drive;
        if (drive is null) return server;
        var sourceIds = server.LiveSourceIds.ToHashSet();
        var views = server.Groups.SelectMany(group => group.Broadcasts).Concat(drive.Groups.SelectMany(group => group.Broadcasts)
            .Where(view => !server.LiveBroadcastIds.Contains(view.Record.BroadcastId)
                && (view.Record.Descriptor?.SourceId is not { } id || !sourceIds.Contains(id))))
            .GroupBy(view => view.Record.BroadcastId).Select(group => group.First()).ToList();
        var devices = (server.Devices ?? []).Concat(drive.Devices ?? []).GroupBy(device => device.DeviceId)
            .Select(group => group.First()).ToList();
        var groups = views.GroupBy(view => view.Record.DeviceId).Select(group =>
        {
            var device = devices.FirstOrDefault(item => item.DeviceId == group.Key);
            return new ExchangeDeviceGroup(group.Key, device?.DeviceName ?? group.Key, device?.IsOnline ?? false, group.ToList());
        }).ToList();
        return new ExchangeDirectorySnapshot(Math.Max(server.Revision, drive.Revision), groups,
            views.Select(view => view.Record.Descriptor?.SourceId).OfType<string>().ToHashSet(),
            views.Select(view => view.Record.BroadcastId).ToHashSet(), devices);
    }

    public async Task<ExchangeDirectorySnapshot> PollAsync(string ownDeviceId, CancellationToken cancellation)
    {
        var listing = await client.ListAsync(cancellation).ConfigureAwait(false);
        var next = new Dictionary<string, (DateTimeOffset Modified, string Name, DriveRecord? Record)>();
        var devices = new List<JsonElement>();
        var broadcasts = new List<JsonElement>();
        foreach (var file in listing.Files)
        {
            DriveRecord? record;
            if (_cache.TryGetValue(file.Id, out var cached) && cached.Modified == file.ModifiedTime && cached.Name == file.Name)
                record = cached.Record;
            else
            {
                var bytes = await client.ReadAsync(file, cancellation).ConfigureAwait(false);
                record = bytes is null ? null : DriveRecords.Parse(bytes, file.Name);
            }
            next[file.Id] = (file.ModifiedTime, file.Name, record);
            if (record is null || record.DeviceId == ownDeviceId)
                continue;
            var expired = DriveRecords.IsExpired(record, file.ModifiedTime, listing.ServerTime);
            if (record.Type == "broadcast")
            {
                if (!expired)
                    broadcasts.Add(record.Record);
            }
            else
            {
                var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(record.Record)!;
                fields["presence"] = JsonSerializer.SerializeToElement(expired ? "offline" : "online");
                devices.Add(JsonSerializer.SerializeToElement(fields));
            }
        }
        var content = JsonSerializer.Serialize(new { devices, broadcasts });
        cancellation.ThrowIfCancellationRequested();
        if (_content == content)
        {
            _cache = next;
            return Snapshot;
        }
        var envelope = JsonSerializer.SerializeToElement(new { type = "directory", revision = ++_revision, devices, broadcasts });
        var state = new ExchangeDirectoryState();
        var snapshot = state.ApplyFull(envelope).Snapshot;
        cancellation.ThrowIfCancellationRequested();
        _cache = next;
        _content = content;
        Snapshot = snapshot;
        return snapshot;
    }
}
