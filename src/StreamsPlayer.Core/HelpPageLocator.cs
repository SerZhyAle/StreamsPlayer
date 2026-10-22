namespace StreamsPlayer.Core;

/// <summary>
/// SP-0230: turns a <see cref="HelpTarget"/> and the application's language into the page file to open, with the
/// fallbacks of CONTEXT-HELP rule 13 - a missing locale opens the source language's page, a missing page opens the
/// index, and nothing ever opens a dead end.
/// </summary>
public static class HelpPageLocator
{
    /// <summary>The folder, beside the executable and in the published site, that holds the pages.</summary>
    public const string FolderName = "help";

    /// <summary>
    /// The page to open as a path relative to the help folder, using forward slashes and carrying the anchor as a
    /// fragment, or null when not even an index exists (a build that shipped without its help).
    /// </summary>
    /// <param name="exists">Whether a path relative to the help folder is a file; injected so no disk is needed to test it.</param>
    public static string? Locate(HelpMap map, HelpTarget? target, string locale, Func<string, bool> exists)
    {
        string[] locales = string.Equals(locale, map.SourceLocale, StringComparison.OrdinalIgnoreCase)
            ? [map.SourceLocale]
            : [locale.ToLowerInvariant(), map.SourceLocale];

        if (target is not null)
        {
            foreach (var candidate in locales)
            {
                var page = PagePath(map, candidate, target.HelpId);
                if (exists(page))
                {
                    return string.IsNullOrEmpty(target.Anchor) ? page : $"{page}#{target.Anchor}";
                }
            }
        }

        foreach (var candidate in locales)
        {
            var index = PagePath(map, candidate, map.Index);
            if (exists(index))
            {
                return index;
            }
        }

        return null;
    }

    /// <summary>
    /// The address handed to the browser for a located page: a <c>file:</c> address with each path segment
    /// percent-encoded (a profile or install folder may hold spaces and any script), the located page's
    /// <c>#anchor</c> kept, and an optional <c>?query</c>. Built by hand rather than through <c>Uri</c>, which would
    /// also accept a channel address nobody validated (the launch helper is the one place those are parsed).
    /// </summary>
    public static string FileAddress(string helpFolder, string relative, string? query)
    {
        var hash = relative.IndexOf('#', StringComparison.Ordinal);
        var file = hash < 0 ? relative : relative[..hash];
        var fragment = hash < 0 ? string.Empty : relative[(hash + 1)..];
        var path = helpFolder.Replace('\\', '/').TrimEnd('/') + "/" + file;

        string prefix;
        IEnumerable<string> segments;
        if (path.StartsWith("//", StringComparison.Ordinal))
        {
            var parts = path[2..].Split('/', StringSplitOptions.RemoveEmptyEntries);
            prefix = "file://" + parts[0] + "/";
            segments = parts.Skip(1);
        }
        else
        {
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var drive = parts.Length > 0 && parts[0].Length == 2 && parts[0][1] == ':';
            prefix = drive ? "file:///" + parts[0] + "/" : "file:///";
            segments = drive ? parts.Skip(1) : parts;
        }

        return prefix + string.Join('/', segments.Select(Uri.EscapeDataString))
            + (string.IsNullOrEmpty(query) ? string.Empty : "?" + query)
            + (fragment.Length == 0 ? string.Empty : "#" + fragment);
    }

    /// <summary>The path of one page of one locale, relative to the help folder.</summary>
    public static string PagePath(HelpMap map, string locale, string helpId) => $"{locale.ToLowerInvariant()}/{helpId}.html";
}
