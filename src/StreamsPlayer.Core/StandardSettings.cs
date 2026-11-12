using System.Text;
using System.Text.Json;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0260 / <c>DEVICE-EXCHANGE</c> 1.12 items EN to ER: the settings this product keeps as its STANDARD on an exchange
/// server. Every setting is declared here or nowhere: a key that is not in <see cref="Declarations"/> is local-only by
/// omission, so a device key, a password, a folder, an audio device, a port, a window rectangle, the listening history
/// and the resume markers are never written into a payload and never read out of one (item EQ).
/// </summary>
public static class StandardSettings
{
    /// <summary>The <c>appId</c> of this product (item EN); the server binds it to the device's <c>product</c> <c>streamsplayer-windows</c>.</summary>
    public const string AppId = "streamsplayer";

    /// <summary>The only <c>formatVersion</c> 1.12 defines (item EO).</summary>
    public const int FormatVersion = 1;

    /// <summary>The bound of <c>settingsPayload</c> in bytes of UTF-8 (item EO).</summary>
    public const int MaximumPayloadBytes = 65536;

    /// <summary>The <c>welcome.features</c> entry a server lists when it keeps standards (item EO).</summary>
    public const string Feature = "settings";

    public const string Appearance = "appearance";
    public const string Playback = "playback";
    public const string Interface = "interface";

    /// <summary>The group ids, in the order the settings screen shows them. Wire values, never translated (item EQ).</summary>
    public static IReadOnlyList<string> Groups { get; } = Array.AsReadOnly(new[] { Appearance, Playback, Interface });

    /// <summary>
    /// The product's own names of settings that stay on this device whatever an inbound payload says (item EQ). The
    /// list is for the tests and for the record: the reader honours it by reading only <see cref="Declarations"/>.
    /// </summary>
    public static IReadOnlyList<string> LocalOnlyKeys { get; } = Array.AsReadOnly(new[]
    {
        "audioOutputDevice", "frameFolder", "videoRecordingFolder", "audioRecordingFolder", "captureFoldersSchema",
        "resumePlaybackOnStartup", "resumeChannelIds", "listeningHistory", "lastSelectedChannelId", "catalogScrollAnchorId",
        "window", "windowBounds", "deviceKey", "deviceToken", "token", "password", "pin", "port", "host"
    });

    /// <summary>One exchangeable setting: its group, its key, how it is read from the state and how a value is applied.</summary>
    public sealed record Declaration(string Group, string Key, Func<CatalogState, object?> Read,
        Func<CatalogState, JsonElement, CatalogState?> Apply);

    public static IReadOnlyList<Declaration> Declarations { get; } = Array.AsReadOnly(new[]
    {
        EnumSetting<AppTheme>(Appearance, "theme", state => state.Theme, (state, value) => state with { Theme = value }),
        EnumSetting<StreamTileSize>(Appearance, "tileSize", state => state.TileSize, (state, value) => state with { TileSize = value }),
        EnumSetting<CatalogViewMode>(Appearance, "viewMode", state => state.ViewMode, (state, value) => state with { ViewMode = value }),
        BoolSetting(Appearance, "animatedBackdrop", state => state.AnimatedBackdrop, (state, value) => state with { AnimatedBackdrop = value }),
        BoolSetting(Appearance, "hideAdultContent", state => state.HideAdultContent, (state, value) => state with { HideAdultContent = value }),
        BoolSetting(Appearance, "mainWindowTopmost", state => state.MainWindowTopmost, (state, value) => state with { MainWindowTopmost = value }),
        BoolSetting(Appearance, "playerWindowTopmost", state => state.PlayerWindowTopmost, (state, value) => state with { PlayerWindowTopmost = value }),
        BoolSetting(Appearance, "keepPlayerControlsVisible", state => state.KeepPlayerControlsVisible,
            (state, value) => state with { KeepPlayerControlsVisible = value }),
        VolumeSetting(Playback, "videoVolume", state => state.VideoVolume, (state, value) => state with { VideoVolume = value }),
        BoolSetting(Playback, "videoMuted", state => state.VideoMuted, (state, value) => state with { VideoMuted = value }),
        VolumeSetting(Playback, "audioVolume", state => state.AudioVolume, (state, value) => state with { AudioVolume = value }),
        EnumSetting<AudioChannelMode>(Playback, "audioChannelMode", state => state.AudioChannelMode,
            (state, value) => state with { AudioChannelMode = value }),
        BoolSetting(Playback, "keepAwakeDuringPlayback", state => state.KeepAwakeDuringPlayback,
            (state, value) => state with { KeepAwakeDuringPlayback = value }),
        BoolSetting(Playback, "systemMediaControls", state => state.SystemMediaControls, (state, value) => state with { SystemMediaControls = value }),
        EnumSetting<MediaBackend>(Playback, "videoBackend", state => state.VideoBackend, (state, value) => state with { VideoBackend = value }),
        BoolSetting(Playback, "updateStreamPreviews", state => state.UpdateStreamPreviews, (state, value) => state with { UpdateStreamPreviews = value }),
        // An interface language never chosen is not written: an absent key leaves the reader's own choice alone.
        new Declaration(Interface, "language", state => state.Language?.ToString(),
            (state, element) => TryEnum<AppLanguage>(element, out var language) ? state with { Language = language } : null)
    });

    /// <summary>
    /// The payload of a save: <c>{"version":1,"groups":{...}}</c> holding the chosen groups and only their declared
    /// keys, in declaration order. Throws when no known group is chosen; the caller offers no save without one.
    /// </summary>
    public static string Serialize(CatalogState state, IEnumerable<string> groups)
    {
        ArgumentNullException.ThrowIfNull(state);
        var chosen = Groups.Where(new HashSet<string>(groups, StringComparer.Ordinal).Contains).ToArray();
        if (chosen.Length == 0)
        {
            throw new ArgumentException("At least one known group is required.", nameof(groups));
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", FormatVersion);
            writer.WriteStartObject("groups");
            foreach (var group in chosen)
            {
                writer.WriteStartObject(group);
                foreach (var declaration in Declarations.Where(item => item.Group == group))
                {
                    switch (declaration.Read(state))
                    {
                        case null:
                            break;
                        case bool flag:
                            writer.WriteBoolean(declaration.Key, flag);
                            break;
                        case int number:
                            writer.WriteNumber(declaration.Key, number);
                            break;
                        case object other:
                            writer.WriteString(declaration.Key, other.ToString());
                            break;
                    }
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        if (buffer.Length > MaximumPayloadBytes)
        {
            throw new InvalidOperationException("The standard settings payload exceeds its bound.");
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>
    /// Item ER: the whole answer is understood before a single value is applied. A <paramref name="formatVersion"/>
    /// above <see cref="FormatVersion"/> is <see cref="StandardSettingsReadFailure.FormatUnsupported"/>; a payload that
    /// is above the bound, not one object, without a matching <c>version</c> or without a <c>groups</c> object of
    /// objects is <see cref="StandardSettingsReadFailure.Damaged"/>.
    /// </summary>
    public static StandardSettingsReadResult Parse(int formatVersion, string? payload)
    {
        if (formatVersion > FormatVersion)
        {
            return new StandardSettingsReadResult(null, StandardSettingsReadFailure.FormatUnsupported);
        }

        if (formatVersion != FormatVersion || payload is null || Encoding.UTF8.GetByteCount(payload) > MaximumPayloadBytes)
        {
            return new StandardSettingsReadResult(null, StandardSettingsReadFailure.Damaged);
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var number) || number != formatVersion
                || !root.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Object)
            {
                return new StandardSettingsReadResult(null, StandardSettingsReadFailure.Damaged);
            }

            var read = new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>(StringComparer.Ordinal);
            foreach (var group in groups.EnumerateObject())
            {
                if (group.Value.ValueKind != JsonValueKind.Object)
                {
                    return new StandardSettingsReadResult(null, StandardSettingsReadFailure.Damaged);
                }

                var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var member in group.Value.EnumerateObject())
                {
                    values[member.Name] = member.Value.Clone();
                }

                read[group.Name] = values;
            }

            return new StandardSettingsReadResult(new StandardSettingsDocument(read), StandardSettingsReadFailure.None);
        }
        catch (JsonException)
        {
            return new StandardSettingsReadResult(null, StandardSettingsReadFailure.Damaged);
        }
    }

    /// <summary>
    /// Applies the chosen groups of a parsed standard to <paramref name="state"/>: only declared keys, only values of the
    /// right type and range. An unknown group or key, a group not chosen, and a key that is absent change nothing.
    /// </summary>
    public static StandardSettingsApplied Apply(CatalogState state, StandardSettingsDocument document, IEnumerable<string> groups)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(document);
        var chosen = new HashSet<string>(groups, StringComparer.Ordinal);
        var applied = new List<string>();
        var result = state;
        foreach (var declaration in Declarations)
        {
            if (!chosen.Contains(declaration.Group)
                || !document.Groups.TryGetValue(declaration.Group, out var values)
                || !values.TryGetValue(declaration.Key, out var value)
                || declaration.Apply(result, value) is not { } next)
            {
                continue;
            }

            result = next;
            applied.Add(declaration.Group + "." + declaration.Key);
        }

        return new StandardSettingsApplied(result, applied);
    }

    private static Declaration BoolSetting(string group, string key, Func<CatalogState, bool> read,
        Func<CatalogState, bool, CatalogState> apply) =>
        new(group, key, state => read(state), (state, element) => element.ValueKind switch
        {
            JsonValueKind.True => apply(state, true),
            JsonValueKind.False => apply(state, false),
            _ => null
        });

    private static Declaration VolumeSetting(string group, string key, Func<CatalogState, int> read,
        Func<CatalogState, int, CatalogState> apply) =>
        new(group, key, state => read(state), (state, element) =>
            element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var volume) && volume is >= 0 and <= 100
                ? apply(state, volume)
                : null);

    private static Declaration EnumSetting<T>(string group, string key, Func<CatalogState, T> read,
        Func<CatalogState, T, CatalogState> apply) where T : struct, Enum =>
        new(group, key, state => read(state).ToString(), (state, element) =>
            TryEnum<T>(element, out var value) ? apply(state, value) : null);

    // A member name only: Enum.TryParse would also take "7" or "Dark, Light", which no writer of this format produces.
    private static bool TryEnum<T>(JsonElement element, out T value) where T : struct, Enum
    {
        value = default;
        return element.ValueKind == JsonValueKind.String && element.GetString() is { } text
            && Enum.GetNames<T>().Contains(text, StringComparer.Ordinal)
            && Enum.TryParse(text, ignoreCase: false, out value);
    }
}

public enum StandardSettingsReadFailure
{
    None,

    /// <summary>A <c>formatVersion</c> above what this program implements: nothing is applied (item ER).</summary>
    FormatUnsupported,

    /// <summary>A payload that is not the shape of item EO: nothing is applied.</summary>
    Damaged
}

/// <summary>A standard read and understood as a whole: group id to key to value, nothing applied yet.</summary>
public sealed record StandardSettingsDocument(IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>> Groups);

public sealed record StandardSettingsReadResult(StandardSettingsDocument? Document, StandardSettingsReadFailure Failure);

/// <summary>The state after a read, and the <c>group.key</c> names that changed it (equal values included).</summary>
public sealed record StandardSettingsApplied(CatalogState State, IReadOnlyList<string> AppliedKeys);
