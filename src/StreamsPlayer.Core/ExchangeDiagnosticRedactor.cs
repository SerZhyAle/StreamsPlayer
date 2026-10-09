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

    [GeneratedRegex("(?i)(/b/)[^/\\s\"'?#]+", RegexOptions.CultureInvariant, 100)]
    private static partial Regex BroadcastUrlPaths();

    /// <summary>
    /// SP-0201 requirement 5: the one definition of "this address carries a broadcast capability" - a relay
    /// listen path (<c>/v2/b/&lt;broadcastId&gt;/..</c>) and a tunnel path (<c>/b/&lt;id&gt;/..</c>) both put the
    /// listening right in a <c>/b/</c> segment. The launch-argument gate and the shareable-report redaction
    /// read this instead of keeping a second pattern.
    /// </summary>
    public static bool ContainsBroadcastPath(string text)
    {
        try
        {
            return BroadcastUrlPaths().IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            // An input the pattern cannot be decided on is treated as carrying one: the callers withhold.
            return true;
        }
    }

    /// <summary>Masks the broadcast id of every <c>/b/&lt;id&gt;</c> segment in <paramref name="text"/>.</summary>
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
