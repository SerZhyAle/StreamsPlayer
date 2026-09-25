namespace StreamsPlayer.Core;

/// <summary>
/// Outcome of analysing an M3U/M3U8 body for import. <see cref="Status"/> distinguishes a normal list from
/// an HLS media manifest (import zero) and an empty list. Counts are disjoint per source line:
/// New = launchable and not already stored; Duplicate = launchable but the exact URL is already stored;
/// Invalid = a non-comment line that is not a launchable http/https/rtsp URL; Skipped = a launchable URL
/// repeated within the same file.
/// </summary>
public sealed record M3uImportPreview(
    M3uImportStatus Status,
    IReadOnlyList<CatalogEntry> NewEntries,
    int NewCount,
    int DuplicateCount,
    int InvalidCount,
    int SkippedCount);

public enum M3uImportStatus
{
    Ok,
    HlsManifest,
    Empty
}

public static class M3uPlaylistParser
{
    /// <summary>Launchable, in-file-deduplicated channels from an M3U body, ignoring what is already stored.</summary>
    public static IReadOnlyList<CatalogEntry> Parse(string text) =>
        Analyze(text, new HashSet<string>(StringComparer.Ordinal)).NewEntries;

    /// <summary>
    /// Categorise an M3U body against the URLs already stored (exact ordinal match, the authoritative
    /// de-duplication key). Never mutates state; the caller applies <see cref="M3uImportPreview.NewEntries"/>.
    /// </summary>
    public static M3uImportPreview Analyze(string text, ISet<string> existingUrls)
    {
        if (text.Contains("#EXT-X-", StringComparison.OrdinalIgnoreCase))
        {
            return new M3uImportPreview(M3uImportStatus.HlsManifest, [], 0, 0, 0, 0);
        }

        var newEntries = new List<CatalogEntry>();
        var seenInFile = new HashSet<string>(StringComparer.Ordinal);
        var duplicate = 0;
        var invalid = 0;
        var skipped = 0;
        var candidateLines = 0;
        string? nextTitle = null;

        foreach (var originalLine in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = originalLine.Trim();
            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                nextTitle = TitleOf(line);
                continue;
            }

            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            candidateLines++;
            if (!StreamMediaKindClassifier.IsLaunchable(line))
            {
                invalid++;
                nextTitle = null;
                continue;
            }

            if (!seenInFile.Add(line))
            {
                skipped++;
                nextTitle = null;
                continue;
            }

            if (existingUrls.Contains(line))
            {
                duplicate++;
                nextTitle = null;
                continue;
            }

            var title = string.IsNullOrWhiteSpace(nextTitle) ? LaunchableAddress.HostOf(line) : nextTitle;
            newEntries.Add(new CatalogEntry(
                title,
                line,
                StreamMediaKindClassifier.Classify(line),
                null,
                null,
                null,
                null,
                null,
                null));
            nextTitle = null;
        }

        var status = candidateLines == 0 ? M3uImportStatus.Empty : M3uImportStatus.Ok;
        return new M3uImportPreview(status, newEntries, newEntries.Count, duplicate, invalid, skipped);
    }

    /// <summary>
    /// The display title of an <c>#EXTINF</c> line: whatever follows the first comma outside a quoted attribute
    /// value (SP-0126), so <c>group-title="News, Sport",BBC</c> is titled <c>BBC</c>. Null when there is none.
    /// </summary>
    private static string? TitleOf(string extinfLine)
    {
        var quoted = false;
        for (var i = 0; i < extinfLine.Length; i++)
        {
            switch (extinfLine[i])
            {
                case '"':
                    quoted = !quoted;
                    break;
                case ',' when !quoted:
                    return extinfLine[(i + 1)..].Trim();
            }
        }

        return null;
    }
}
