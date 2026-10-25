using System.Globalization;
using System.Security.Cryptography;

namespace StreamsPlayer.Core;

/// <summary>LIVE-BROADCAST item R: how the outward link of a broadcast is protected. <see cref="Generated"/> is the default.</summary>
public enum BroadcastLinkChoice
{
    Generated,
    Custom,
    None
}

/// <summary>
/// The password of a broadcast's pairs (LIVE-BROADCAST items M and R): the generated one, the rule a password the
/// person typed must pass, and the two user names the family shares. Holds no state and prints nothing.
/// </summary>
public static class BroadcastLinkPassword
{
    /// <summary>Item R: a password the person sets is 1 to 64 Unicode code points.</summary>
    public const int MaximumCodePoints = 64;

    /// <summary>The user of the account's own devices; the same name on the phone, so a shared descriptor predicts it.</summary>
    public const string OwnUser = "account";

    /// <summary>The user of the outward link; the same name on the phone.</summary>
    public const string LinkUser = "listener";

    // The phone makes 12 characters (about 69 bits). This product has no typing-length pressure on a generated
    // password - the person copies it - so 16 (about 92 bits) costs nothing and puts the guess out of reach.
    private const int GeneratedLength = 16;

    // No look-alike characters (l, 1, I, 0, O): the password is read off a screen and typed by a person.
    private const string Alphabet = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string Generate() => RandomNumberGenerator.GetString(Alphabet, GeneratedLength);

    /// <summary>
    /// Item R: 1 to 64 code points, no control character, and no other rule - never length or complexity beyond that,
    /// never trimmed. A paragraph or line separator is refused with the controls: it breaks a line the same way.
    /// An unpaired surrogate is not text, and would encode to a different byte string on each side of the wire.
    /// </summary>
    public static bool IsAcceptable(string? text) =>
        !string.IsNullOrEmpty(text)
        && text.Length <= MaximumCodePoints * 2
        && TryCountPlainText(text, out var codePoints)
        && codePoints <= MaximumCodePoints;

    /// <summary>
    /// Counts Unicode code points (a surrogate pair is one), and fails on a control character, a line or paragraph
    /// separator, or an unpaired surrogate. The verifier's user name is held to the same text rule.
    /// </summary>
    internal static bool TryCountPlainText(string text, out int codePoints)
    {
        codePoints = 0;
        for (var index = 0; index < text.Length; index++)
        {
            var current = text[index];
            if (char.IsHighSurrogate(current))
            {
                if (index + 1 >= text.Length || !char.IsLowSurrogate(text[index + 1]))
                {
                    return false;
                }

                index++;
            }
            else if (char.IsLowSurrogate(current)
                || CharUnicodeInfo.GetUnicodeCategory(current) is UnicodeCategory.Control
                    or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                return false;
            }

            codePoints++;
        }

        return true;
    }
}
