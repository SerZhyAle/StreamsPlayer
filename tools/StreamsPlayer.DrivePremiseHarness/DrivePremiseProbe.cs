using System.Text.Json;

namespace StreamsPlayer.DrivePremiseHarness;

/// <summary>
/// The probe record of SP-0202's premise experiment: a <c>fmsx-broadcast-*.json</c> file whose bytes carry the
/// DEVICE-EXCHANGE 8.1 wrapper exactly as the contract's Drive corpus (<c>vectors/records-v1.json</c>, section 8.1
/// cases) shapes it. The experiment must write a file the other client's conforming reader would accept, or the
/// result says nothing about the premise - a probe of any other shape could be refused for its shape alone.
/// </summary>
public static class DrivePremiseProbe
{
    /// <summary>The record ceiling the contract holds over every record, on the wire and on Drive alike.</summary>
    public const int MaxWrapperBytes = 16384;

    /// <summary>The working TTL of a broadcast record in the contract's Drive corpus.</summary>
    public const int BroadcastTtlSeconds = 120;

    /// <summary>Stable across every run, so the listing side can tell a harness probe from a device's own record.</summary>
    public const string ProbeDeviceId = "SP0202PremiseProbe000A";

    public static string NewBroadcastId()
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string FileNameFor(string broadcastId) => $"fmsx-broadcast-{broadcastId}.json";

    /// <summary>Whether a Drive file name is one this product would read (SP-0202 requirement 3): the contract's
    /// <c>fmsx-broadcast-*</c> and <c>fmsx-device-*</c> prefixes and nothing else.</summary>
    public static bool IsFmsxName(string name)
    {
        return name.StartsWith("fmsx-broadcast-", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal)
            || name.StartsWith("fmsx-device-", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal);
    }

    /// <summary>The 8.1 wrapper bytes for the probe, in the member order the contract's corpus uses.</summary>
    public static string BuildWrapper(string broadcastId, DateTimeOffset writtenAtUtc)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("type", "broadcast");
            writer.WriteString("writtenAt", writtenAtUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteNumber("ttlSeconds", BroadcastTtlSeconds);
            writer.WriteStartObject("record");
            writer.WriteString("broadcastId", broadcastId);
            writer.WriteStartObject("descriptor");
            writer.WriteEndObject();
            writer.WriteString("deviceId", ProbeDeviceId);
            writer.WriteString("mode", "AUDIO_ONLY");
            writer.WriteString("startedAt", writtenAtUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteString("title", "SP-0202 premise probe");
            writer.WriteString("updatedAt", writtenAtUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>The members a listing reads out of a found wrapper: enough to say "the other client's record, and
    /// it reads", without the harness growing a record model of its own - that model is the gated product work.</summary>
    public sealed record WrapperView(
        int? SchemaVersion,
        string? Type,
        int? TtlSeconds,
        string? WrittenAt,
        string? BroadcastId,
        string? DeviceId,
        string? Mode,
        string? Title);

    public static bool TryParseWrapper(ReadOnlySpan<byte> wrapperBytes, out WrapperView? view)
    {
        view = null;
        try
        {
            using var document = JsonDocument.Parse(wrapperBytes.ToArray());
            var root = document.RootElement;
            string? broadcastId = null, deviceId = null, mode = null, title = null;
            if (root.TryGetProperty("record", out var record) && record.ValueKind == JsonValueKind.Object)
            {
                broadcastId = String(record, "broadcastId");
                deviceId = String(record, "deviceId");
                mode = String(record, "mode");
                title = String(record, "title");
            }
            view = new WrapperView(
                Int(root, "schemaVersion"),
                String(root, "type"),
                Int(root, "ttlSeconds"),
                String(root, "writtenAt"),
                broadcastId,
                deviceId,
                mode,
                title);
            return view.SchemaVersion is not null && view.Type is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? String(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Int(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed) ? parsed : null;
}
