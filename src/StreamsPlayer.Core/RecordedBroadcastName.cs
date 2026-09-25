using System.Globalization;
using System.IO;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0101: names the recorded broadcast media file written to disk. Lives in Core so file naming,
/// hostile titles sanitization, and timestamp formatting are fully platform-neutral and tested.
/// </summary>
public static class RecordedBroadcastName
{
    public const string DefaultVideoExtension = ".mp4";

    /// <summary>Time part of the name; sorts chronologically inside one channel and carries no separator
    /// that Windows or a shell would treat specially.</summary>
    public const string TimestampFormat = "yyyyMMdd-HHmmss";

    /// <summary>
    /// Longest title kept. Well under MAX_PATH once a deep folder, the stamp and the extension
    /// are added, and long enough that no ordinary channel name is cut.
    /// </summary>
    private const int MaxTitleLength = 80;

    /// <summary>Used when a title is empty or consists entirely of characters a file name cannot hold.</summary>
    private const string Fallback = "Stream";

    /// <summary>
    /// <c><paramref name="channelTitle"/>_yyyyMMdd-HHmmss<paramref name="extension"/></c>, with everything
    /// Windows rejects in a file name replaced.
    /// </summary>
    public static string For(string? channelTitle, DateTimeOffset recordedAt, string? extension = null)
    {
        var ext = NormalizeExtension(extension);
        return $"{Sanitize(channelTitle)}_{recordedAt.ToString(TimestampFormat, CultureInfo.InvariantCulture)}{ext}";
    }

    private static string NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return DefaultVideoExtension;
        }

        var trimmed = extension.Trim();
        return trimmed.StartsWith('.') ? trimmed : $".{trimmed}";
    }

    public static string Sanitize(string? channelTitle)
    {
        if (string.IsNullOrWhiteSpace(channelTitle))
        {
            return Fallback;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(channelTitle.Length);
        var lastWasSpace = false;
        foreach (var character in channelTitle)
        {
            // Control characters and the invalid set both collapse into a single space rather than
            // vanishing: "News/Sport" must not read as "NewsSport".
            var replaced = char.IsControl(character) || Array.IndexOf(invalid, character) >= 0
                ? ' '
                : character;
            if (replaced == ' ')
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

            builder.Append(replaced);
        }

        // A trailing space or dot makes a file name Windows cannot open, so trim after truncating too.
        var trimmed = builder.ToString().TrimEnd(' ', '.');
        if (trimmed.Length > MaxTitleLength)
        {
            trimmed = trimmed[..MaxTitleLength].TrimEnd(' ', '.');
        }

        return trimmed.Length == 0 ? Fallback : trimmed;
    }
}
