using System.Text.RegularExpressions;

namespace StreamsPlayer.Core;

public static partial class ExchangeDiagnosticRedactor
{
    private static readonly string[] PrivateMarkers =
    ["deviceToken", "pairingCode", "login", "account", "exchangeAddress", "publicEndpoint", "Fingerprint", "password", "SHA256:", "broadcastId", "/b/", "fmsx:"];
    // These fields cover accidental envelope/exception dumps without storing a copy of the account in diagnostics.
    [GeneratedRegex("(?i)(\"?(?:deviceToken|pairingCode|login|account|exchangeLogin|exchangeAddress|publicEndpoint|certFingerprint|oldFingerprint|newFingerprint|password|broadcastId)\"?\\s*[:=]\\s*)(?:\"(?:\\\\.|[^\"\\\\])*\"|[^\\s|,;}]+)", RegexOptions.CultureInvariant, 100)]
    private static partial Regex PrivateFields();

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
        if (!PrivateMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return text;
        }

        return string.Join('\n', text.Split('\n').Select(RedactLine));
    }

    private static string RedactLine(string text)
    {
        try
        {
            var redacted = PrivateFields().Replace(text, "$1[REDACTED]");
            redacted = Fingerprints().Replace(redacted, "[REDACTED]");
            return RedactBroadcastPaths(redacted);
        }
        catch (RegexMatchTimeoutException)
        {
            return "[REDACTED]";
        }
    }
}
