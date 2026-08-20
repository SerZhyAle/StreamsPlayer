using System.Text;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0087: what a channel shows when the stream bank gave it no icon. 13 910 of the 19 534 rows the
/// bank published on 2026-08-19 - 71.2% - carry no <c>favicon_index</c> at all, so this is the common
/// case rather than the exceptional one.
/// </summary>
/// <remarks>
/// Pure string work, deliberately: the monogram and the palette <em>index</em> are decided here, in
/// platform-neutral code with tests, while the colours those indices name belong to the interface.
/// </remarks>
public static class ChannelMonogram
{
    /// <summary>What a title made entirely of punctuation gets. The result is never empty.</summary>
    private const string Fallback = "?";

    /// <summary>
    /// One or two characters taken from the channel's own title.
    /// </summary>
    /// <remarks>
    /// Runs over runes rather than chars so a surrogate pair is never split in half - the bank's titles
    /// are not Latin-only and not even letter-initial (<c>0 N - Chillout on Radio</c>,
    /// <c># 100 GREATEST HEAVY METAL</c>, <c>24/7 Nature Radio</c>).
    /// <para>
    /// A leading number is kept as a number: <c>24/7 Nature Radio</c> reads <c>24</c>, not <c>2N</c>.
    /// A single leading digit borrows the next word's initial instead (<c>7 Rays Radio</c> -> <c>7R</c>),
    /// because one bare digit distinguishes almost nothing.
    /// </para>
    /// </remarks>
    public static string Text(string? title)
    {
        var trimmed = title?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return Fallback;
        }

        Rune? first = null;
        Rune? firstFollower = null;
        Rune? secondInitial = null;
        var inToken = false;
        var tokenIndex = 0;

        foreach (var rune in trimmed.EnumerateRunes())
        {
            if (!Rune.IsLetter(rune) && !Rune.IsDigit(rune))
            {
                inToken = false;
                continue;
            }

            if (!inToken)
            {
                inToken = true;
                tokenIndex++;
            }

            if (tokenIndex == 1)
            {
                if (first is null)
                {
                    first = rune;
                }
                else if (firstFollower is null)
                {
                    firstFollower = rune;
                }
            }
            else if (tokenIndex == 2)
            {
                secondInitial = rune;
                break;
            }
        }

        if (first is not Rune head)
        {
            return Fallback;
        }

        var builder = new StringBuilder(4).Append(head.ToString());
        if (Rune.IsDigit(head))
        {
            // Two digits of the same number, or one digit plus the next word's initial - never a digit
            // followed by a letter from the middle of the same token.
            if (firstFollower is Rune follower && Rune.IsDigit(follower))
            {
                builder.Append(follower.ToString());
            }
            else if (secondInitial is Rune next && Rune.IsLetter(next))
            {
                builder.Append(next.ToString());
            }
        }
        else if (secondInitial is Rune initial && Rune.IsLetter(initial))
        {
            builder.Append(initial.ToString());
        }

        return builder.ToString().ToUpperInvariant();
    }

    /// <summary>
    /// Which entry of a palette of <paramref name="paletteSize"/> colours this title owns.
    /// </summary>
    /// <remarks>
    /// FNV-1a is written out here on purpose. <see cref="string.GetHashCode()"/> is randomized per
    /// process in .NET, so a palette driven by it would repaint every station on every launch - and the
    /// spec requires a station to look the same across restarts and across machines. Do not "simplify"
    /// this back to the framework hash.
    /// </remarks>
    public static int PaletteIndex(string? title, int paletteSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(paletteSize);
        var trimmed = title?.Trim() ?? string.Empty;
        unchecked
        {
            const uint offsetBasis = 2166136261;
            const uint prime = 16777619;
            var hash = offsetBasis;
            foreach (var character in trimmed)
            {
                hash = (hash ^ (byte)(character & 0xFF)) * prime;
                hash = (hash ^ (byte)(character >> 8)) * prime;
            }

            return (int)(hash % (uint)paletteSize);
        }
    }
}
