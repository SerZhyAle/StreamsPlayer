using System.Globalization;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0038: names the picture file a captured frame is written to. Lives in Core so the rule - and the
/// hostile titles the catalog is full of - are tested without a player window.
/// </summary>
public static class CapturedFrameName
{
    public const string Extension = ".jpg";

    /// <summary>Time part of the name; sorts chronologically inside one channel and carries no separator
    /// that Windows or a shell would treat specially.</summary>
    public const string TimestampFormat = "yyyyMMdd-HHmmss";

    /// <summary>
    /// Longest title kept. Well under MAX_PATH once a deep frames folder, the stamp and the extension
    /// are added, and long enough that no ordinary channel name is cut.
    /// </summary>
    private const int MaxTitleLength = 80;

    /// <summary>Used when a title is empty or consists entirely of characters a file name cannot hold.</summary>
    private const string Fallback = "Stream";

    /// <summary>
    /// <c><paramref name="channelTitle"/>_yyyyMMdd-HHmmss.jpg</c>, with everything Windows rejects in a
    /// file name replaced. The caller supplies the capture time so the name is reproducible in tests.
    /// </summary>
    public static string For(string? channelTitle, DateTimeOffset capturedAt) =>
        $"{Sanitize(channelTitle)}_{capturedAt.ToString(TimestampFormat, CultureInfo.InvariantCulture)}{Extension}";

    private static string Sanitize(string? channelTitle)
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
