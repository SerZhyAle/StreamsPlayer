namespace StreamsPlayer.Core;

/// <summary>
/// SP-0008: names the desktop <c>.lnk</c> file a channel is pinned to. Lives in Core because the length
/// budget is a rule, not a UI detail: the shell COM object that writes the shortcut rejects a path over
/// MAX_PATH with <see cref="PathTooLongException"/>, and a catalog title long enough to cross that line
/// took the whole application down on 2026-08-10.
/// </summary>
public static class DesktopShortcutName
{
    /// <summary>Tells the user's own files apart from the ones this application put on the desktop.</summary>
    public const string Suffix = " - StreamsPlayer.lnk";

    /// <summary>Used when a title is empty or consists entirely of characters a file name cannot hold.</summary>
    private const string Fallback = "Stream";

    /// <summary>MAX_PATH (260) minus the terminating NUL the shell counts.</summary>
    private const int MaximumPathLength = 259;

    /// <summary>
    /// The full path of the shortcut for <paramref name="channelTitle"/> inside <paramref name="directory"/>,
    /// with everything Windows rejects in a file name replaced and the title cut to whatever the directory
    /// leaves. A directory so deep that not even <see cref="Fallback"/> fits still yields a path - one the
    /// shell will refuse, which is the caller's failure to report rather than this method's to guess around.
    /// </summary>
    public static string PathFor(string directory, string? channelTitle) => PathFor(directory, channelTitle, ordinal: 1);

    /// <summary>The most distinguishing suffixes tried before <see cref="FreePathFor"/> gives up.</summary>
    public const int MaximumOrdinal = 999;

    /// <summary>
    /// SP-0127: the first path for <paramref name="channelTitle"/> that <paramref name="isTaken"/> does not
    /// refuse - the plain name, then <c>Title (2)</c>, <c>Title (3)</c> and on - so two channels titled
    /// alike get two shortcuts instead of the second silently repointing the first. The caller decides what
    /// "taken" means: a file held by another channel is, the same channel's own earlier shortcut is not.
    /// <see langword="null"/> when every suffix up to <see cref="MaximumOrdinal"/> is taken.
    /// </summary>
    public static string? FreePathFor(string directory, string? channelTitle, Func<string, bool> isTaken)
    {
        ArgumentNullException.ThrowIfNull(isTaken);
        for (var ordinal = 1; ordinal <= MaximumOrdinal; ordinal++)
        {
            var path = PathFor(directory, channelTitle, ordinal);
            if (!isTaken(path))
            {
                return path;
            }
        }

        return null;
    }

    private static string PathFor(string directory, string? channelTitle, int ordinal)
    {
        // The distinguishing suffix is spent out of the title's budget, never past the path limit.
        var distinguisher = ordinal == 1 ? string.Empty : $" ({ordinal})";

        // Combining a one-character name is the only way to learn whether the directory carries its own
        // trailing separator without restating the platform's separator rules here.
        var prefixLength = Path.Combine(directory, "x").Length - 1;
        var budget = MaximumPathLength - prefixLength - Suffix.Length - distinguisher.Length;
        return Path.Combine(directory, Sanitize(channelTitle, budget) + distinguisher + Suffix);
    }

    private static string Sanitize(string? channelTitle, int budget)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var replaced = string.IsNullOrWhiteSpace(channelTitle)
            ? string.Empty
            : new string(channelTitle
                .Select(character => char.IsControl(character) || Array.IndexOf(invalid, character) >= 0 ? '_' : character)
                .ToArray()).Trim();

        if (replaced.Length > budget)
        {
            replaced = replaced[..Math.Max(budget, 0)];
        }

        // A trailing space or dot makes a file name Windows cannot open, so trim after truncating too.
        replaced = replaced.TrimEnd(' ', '.');
        return replaced.Length == 0 ? Fallback : replaced;
    }
}
