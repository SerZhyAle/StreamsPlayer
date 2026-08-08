using System.Globalization;

namespace StreamsPlayer.Core;

/// <summary>How a piece of text offered to the paste flow was understood.</summary>
public enum ChannelShareStatus
{
    /// <summary>A launchable address was recognized and is carried by the read.</summary>
    Ok,

    /// <summary>Not a StreamsPlayer share text and not a bare launchable address either.</summary>
    NotShareText,

    /// <summary>Unmistakably a StreamsPlayer share text, written by a version this build cannot honour.</summary>
    UnsupportedVersion,

    /// <summary>A StreamsPlayer share text whose payload is not an address this application can launch.</summary>
    InvalidAddress
}

/// <summary>
/// The outcome of reading a share text. <paramref name="Url"/> is <see cref="string.Empty"/> for every
/// status other than <see cref="ChannelShareStatus.Ok"/>, so a refusal never needs a null check.
/// </summary>
public sealed record ChannelShareRead(ChannelShareStatus Status, string Url);

/// <summary>The channel a shared address resolves to in the local list, and whether the user hid it.</summary>
public sealed record ChannelShareMatch(StreamChannel? Existing, bool Hidden);

/// <summary>
/// SP-0058: the one-line, human-legible text that hands a single channel to another person through an
/// ordinary chat message - <c>SPCH1 https://example.test/live</c>.
/// </summary>
/// <remarks>
/// The payload is the address and nothing else: title, media kind and every catalog field are derived
/// locally by the receiving copy. The format carries its version from the first release so a text written
/// by a later build is refused with an explanation instead of being mis-imported. Nothing here contacts
/// the network - both sides of a share are offline, which is what keeps the explicit-refresh rule intact.
/// </remarks>
public static class ChannelShareText
{
    /// <summary>The fixed prefix that identifies the text as a StreamsPlayer channel.</summary>
    public const string Marker = "SPCH";

    /// <summary>The format version this build emits and is the only one it accepts.</summary>
    public const int Version = 1;

    /// <summary>
    /// Renders the share text for one address. The result is a single line by construction, so a chat
    /// client has nothing to split on.
    /// </summary>
    public static string Format(string url) => $"{Marker}{Version} {url.Trim()}";

    /// <summary>
    /// Reads text taken from the clipboard. A bare launchable address is accepted too, because a sender
    /// may simply forward the link rather than use the copy action.
    /// </summary>
    public static ChannelShareRead Read(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new ChannelShareRead(ChannelShareStatus.NotShareText, string.Empty);
        }

        // Only the first non-empty line is considered: a chat client that appends a signature, or a
        // sender who added a word of their own on the next line, must not defeat the read.
        var line = FirstNonEmptyLine(text);
        if (line.Length == 0)
        {
            return new ChannelShareRead(ChannelShareStatus.NotShareText, string.Empty);
        }

        if (TryReadMarked(line, out var marked))
        {
            return marked;
        }

        return StreamMediaKindClassifier.IsLaunchable(line)
            ? new ChannelShareRead(ChannelShareStatus.Ok, line)
            : new ChannelShareRead(ChannelShareStatus.NotShareText, string.Empty);
    }

    /// <summary>
    /// Finds the channel a shared address already resolves to, and reports whether the user has hidden it.
    /// </summary>
    /// <remarks>
    /// Deliberately the <b>normalized</b> identity of <see cref="CatalogUrlIdentity"/>, while the add and
    /// M3U import flows stay on exact ordinal equality. A shared address arrives retyped or forwarded, so
    /// host case and an explicit default port must resolve to the channel the user already has; the two
    /// rules disagree in exactly that band and nowhere else, because normalization never folds
    /// <c>http</c> into <c>https</c> and never touches the path or query.
    /// </remarks>
    public static ChannelShareMatch FindExisting(
        IEnumerable<StreamChannel> channels,
        IEnumerable<string> hiddenUrls,
        string url)
    {
        var existing = channels.FirstOrDefault(channel => CatalogUrlIdentity.SameIdentity(channel.Url, url));
        return existing is null
            ? new ChannelShareMatch(null, false)
            : new ChannelShareMatch(existing, CatalogUrlIdentity.IsHidden(hiddenUrls, existing.Url));
    }

    private static string FirstNonEmptyLine(string text)
    {
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', '\r'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }

        return string.Empty;
    }

    private static bool TryReadMarked(string line, out ChannelShareRead read)
    {
        read = new ChannelShareRead(ChannelShareStatus.NotShareText, string.Empty);
        if (!line.StartsWith(Marker, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var digits = 0;
        while (Marker.Length + digits < line.Length && char.IsAsciiDigit(line[Marker.Length + digits]))
        {
            digits++;
        }

        if (digits == 0)
        {
            return false;
        }

        // An unparsable run is a version number too large for this build to name, which is the same
        // answer to the user as a version it can name and does not support.
        var declared = line.Substring(Marker.Length, digits);
        if (!int.TryParse(declared, NumberStyles.None, CultureInfo.InvariantCulture, out var version) ||
            version != Version)
        {
            read = new ChannelShareRead(ChannelShareStatus.UnsupportedVersion, string.Empty);
            return true;
        }

        var address = line[(Marker.Length + digits)..].Trim();
        read = StreamMediaKindClassifier.IsLaunchable(address)
            ? new ChannelShareRead(ChannelShareStatus.Ok, address)
            : new ChannelShareRead(ChannelShareStatus.InvalidAddress, string.Empty);
        return true;
    }
}
