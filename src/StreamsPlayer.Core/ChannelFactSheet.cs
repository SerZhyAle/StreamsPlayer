using System.Globalization;
using System.Text;

namespace StreamsPlayer.Core;

/// <summary>The three answers the About window separates (SP-0053 decision 1).</summary>
/// <remarks>
/// The split is the point of the window: <see cref="Catalog"/> holds what a maintainer claims about a
/// stream, <see cref="Stream"/> holds what the stream was measured to send. Flattening them would put a
/// claim and a measurement on adjacent lines with nothing to tell them apart.
/// </remarks>
public enum ChannelFactGroup
{
    Channel,
    Catalog,
    Stream
}

/// <summary>
/// One labelled line of the About window.
/// </summary>
/// <param name="LabelKey">Localization key of the property name.</param>
/// <param name="Text">A value that is already final - a title, an address, a number.</param>
/// <param name="ValueKey">Localization key of a fixed value, used instead of <paramref name="Text"/>.</param>
/// <param name="ValueFormatKey">
/// Localization key of a <c>{0}</c> template that wraps <paramref name="Text"/> in its unit. Kept apart
/// from the number so the unit is translatable while the formatting of the number stays in one place.
/// </param>
/// <remarks>
/// Every value field null means the property is simply not set. The line still appears, with an empty
/// value, so every channel produces the same shape and a missing property is visibly missing.
/// </remarks>
public sealed record ChannelFact(
    ChannelFactGroup Group,
    string LabelKey,
    string? Text = null,
    string? ValueKey = null,
    string? ValueFormatKey = null);

/// <summary>
/// What a stream was observed to be sending. Platform-neutral by design: the media engine that fills
/// this in lives in the app layer, and Core owns only the shape and how it is put into words.
/// </summary>
/// <remarks>
/// Every member is optional in practice - an engine reports what the stream told it and nothing more.
/// A zero or null value renders as unset, never as "0 Hz" or "0×0": "unknown" and "none" are different
/// statements, and only the first is true here. Nothing may be reported as measured that was not.
/// </remarks>
public sealed record StreamTransmission(
    string? VideoCodec,
    int Width,
    int Height,
    double? FrameRate,
    string? AudioCodec,
    int AudioChannels,
    int SampleRate,
    double? ObservedKilobitsPerSecond);

/// <summary>
/// SP-0053: turns a channel - and, when one exists, a measurement of its stream - into the ordered
/// name/value lines the About window shows and copies.
/// </summary>
public static class ChannelFactSheet
{
    /// <summary>The stored half: everything the app already knows without touching the network.</summary>
    public static IReadOnlyList<ChannelFact> Describe(
        StreamChannel channel,
        IReadOnlyList<string> collectionNames,
        IFormatProvider? formats = null)
    {
        var culture = formats ?? CultureInfo.CurrentCulture;
        return
        [
            new ChannelFact(ChannelFactGroup.Channel, "FieldTitle", channel.Title),
            new ChannelFact(ChannelFactGroup.Channel, "FieldAddress", channel.Url),
            new ChannelFact(ChannelFactGroup.Channel, "FieldType", ValueKey: KindKey(channel.MediaKind)),
            new ChannelFact(ChannelFactGroup.Channel, "FieldOrigin", ValueKey: OriginKey(channel.SourceOrigin)),
            new ChannelFact(ChannelFactGroup.Channel, "FieldAdded", Timestamp(channel.AddedAt, culture)),
            new ChannelFact(ChannelFactGroup.Channel, "FieldLastPlayed", Timestamp(channel.LastPlayedAt, culture)),
            new ChannelFact(ChannelFactGroup.Channel, "FieldLastOutcome", ValueKey: OutcomeKey(channel.LastPlayOutcome)),
            new ChannelFact(ChannelFactGroup.Channel, "Pinned", ValueKey: channel.Pinned ? "AboutValueYes" : "AboutValueNo"),
            new ChannelFact(ChannelFactGroup.Channel, "FieldCollections", Join(collectionNames)),

            new ChannelFact(ChannelFactGroup.Catalog, "FieldTopic", Clean(channel.Topic)),
            new ChannelFact(ChannelFactGroup.Catalog, "Category", Clean(channel.Category)),
            new ChannelFact(ChannelFactGroup.Catalog, "Language", Clean(channel.Language)),
            new ChannelFact(ChannelFactGroup.Catalog, "Country", Clean(channel.Country)),
            new ChannelFact(ChannelFactGroup.Catalog, "FieldHomepage", Clean(channel.Homepage)),
            new ChannelFact(ChannelFactGroup.Catalog, "FieldProtocol", Clean(channel.Protocol)),
            new ChannelFact(ChannelFactGroup.Catalog, "FieldFormat", Clean(channel.Format)),
            AdvertisedBitrate(channel.Bitrate, culture),
            new ChannelFact(ChannelFactGroup.Catalog, "FieldLive", ValueKey: LiveKey(channel.IsLive)),
            new ChannelFact(ChannelFactGroup.Catalog, "FieldAccess", ValueKey: AccessKey(channel.Access))
        ];
    }

    /// <summary>
    /// The measured half. <paramref name="measured"/> false collapses the group to one line saying so:
    /// a stream that could not be reached must not read as a stream that carries no video.
    /// </summary>
    public static IReadOnlyList<ChannelFact> DescribeTransmission(
        StreamTransmission? transmission,
        bool measured,
        IFormatProvider? formats = null)
    {
        if (!measured || transmission is null)
        {
            return [Status("AboutUnavailable")];
        }

        var culture = formats ?? CultureInfo.CurrentCulture;
        return
        [
            new ChannelFact(ChannelFactGroup.Stream, "FieldVideoCodec", Clean(transmission.VideoCodec)),
            new ChannelFact(ChannelFactGroup.Stream, "FieldResolution", Resolution(transmission, culture)),
            new ChannelFact(ChannelFactGroup.Stream, "FieldFrameRate", Fractional(transmission.FrameRate, culture), ValueFormatKey: "FrameRateValue"),
            new ChannelFact(ChannelFactGroup.Stream, "FieldAudioCodec", Clean(transmission.AudioCodec)),
            new ChannelFact(ChannelFactGroup.Stream, "FieldAudioChannels", Count(transmission.AudioChannels, culture)),
            new ChannelFact(ChannelFactGroup.Stream, "FieldSampleRate", Count(transmission.SampleRate, culture), ValueFormatKey: "SampleRateValue"),
            new ChannelFact(ChannelFactGroup.Stream, "FieldObservedBitrate", Fractional(transmission.ObservedKilobitsPerSecond, culture), ValueFormatKey: "BitrateValue")
        ];
    }

    /// <summary>
    /// The transmission group standing in for a reading: measuring, or measured and unreachable. It is
    /// labelled with the group's own name so it reads as a statement about the stream rather than an
    /// answer about one of its properties.
    /// </summary>
    public static ChannelFact Status(string valueKey) =>
        new(ChannelFactGroup.Stream, "AboutGroupStream", ValueKey: valueKey);

    /// <summary>
    /// Resolves one line's value. The single place the three value shapes are collapsed into a string,
    /// so the window and the clipboard can never disagree about what a line says.
    /// </summary>
    public static string ResolveValue(ChannelFact fact, Func<string, string> localize)
    {
        if (fact.ValueKey is not null)
        {
            return localize(fact.ValueKey);
        }

        if (fact.Text is null)
        {
            return string.Empty;
        }

        return fact.ValueFormatKey is null
            ? fact.Text
            : string.Format(CultureInfo.CurrentCulture, localize(fact.ValueFormatKey), fact.Text);
    }

    /// <summary>
    /// The clipboard text: one <c>label: value</c> line per fact, a blank line between groups. The
    /// lookup is a delegate so this stays clear of the app's resource dictionaries.
    /// </summary>
    public static string Render(IReadOnlyList<ChannelFact> facts, Func<string, string> localize)
    {
        var text = new StringBuilder();
        ChannelFactGroup? previous = null;
        foreach (var fact in facts)
        {
            if (previous is not null && fact.Group != previous)
            {
                text.AppendLine();
            }

            text.Append(localize(fact.LabelKey)).Append(": ").AppendLine(ResolveValue(fact, localize));
            previous = fact.Group;
        }

        return text.ToString();
    }

    private static string KindKey(MediaKind kind) => kind switch
    {
        MediaKind.Audio => "KindAudio",
        MediaKind.Video => "KindVideo",
        _ => "KindRtsp"
    };

    private static string OriginKey(SourceOrigin origin) => origin switch
    {
        SourceOrigin.Manual => "OriginManual",
        SourceOrigin.Imported => "OriginImported",
        _ => "OriginCatalog"
    };

    private static string OutcomeKey(PlayOutcome? outcome) => outcome switch
    {
        PlayOutcome.Ok => "StatusVerified",
        PlayOutcome.Fail => "StatusFailed",
        _ => "StatusNotPlayed"
    };

    // Null stays null rather than becoming a word: the catalog's live column is genuinely three-valued,
    // and calling an unstated stream "on demand" would invent a claim the maintainer never made.
    private static string? LiveKey(bool? isLive) => isLive switch
    {
        true => "LiveLabel",
        false => "OnDemandLabel",
        _ => null
    };

    private static string AccessKey(ChannelAccess access) =>
        access == ChannelAccess.GeoRestricted ? "RegionRestrictedLabel" : "AboutAccessOpen";

    // Mirrors the card's reading of the same column (SP-0018): a number becomes a rate in the shared
    // bitrate template, anything else is shown as the maintainer wrote it rather than dropped.
    private static ChannelFact AdvertisedBitrate(string? raw, IFormatProvider culture) =>
        StreamBitrate.TryParseKbps(raw, out var kbps)
            ? new ChannelFact(ChannelFactGroup.Catalog, "FieldBitrate", kbps.ToString("N0", culture), ValueFormatKey: "BitrateValue")
            : new ChannelFact(ChannelFactGroup.Catalog, "FieldBitrate", Clean(raw));

    private static string? Resolution(StreamTransmission transmission, IFormatProvider culture) =>
        transmission is { Width: > 0, Height: > 0 }
            ? string.Format(culture, "{0}×{1}", transmission.Width, transmission.Height)
            : null;

    private static string? Fractional(double? value, IFormatProvider culture) =>
        value is > 0 ? value.Value.ToString("0.##", culture) : null;

    private static string? Count(int value, IFormatProvider culture) =>
        value > 0 ? value.ToString("N0", culture) : null;

    private static string? Timestamp(DateTimeOffset? value, IFormatProvider culture) =>
        value?.ToLocalTime().ToString("g", culture);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Join(IReadOnlyList<string> names) =>
        names.Count == 0 ? null : string.Join(", ", names);
}
