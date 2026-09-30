namespace StreamsPlayer.Core;

/// <summary>
/// SP-0179: the folders a captured file may land in, in the order CAPTURE-OUTPUT rules 9-11 try them - the
/// user's choice for the kind when there is one, then the kind's default folder, then the downloads folder
/// and nothing else. The first one that can be written wins; landing anywhere but the first is a fallback
/// the user is told about. Platform-neutral: the caller resolves the known folders.
/// </summary>
public static class CaptureFolderChain
{
    /// <summary>
    /// The chain for one kind. <paramref name="chosen"/> is the user's folder for it, or blank for none. A
    /// folder that appears twice - the user chose the default, or the downloads folder - is tried once.
    /// </summary>
    public static IReadOnlyList<string> For(string? chosen, string roleDefault, string downloads)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roleDefault);
        ArgumentException.ThrowIfNullOrWhiteSpace(downloads);
        var chain = new List<string>(3);
        foreach (var folder in new[] { chosen, roleDefault, downloads })
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                continue;
            }

            var trimmed = folder.Trim();
            if (!chain.Any(existing => SameFolder(existing, trimmed)))
            {
                chain.Add(trimmed);
            }
        }

        return chain;
    }

    /// <summary>Two spellings of one folder: case and a trailing separator do not make a second one on Windows.</summary>
    public static bool SameFolder(string left, string right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string folder) => folder.Trim().TrimEnd('\\', '/');
}
