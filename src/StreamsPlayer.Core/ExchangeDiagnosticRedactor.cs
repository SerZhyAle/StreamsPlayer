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
            return BroadcastUrlPaths().Replace(redacted, "$1[REDACTED]");
        }
        catch (RegexMatchTimeoutException)
        {
            return "[REDACTED]";
        }
    }
}
