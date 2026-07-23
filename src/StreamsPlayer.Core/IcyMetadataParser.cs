using System.Text;

namespace StreamsPlayer.Core;

/// <summary>
/// Extracts the current-track title from a decoded ICY/Shoutcast metadata block.
/// The value is untrusted broadcaster text, so it is sanitized and length-bounded
/// here before any caller surfaces it.
/// </summary>
public static class IcyMetadataParser
{
    /// <summary>Upper bound on the surfaced title length. An ICY block is already
    /// capped at 4080 bytes; this keeps a hostile or padded value from bloating the UI.</summary>
    public const int MaxTitleLength = 512;

    private const string TitleKey = "StreamTitle='";

    /// <summary>
    /// Returns the sanitized <c>StreamTitle</c> value, or <c>null</c> when the block
    /// carries no title, an empty title, or no <c>StreamTitle</c> field at all.
    /// Never throws for malformed input.
    /// </summary>
    public static string? ExtractStreamTitle(string metadataBlock)
    {
        if (string.IsNullOrWhiteSpace(metadataBlock))
        {
            return null;
        }

        var keyStart = metadataBlock.IndexOf(TitleKey, StringComparison.Ordinal);
        if (keyStart < 0)
        {
            return null;
        }

        var valueStart = keyStart + TitleKey.Length;
        // ICY delimits the value with "';"; tolerate a missing terminator (malformed block).
        var valueEnd = metadataBlock.IndexOf("';", valueStart, StringComparison.Ordinal);
        var rawValue = valueEnd < 0
            ? metadataBlock[valueStart..]
            : metadataBlock[valueStart..valueEnd];

        return Sanitize(rawValue);
    }

    private static string? Sanitize(string value)
    {
        var builder = new StringBuilder(Math.Min(value.Length, MaxTitleLength));
        var lastWasSpace = false;
        foreach (var ch in value)
        {
            // Drop control characters (newlines, NULs, tabs) and collapse whitespace runs.
            var normalized = char.IsControl(ch) ? ' ' : ch;
            if (normalized == ' ')
            {
                if (lastWasSpace || builder.Length == 0)
                {
                    continue;
                }

                lastWasSpace = true;
            }
            else
            {
                lastWasSpace = false;
            }

            builder.Append(normalized);
            if (builder.Length >= MaxTitleLength)
            {
                break;
            }
        }

        // Trim a trailing collapsed space.
        while (builder.Length > 0 && builder[^1] == ' ')
        {
            builder.Length--;
        }

        return builder.Length == 0 ? null : builder.ToString();
    }
}
