using System.Globalization;
using System.Text.Json;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0260 / <c>DEVICE-EXCHANGE</c> 1.12 item EO: the four frames of the settings standard, on the control stream.
/// The writers return plain envelopes serialised like every other control frame; the readers return null for an answer
/// that is not this product's (another <c>appId</c>) or lacks a member, which the session then treats as no answer.
/// </summary>
public static class ExchangeSettingsFrames
{
    public const string SaveRequestType = "settings_save_standard";
    public const string SavedType = "settings_saved";
    public const string GetRequestType = "settings_get_standard";
    public const string PayloadType = "settings_standard_payload";

    /// <summary><c>settings_save_standard</c>; <c>timestamp</c> is informational, RFC 3339 in UTC.</summary>
    public static ExchangeSettingsSaveFrame Save(string settingsPayload, DateTimeOffset now) => new(SaveRequestType, 2,
        StandardSettings.AppId, StandardSettings.FormatVersion, settingsPayload,
        now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));

    public static ExchangeSettingsGetFrame Get() => new(GetRequestType, 2, StandardSettings.AppId);

    /// <summary>The server's <c>settings_saved</c>: the standard's new <c>version</c> and the server's <c>updatedAt</c>.</summary>
    public static ExchangeSettingsSaved? ReadSaved(JsonElement root)
    {
        if (!IsOwn(root) || !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt64(out var number))
        {
            return null;
        }

        return new ExchangeSettingsSaved(number, UpdatedAt(root));
    }

    /// <summary>The server's <c>settings_standard_payload</c>, not yet understood (<see cref="StandardSettings.Parse"/> does that).</summary>
    public static ExchangeSettingsPayload? ReadPayload(JsonElement root)
    {
        if (!IsOwn(root) || !root.TryGetProperty("formatVersion", out var format) || format.ValueKind != JsonValueKind.Number
            || !format.TryGetInt32(out var formatVersion)
            || !root.TryGetProperty("settingsPayload", out var payload) || payload.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return new ExchangeSettingsPayload(formatVersion, payload.GetString(), UpdatedAt(root));
    }

    private static bool IsOwn(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && string.Equals(ExchangeProtocol.String(root, "appId"), StandardSettings.AppId, StringComparison.Ordinal);

    private static DateTimeOffset? UpdatedAt(JsonElement root) =>
        ExchangeProtocol.String(root, "updatedAt") is { } text
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment)
            ? moment
            : null;
}

public sealed record ExchangeSettingsSaveFrame(string Type, int SchemaVersion, string AppId, int FormatVersion,
    string SettingsPayload, string Timestamp);

public sealed record ExchangeSettingsGetFrame(string Type, int SchemaVersion, string AppId);

public sealed record ExchangeSettingsSaved(long Version, DateTimeOffset? UpdatedAt);

public sealed record ExchangeSettingsPayload(int FormatVersion, string? SettingsPayload, DateTimeOffset? UpdatedAt);
