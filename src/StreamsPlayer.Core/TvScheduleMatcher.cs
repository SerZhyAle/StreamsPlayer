using System.Globalization;
using System.Text;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0075: binds catalog channels to schedule channels.
///
/// <para>The catalog carries no schedule identifier, so the automatic path is the channel name - and only
/// an exact match of the normalized name. A wrong programme under a channel's name reads as a bug of the
/// application, not of the source (strategic risk), so anything uncertain binds nothing: a name the
/// schedule publishes for more than one of its channels is ambiguous and dropped. The user's own binding
/// always wins, including an explicit "no schedule".</para>
/// </summary>
public static class TvScheduleMatcher
{
    // Picture-quality and format words carry no identity: "Channel One HD" and "Channel One" are the same
    // station. "TV" is deliberately not here - it distinguishes real stations ("TV5" / "5").
    private static readonly HashSet<string> NoiseTokens = new(StringComparer.Ordinal)
    {
        "hd", "fhd", "uhd", "sd", "hq", "4k", "8k", "hevc", "h264", "h265",
        "1080p", "1080i", "720p", "576p", "576i", "480p", "360p", "240p"
    };

    /// <summary>
    /// Lower-case, diacritics removed, bracketed remarks and quality words dropped, everything that is
    /// not a letter or digit removed. Null when nothing identifying is left.
    /// </summary>
    public static string? NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var withoutRemarks = StripBracketed(name);
        var decomposed = withoutRemarks.Normalize(NormalizationForm.FormKD);
        var tokens = new List<string>();
        var current = new StringBuilder();
        foreach (var character in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.EnclosingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                current.Append(char.ToLowerInvariant(character));
                continue;
            }

            Flush(current, tokens);
        }

        Flush(current, tokens);
        while (tokens.Count > 1 && NoiseTokens.Contains(tokens[^1]))
        {
            tokens.RemoveAt(tokens.Count - 1);
        }

        var result = string.Concat(tokens);
        return result.Length == 0 ? null : result;
    }

    /// <summary>
    /// Normalized name -> schedule channel id, with every name that two schedule channels share removed.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildNameIndex(IEnumerable<TvScheduleChannel> channels)
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);
        foreach (var channel in channels)
        {
            foreach (var name in channel.Names)
            {
                if (NormalizeName(name) is not { } key || ambiguous.Contains(key))
                {
                    continue;
                }

                if (index.TryGetValue(key, out var existing))
                {
                    if (!string.Equals(existing, channel.Id, StringComparison.Ordinal))
                    {
                        index.Remove(key);
                        ambiguous.Add(key);
                    }

                    continue;
                }

                index[key] = channel.Id;
            }
        }

        return index;
    }

    /// <summary>Only a picture channel gets a television schedule; radio never auto-matches.</summary>
    public static bool IsEligible(StreamChannel channel) => channel.MediaKind != MediaKind.Audio;

    /// <summary>
    /// The schedule channel id for a catalog channel: the user's binding when there is one (null meaning
    /// "no schedule"), otherwise the unambiguous name match, otherwise null.
    /// </summary>
    public static string? Resolve(
        StreamChannel channel,
        IReadOnlyDictionary<string, TvScheduleBinding> bindingsByUrl,
        IReadOnlyDictionary<string, string> nameIndex)
    {
        if (bindingsByUrl.TryGetValue(CatalogUrlIdentity.Normalize(channel.Url), out var binding))
        {
            return binding.ScheduleChannelId;
        }

        if (!IsEligible(channel) || NormalizeName(channel.Title) is not { } key)
        {
            return null;
        }

        return nameIndex.TryGetValue(key, out var id) ? id : null;
    }

    private static void Flush(StringBuilder current, List<string> tokens)
    {
        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
            current.Clear();
        }
    }

    private static string StripBracketed(string name)
    {
        var builder = new StringBuilder(name.Length);
        var depth = 0;
        foreach (var character in name)
        {
            switch (character)
            {
                case '(' or '[' or '{':
                    depth++;
                    builder.Append(' ');
                    break;
                case ')' or ']' or '}' when depth > 0:
                    depth--;
                    break;
                default:
                    if (depth == 0)
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        return builder.ToString();
    }
}
