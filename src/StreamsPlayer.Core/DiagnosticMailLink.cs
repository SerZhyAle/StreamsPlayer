namespace StreamsPlayer.Core;

/// <summary>
/// Builds the <c>mailto:</c> link that opens the user's own mail program with the log report prepared
/// (SP-0040). Platform-neutral on purpose: the escaping is the part that silently loses half a message,
/// so it is unit-tested rather than trusted.
/// </summary>
public static class DiagnosticMailLink
{
    /// <summary>
    /// Mail bodies degrade badly past this: a long <c>mailto:</c> is truncated by some clients and
    /// rejected outright by others, and escaping roughly triples the character count.
    /// </summary>
    public const int MaxBodyCharacters = 1500;

    public static string Build(string recipient, string subject, string body)
    {
        var trimmed = body.Length > MaxBodyCharacters ? body[..MaxBodyCharacters] : body;
        // Both fields are escaped: an unescaped '&' or a line break in a translated body ends the
        // parameter early, and the user then mails an empty message with no idea anything was lost.
        return $"mailto:{recipient}?subject={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(trimmed)}";
    }
}
