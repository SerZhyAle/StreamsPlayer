using System.Text;

namespace StreamsPlayer.Core;

/// <summary>
/// Makes a string a broadcaster sent safe to render. Every field a station fills in - the ICY track
/// title, the "now playing" the media engine reports - is text this product neither authored nor
/// validated, and all of it reaches a UI element that must survive whatever arrives.
/// </summary>
/// <remarks>
/// One rule, not one per surface: the radio line in the main window and the player's own line read the
/// same class of value from the same broadcasters, and a second copy of this would drift from the first
/// the moment either was hardened.
/// </remarks>
public static class BroadcastText
{
    /// <summary>
    /// Returns <paramref name="value"/> with control characters folded to spaces, whitespace runs
    /// collapsed, bidirectional overrides removed, and the length bounded by
    /// <paramref name="maxLength"/>; <c>null</c> when nothing legible is left.
    /// </summary>
    /// <remarks>
    /// The bidi strip is the one rule here that is not obvious. <see cref="char.IsControl(char)"/> does
    /// not cover U+202E and its relatives - they are format characters, not control characters - so a
    /// title carrying one would reverse the reading order of everything rendered after it in the same
    /// line, including the interface's own text around it. Only the embedding, override, isolate and
    /// directional-mark set is dropped; other format characters are left alone, because scripts that
    /// need a joiner to render correctly need it here too.
    /// </remarks>
    public static string? Sanitize(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || maxLength <= 0)
        {
            return null;
        }

        var builder = new StringBuilder(Math.Min(value.Length, maxLength));
        var lastWasSpace = false;
        foreach (var ch in value)
        {
            if (IsDirectionalFormatting(ch))
            {
                continue;
            }

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
            // Collect past the cap before cutting (SP-0134): whether the cap falls on a character boundary
            // depends on what follows it - the second half of a surrogate pair, a combining mark, a skin-tone
            // modifier - so the cut is made on text that still carries those. The bound keeps a hostile
            // megabyte title from being walked to its end.
            if (builder.Length - maxLength >= BoundaryLookahead)
            {
                break;
            }
        }

        var text = TextBoundary.Truncate(builder.ToString(), maxLength).TrimEnd(' ');
        return text.Length == 0 ? null : text;
    }

    /// <summary>Code units read past the cap so the cut can see the whole character it lands in.</summary>
    private const int BoundaryLookahead = 16;

    // Written as code points rather than as character literals on purpose: these characters are
    // invisible in an editor, so a source file carrying them literally is one re-encoding away from a
    // rule that silently stops matching - and unreviewable in a diff either way.
    private const char LeftToRightMark = (char)0x200E;
    private const char RightToLeftMark = (char)0x200F;
    private const char FirstEmbedding = (char)0x202A;   // LEFT-TO-RIGHT EMBEDDING
    private const char LastOverride = (char)0x202E;     // RIGHT-TO-LEFT OVERRIDE
    private const char FirstIsolate = (char)0x2066;     // LEFT-TO-RIGHT ISOLATE
    private const char LastIsolate = (char)0x2069;      // POP DIRECTIONAL ISOLATE

    private static bool IsDirectionalFormatting(char ch) => ch is
        LeftToRightMark or RightToLeftMark or
        >= FirstEmbedding and <= LastOverride or
        >= FirstIsolate and <= LastIsolate;
}
