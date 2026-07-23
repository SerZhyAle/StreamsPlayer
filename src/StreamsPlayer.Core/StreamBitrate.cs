using System.Globalization;

namespace StreamsPlayer.Core;

/// <summary>
/// Tolerant numeric interpretation of the optional, untrusted <c>bitrate</c> catalog claim (SP-0018).
/// The stored claim is a raw string; this is the single place that turns it into a comparable kbps value.
/// A claim that cannot be interpreted is treated as unknown — never as zero-that-passes.
/// </summary>
public static class StreamBitrate
{
    /// <summary>
    /// Parses a leading decimal number with an optional unit token into kilobits per second.
    /// Bare / <c>k</c> / <c>kb</c> / <c>kbps</c> are kbps; <c>m</c> / <c>mb</c> / <c>mbps</c> are ×1000.
    /// Returns false (and 0) for null, empty, or a value without a leading number.
    /// </summary>
    public static bool TryParseKbps(string? raw, out int kbps)
    {
        kbps = 0;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var text = raw.Trim();
        var end = 0;
        while (end < text.Length && (char.IsDigit(text[end]) || text[end] == '.'))
        {
            end++;
        }

        if (end == 0 ||
            !double.TryParse(text[..end], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        var unit = text[end..].Trim().ToLowerInvariant();
        var multiplier = unit switch
        {
            "" or "k" or "kb" or "kbps" or "kbit" or "kbit/s" => 1.0,
            "m" or "mb" or "mbps" or "mbit" or "mbit/s" => 1000.0,
            _ => 0.0
        };

        if (multiplier == 0.0)
        {
            return false;
        }

        kbps = (int)Math.Round(value * multiplier);
        return true;
    }

    /// <summary>
    /// True only when the claim parses and meets the threshold. An unparseable or missing claim is
    /// excluded under an active minimum (AC3).
    /// </summary>
    public static bool MeetsMinimum(string? raw, int minimumKbps) =>
        TryParseKbps(raw, out var kbps) && kbps >= minimumKbps;
}
