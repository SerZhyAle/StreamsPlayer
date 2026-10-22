using System.Security.Cryptography;

namespace StreamsPlayer.Core;

/// <summary>
/// Unpadded base64url, the spelling every binary member of `DEVICE-EXCHANGE` section 27 uses (items DF and
/// DH). The reader is strict: padding, the standard alphabet, whitespace and a non-canonical final symbol are
/// refused, so one byte string has exactly one accepted text.
/// </summary>
public static class ExchangeBase64Url
{
    public static string Encode(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool TryDecode(string? text, out byte[] bytes)
    {
        bytes = [];
        if (text is null || text.Length % 4 == 1)
        {
            return false;
        }

        foreach (var character in text)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')
            {
                return false;
            }
        }

        var standard = text.Replace('-', '+').Replace('_', '/');
        standard = standard.PadRight(standard.Length + (4 - standard.Length % 4) % 4, '=');
        try
        {
            var decoded = Convert.FromBase64String(standard);
            // A final symbol with stray low bits decodes but would re-encode differently.
            if (!CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(Encode(decoded)), System.Text.Encoding.ASCII.GetBytes(text)))
            {
                return false;
            }

            bytes = decoded;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
