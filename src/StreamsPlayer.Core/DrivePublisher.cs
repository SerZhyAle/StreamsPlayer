namespace StreamsPlayer.Core;

public sealed record DriveAnnouncement(string BroadcastId, object Record)
{
    public override string ToString() => nameof(DriveAnnouncement);
}

/// <summary>Ownership is retained by the host in the same protected file as the grant, across process exits.</summary>
public sealed class DrivePublisher(DriveRestClient client, Dictionary<string, string> ownedFiles,
    Func<CancellationToken, Task> saveOwnership)
{
    private TimeSpan _renewAt;
    private readonly Dictionary<string, byte[]> _published = [];

    public async Task SynchronizeAsync(string deviceId, IReadOnlyList<DriveAnnouncement> broadcasts,
        DateTimeOffset now, TimeSpan elapsed, CancellationToken cancellation)
    {
        var desired = broadcasts.ToDictionary(row => $"fmsx-broadcast-{row.BroadcastId}.json", row =>
        {
            var fields = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(
                System.Text.Json.JsonSerializer.Serialize(row.Record))!;
            fields["deviceId"] = System.Text.Json.JsonSerializer.SerializeToElement(deviceId);
            return DriveRecords.Wrap("broadcast", fields, now);
        });
        if (broadcasts.Count > 0)
            desired[$"fmsx-device-{deviceId}.json"] = DriveRecords.Wrap("device", new
            {
                deviceId, deviceName = "STREAMS Player", product = "streamsplayer-windows",
                presence = "online", lastSeen = now, updatedAt = now
            }, now);
        foreach (var name in ownedFiles.Keys.Except(desired.Keys).ToArray())
        {
            await client.DeleteAsync(ownedFiles[name], cancellation).ConfigureAwait(false);
            ownedFiles.Remove(name);
            _published.Remove(name);
            await saveOwnership(cancellation).ConfigureAwait(false);
        }
        var renew = elapsed >= _renewAt;
        foreach (var pair in desired)
        {
            // writtenAt changes every call; compare the record itself to notice an endpoint/key change immediately.
            var parsed = DriveRecords.Parse(pair.Value, pair.Key)
                ?? throw new InvalidDataException("Outgoing Drive record exceeds the contract or has an invalid identity.");
            var content = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(parsed.Record);
            if (!renew && _published.TryGetValue(pair.Key, out var previous)
                && (parsed.Type == "device" || previous.AsSpan().SequenceEqual(content)))
                continue;
            ownedFiles.TryGetValue(pair.Key, out var fileId);
            try
            {
                fileId = await client.WriteAsync(pair.Key, pair.Value, fileId, cancellation).ConfigureAwait(false);
            }
            catch (DriveRequestException exception) when (exception.StatusCode == 404 && fileId is not null)
            {
                fileId = await client.WriteAsync(pair.Key, pair.Value, null, cancellation).ConfigureAwait(false);
            }
            ownedFiles[pair.Key] = fileId;
            await saveOwnership(cancellation).ConfigureAwait(false);
            _published[pair.Key] = content;
        }
        if (renew)
            _renewAt = elapsed + TimeSpan.FromSeconds(DriveRecords.BroadcastTtlSeconds / 3 - 10);
    }
}
