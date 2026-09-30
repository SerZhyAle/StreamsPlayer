using System.Globalization;

namespace StreamsPlayer.Core;

/// <summary>
/// Shortens text without cutting through a character (SP-0134). A <c>string</c> length counts UTF-16 code
/// units, so a plain <c>text[..n]</c> can end on the first half of a surrogate pair - an emoji becomes a
/// lone surrogate that renders as a replacement box, is rejected by some file systems and escapes to
/// garbage - or between a base letter and the combining mark or skin-tone modifier that belongs to it.
/// </summary>
/// <remarks>
/// The unit kept whole is the text element (the extended grapheme cluster .NET reports through
/// <see cref="StringInfo"/>), not merely the code point: a cut that keeps a family emoji's first person
/// and drops the rest is as wrong on screen as half a surrogate pair.
/// </remarks>
public static class TextBoundary
{
    /// <summary>
    /// The longest prefix of <paramref name="text"/> that is at most <paramref name="maxLength"/> UTF-16
    /// units long and ends on a text-element boundary. Returns <paramref name="text"/> itself when it
    /// already fits.
    /// </summary>
    public static string Truncate(string text, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (maxLength <= 0)
        {
            return string.Empty;
        }

        if (text.Length <= maxLength)
        {
            return text;
        }

        var end = 0;
        while (end < text.Length)
        {
            var next = end + StringInfo.GetNextTextElementLength(text.AsSpan(end));
            if (next > maxLength)
            {
                break;
            }

            end = next;
        }

        return text[..end];
    }

    /// <summary>
    /// The longest prefix of <paramref name="text"/>, ending on a text-element boundary, whose
    /// <see cref="Uri.EscapeDataString(string)"/> form is at most <paramref name="maxEscapedLength"/>
    /// characters long.
    /// </summary>
    /// <remarks>
    /// Percent-escaping works code point by code point, so the escaped length of a prefix is the sum of
    /// its elements' escaped lengths, and each element can be measured alone.
    /// </remarks>
    public static string TruncateEscaped(string text, int maxEscapedLength)
    {
        ArgumentNullException.ThrowIfNull(text);
        var end = 0;
        var escaped = 0;
        while (end < text.Length)
        {
            var length = StringInfo.GetNextTextElementLength(text.AsSpan(end));
            escaped += Uri.EscapeDataString(text.Substring(end, length)).Length;
            if (escaped > maxEscapedLength)
            {
                break;
            }

            end += length;
        }

        return end == text.Length ? text : text[..end];
    }
}
