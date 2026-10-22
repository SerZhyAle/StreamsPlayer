using System.Text.RegularExpressions;

namespace StreamsPlayer.Core;

public static partial class ExchangeDiagnosticRedactor
{
    // SP-0226 / `DEVICE-EXCHANGE` item DQ: a box, a key ring or a key never appears in a log line or a bundle. These are
    // the members that carry one (a device key, a box and its parts, the keys of a broadcast); a line that names one is
    // masked whole, because masking a single token of a JSON dump would leave the rest of the box beside it.
    // Declared first: the marker list below spreads it, and static fields initialize in the order they are written.
    private static readonly string[] SealedMarkers =
    ["e2eKey", "e2ePrivateKey", "e2eKem", "privateKey", "relayKey", "tunnelPin", "sealedAccess", "\"sealed\"", "\"seal\"", "seal=", "seal:",
     "\"fromKey\"", "\"wk\"", "\"enc\"", "groupKey", "epochKey", "contentKey", "x-fmsx-sealed"];

    private static readonly string[] PrivateMarkers =
    ["deviceToken", "pairingCode", "login", "account", "exchangeAddress", "publicEndpoint", "Fingerprint", "password", "invitation", "SHA256:", "broadcastId", "/b/", "fmsx:", "FMSEXCH", ".fmsexch", "FMSBCAST", "fmsbcast", "live-audio", "live.ts", "sourceId",
     .. SealedMarkers];
    // These fields cover accidental envelope/exception dumps without storing a copy of the account in diagnostics.
    // "password" also reaches oldPassword, newPassword and invitationPassword, which end in it case-insensitively.
    [GeneratedRegex("(?i)(\"?(?:deviceToken|pairingCode|login|account|exchangeLogin|exchangeAddress|publicEndpoint|certFingerprint|oldFingerprint|newFingerprint|password|invitation|broadcastId)\"?\\s*[:=]\\s*)(?:\"(?:\\\\.|[^\"\\\\])*\"|[^\\s|,;}]+)", RegexOptions.CultureInvariant, 100)]
    private static partial Regex PrivateFields();

    [GeneratedRegex("(?<![A-Za-z0-9_])(\"?(?:x|fp|n|u)\"?\\s*[:=]\\s*)(?:\"(?:\\\\.|[^\"\\\\])*\"|[^\\s|,;}]+)", RegexOptions.CultureInvariant, 100)]
    private static partial Regex ConnectionFields();

    [GeneratedRegex("SHA256:[A-Za-z0-9+/]{43}", RegexOptions.CultureInvariant, 100)]
    private static partial Regex Fingerprints();

    /// <summary>
    /// Loose pattern for the free text of a log line: any <c>/b/&lt;id&gt;</c> segment, wherever it sits.
    /// Over-masking an ordinary address there costs one path segment of a diagnostic and is harmless. It is
    /// not a test of what an address is - <see cref="BroadcastCapabilityAddress.CarriesCapability"/> is.
    /// </summary>
    [GeneratedRegex("(?i)(/b/)[^/\\s\"'?#]+", RegexOptions.CultureInvariant, 100)]
    private static partial Regex BroadcastUrlPaths();

    /// <summary>
    /// The anchored shapes of the contract (DEVICE-EXCHANGE): a tunnel <c>fmsx://&lt;endpoint&gt;/b/&lt;id&gt;</c>
    /// and a relay listen path <c>&lt;scheme&gt;://&lt;endpoint&gt;/v2/b/&lt;id&gt;</c>, the segment directly after the
    /// authority. An ordinary stream address with <c>/b/</c> deeper in its path does not match.
    /// </summary>
    [GeneratedRegex("(?i)((?:fmsx://[^/\\s\"'?#]*/b/)|(?:[a-z][a-z0-9+.\\-]*://[^/\\s\"'?#]*/v2/b/))[^/\\s\"'?#]+", RegexOptions.CultureInvariant, 100)]
    private static partial Regex CapabilityUrlPaths();

    /// <summary>
    /// SP-0217: the description's own addresses - a LAN audio path, a tokened video path - masked whole,
    /// because a URL of this product's broadcast shape names the machine that serves it. Over-masking an
    /// ordinary address that happens to end in one of these paths costs one token of a diagnostic.
    /// </summary>
    [GeneratedRegex(@"(?i)\S*(?:live-audio|live\.ts)\S*", RegexOptions.CultureInvariant, 100)]
    private static partial Regex DescriptorAddresses();

    /// <summary>
    /// Masks the broadcast id of every <c>/b/&lt;id&gt;</c> segment in <paramref name="text"/>, wherever it
    /// sits. For the free text of a log line, and for an address already known to be a capability address.
    /// </summary>
    public static string RedactBroadcastPaths(string text)
    {
        try
        {
            return BroadcastUrlPaths().Replace(text, "$1[REDACTED]");
        }
        catch (RegexMatchTimeoutException)
        {
            return "[REDACTED]";
        }
    }

    /// <summary>
    /// SP-0201 requirement 5: masks the broadcast id only where <paramref name="text"/> holds an address of
    /// the contract's shape (a tunnel or a relay listen path), so <c>http://host/radio/b/live.mp3</c> is left
    /// as it was. For a stored channel address of unknown shape that does not parse as a URI.
    /// </summary>
    public static string RedactCapabilityAddresses(string text)
    {
        try
        {
            return CapabilityUrlPaths().Replace(text, "$1[REDACTED]");
        }
        catch (RegexMatchTimeoutException)
        {
            return "[REDACTED]";
        }
    }

    public static string Redact(string text)
    {
        if (!PrivateMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase))
            && !ContainsConnectionFields(text))
        {
            return text;
        }

        return string.Join('\n', text.Split('\n').Select(RedactLine));
    }

    private static string RedactLine(string text)
    {
        // A malformed carrier or a quoted path can contain spaces; masking only one token would leak its tail.
        if (text.Contains("FMSEXCH", StringComparison.OrdinalIgnoreCase) || text.Contains(".fmsexch", StringComparison.OrdinalIgnoreCase)
            || text.Contains("FMSBCAST", StringComparison.OrdinalIgnoreCase) || text.Contains("fmsbcast", StringComparison.OrdinalIgnoreCase)
            || SealedMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            return "[REDACTED]";
        try
        {
            var redacted = PrivateFields().Replace(text, "$1[REDACTED]");
            redacted = ConnectionFields().Replace(redacted, "$1[REDACTED]");
            redacted = Fingerprints().Replace(redacted, "[REDACTED]");
            redacted = DescriptorAddresses().Replace(redacted, "[REDACTED]");
            return RedactBroadcastPaths(redacted);
        }
        catch (RegexMatchTimeoutException)
        {
            return "[REDACTED]";
        }
    }

    private static bool ContainsConnectionFields(string text)
    {
        try
        {
            return ConnectionFields().IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
    }
}
