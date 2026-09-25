using System.Globalization;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0071: one rendition of an adaptive stream, as the governor ranks it and the engines cap to it.
/// </summary>
/// <remarks>
/// Carries no URI on purpose: nothing in this feature ever fetches a variant. The player needs a rung
/// only to express a ceiling to its engine and to name the choice in the log, and a field that is never
/// read is a field that will one day be wrong.
/// </remarks>
public readonly record struct StreamQualityRung(int BandwidthBps, int Width, int Height)
{
    /// <summary>How a rung is spelled everywhere it is logged, so two log lines can be compared.</summary>
    public string Describe() =>
        string.Create(CultureInfo.InvariantCulture, $"{BandwidthBps / 1000}k/{Width}x{Height}");
}

/// <summary>
/// SP-0071: reads the quality ladder out of an HLS master playlist. A pure string function - no HTTP, no
/// URIs, no engine - so the whole contract is decidable in tests; the fetch is the App's business.
/// </summary>
/// <remarks>
/// <para>The result is <b>empty whenever a ceiling could not be trusted</b>, never partial. A ladder is
/// used to exclude renditions, so a half-read one produces a cap that does not cap - which is worse than
/// no cap at all, because the log would then claim a limit the stream never obeyed.</para>
/// <para>Read against the real 2026-08-08 playlist of the reported channel, which supplies the three
/// traps this parser exists to survive: its variants are listed out of bandwidth order, its
/// <c>CODECS</c> values contain commas inside quotes, and it carries three
/// <c>#EXT-X-I-FRAME-STREAM-INF</c> trick-play entries - one of them declaring a full 1024x576 at
/// 18803 bps, which would corrupt the ladder outright if counted as a rendition.</para>
/// </remarks>
public static class StreamQualityLadder
{
    private const string MasterTag = "#EXT-X-STREAM-INF:";
    private const string Header = "#EXTM3U";

    /// <summary>
    /// The rungs of <paramref name="playlistText"/>, ascending by bandwidth; empty when this is not a
    /// master playlist, when any variant is under-declared, or when fewer than two steppable rungs remain
    /// (see <see cref="Steppable"/>).
    /// </summary>
    public static IReadOnlyList<StreamQualityRung> Read(string playlistText)
    {
        var lines = playlistText.Split('\n');
        var index = SkipToHeader(lines);
        if (index < 0)
        {
            return [];
        }

        var rungs = new List<StreamQualityRung>();
        for (var i = index; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            // StartsWith on the full tag is what separates a rendition from the trick-play entries:
            // "#EXT-X-I-FRAME-STREAM-INF:" does not start with "#EXT-X-STREAM-INF:".
            if (!line.StartsWith(MasterTag, StringComparison.Ordinal))
            {
                continue;
            }

            if (!HasUriLine(lines, i))
            {
                continue; // a tag with nothing to play is not a rendition
            }

            if (ReadRung(line[MasterTag.Length..]) is not { } rung)
            {
                return []; // an under-declared variant voids the ladder (see the remarks)
            }

            rungs.Add(rung);
        }

        var ladder = Steppable(rungs);
        return ladder.Count >= 2 ? ladder : [];
    }

    /// <summary>
    /// SP-0130: the rungs a ceiling can actually tell apart, ascending by bandwidth.
    /// <para>The engines cap by <em>resolution</em> and then play the richest rendition that fits: LibVLC
    /// takes width and height, Flyleaf height alone. A rung is therefore steppable only when no richer
    /// rendition fits under its own height - otherwise a ceiling set to it plays that richer one instead,
    /// and a "step" to it re-opens the stream (seconds of black) onto the very rendition it left. Measured
    /// by height, the one dimension both engines honour, so the rule holds whichever engine is selected;
    /// what remains is strictly ascending in height as well as in bandwidth.</para>
    /// <para>Two renditions at the same rate are one rung as well, and the first one listed is kept - the
    /// order the source offers them in.</para>
    /// </summary>
    private static List<StreamQualityRung> Steppable(List<StreamQualityRung> rungs)
    {
        var byRate = rungs
            .GroupBy(rung => rung.BandwidthBps)
            .Select(group => group.First())
            .OrderByDescending(rung => rung.BandwidthBps);

        var kept = new List<StreamQualityRung>();
        var lowestKeptHeight = int.MaxValue;
        foreach (var rung in byRate)
        {
            // Richest first: a rung survives only when it is shorter than every richer one kept so far,
            // which is exactly "no richer rendition fits under this rung's cap".
            if (rung.Height < lowestKeptHeight)
            {
                kept.Add(rung);
                lowestKeptHeight = rung.Height;
            }
        }

        kept.Reverse();
        return kept;
    }

    /// <summary>The index just past <c>#EXTM3U</c>, or -1 when this text is not an HLS playlist.</summary>
    private static int SkipToHeader(string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            // The byte-order mark is stripped explicitly: it is not whitespace to Trim, so a BOM ahead
            // of #EXTM3U would make a perfectly good master playlist read as "not a playlist".
            var line = lines[i].Trim().TrimStart('\uFEFF');
            if (line.Length == 0)
            {
                continue;
            }

            return line.StartsWith(Header, StringComparison.Ordinal) ? i + 1 : -1;
        }

        return -1;
    }

    /// <summary>True when a playable URI follows the tag at <paramref name="tagIndex"/>.</summary>
    private static bool HasUriLine(string[] lines, int tagIndex)
    {
        for (var i = tagIndex + 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            return !line.StartsWith('#');
        }

        return false;
    }

    /// <summary>
    /// One variant's attributes, or null when it does not declare both a bandwidth and a resolution.
    /// <para>Both are required because the ceiling is expressed to the engines as a resolution: a
    /// rendition with no <c>RESOLUTION</c> cannot be excluded by one, so a ladder containing it would
    /// describe a limit that does not hold.</para>
    /// </summary>
    private static StreamQualityRung? ReadRung(string attributeList)
    {
        int? bandwidth = null;
        int width = 0, height = 0;
        foreach (var (name, value) in SplitAttributes(attributeList))
        {
            switch (name)
            {
                case "BANDWIDTH":
                    if (!TryReadPositive(value, out var bps))
                    {
                        return null;
                    }

                    bandwidth = bps;
                    break;

                case "RESOLUTION":
                    if (!TryReadResolution(value, out width, out height))
                    {
                        return null;
                    }

                    break;
            }
        }

        return bandwidth is { } declared && width > 0 && height > 0
            ? new StreamQualityRung(declared, width, height)
            : null;
    }

    /// <summary>
    /// Splits an attribute list on commas that are outside double quotes. The naive split is wrong on
    /// every real playlist: <c>CODECS="mp4a.40.2,avc1.4D4028"</c> carries a comma of its own.
    /// </summary>
    private static IEnumerable<(string Name, string Value)> SplitAttributes(string attributeList)
    {
        var quoted = false;
        var start = 0;
        for (var i = 0; i <= attributeList.Length; i++)
        {
            if (i < attributeList.Length)
            {
                if (attributeList[i] == '"')
                {
                    quoted = !quoted;
                    continue;
                }

                if (attributeList[i] != ',' || quoted)
                {
                    continue;
                }
            }

            var pair = attributeList[start..i];
            start = i + 1;
            var equals = pair.IndexOf('=');
            if (equals > 0)
            {
                yield return (pair[..equals].Trim().ToUpperInvariant(), pair[(equals + 1)..].Trim().Trim('"'));
            }
        }
    }

    private static bool TryReadResolution(string value, out int width, out int height)
    {
        height = 0;
        width = 0;
        var separator = value.IndexOf('x', StringComparison.OrdinalIgnoreCase);
        return separator > 0 &&
            TryReadPositive(value[..separator], out width) &&
            TryReadPositive(value[(separator + 1)..], out height);
    }

    private static bool TryReadPositive(string value, out int parsed) =>
        int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out parsed) && parsed > 0;
}
