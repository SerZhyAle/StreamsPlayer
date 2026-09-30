namespace StreamsPlayer.Core;

/// <summary>
/// Builds the <c>mailto:</c> link that opens the user's own mail program with the log report prepared
/// (SP-0040). Platform-neutral on purpose: the escaping is the part that silently loses half a message,
/// so it is unit-tested rather than trusted.
/// </summary>
public static class DiagnosticMailLink
{
    /// <summary>
    /// Longest the <em>escaped</em> body may be. A long <c>mailto:</c> is truncated by some clients and
    /// rejected outright by others, and what they measure is the link, not the text it came from.
    /// </summary>
    /// <remarks>
    /// SP-0134: this used to cap the body before escaping, at 1500 characters - but a Cyrillic character
    /// escapes to six and a Devanagari one to nine, so a non-Latin body inside that cap still produced a
    /// link of up to 13,500 characters. The cap now applies to what the mail client receives. The shipped
    /// report bodies escape to under a thousand characters in every interface language (Bengali, the
    /// longest, measured 951 with a typical archive path), so this bounds a hostile path or a future
    /// translation without cutting the message the product actually sends.
    /// </remarks>
    public const int MaxEscapedBodyCharacters = 2000;

    public static string Build(string recipient, string subject, string body)
    {
        // Cut on a whole character, measured escaped: a cut through a surrogate pair escapes to a
        // replacement character, and a raw-length cap says nothing about the link's real length.
        var trimmed = TextBoundary.TruncateEscaped(body, MaxEscapedBodyCharacters);
        // Both fields are escaped: an unescaped '&' or a line break in a translated body ends the
        // parameter early, and the user then mails an empty message with no idea anything was lost.
        return $"mailto:{recipient}?subject={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(trimmed)}";
    }
}
