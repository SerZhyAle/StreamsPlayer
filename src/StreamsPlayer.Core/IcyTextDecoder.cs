using System.Text;

namespace StreamsPlayer.Core;

/// <summary>SP-0131: which decoding turned an ICY metadata block into text.</summary>
public enum IcyTextEncoding
{
    Utf8,
    Windows1252,
    Windows1251
}

/// <summary>
/// SP-0131: decodes the bytes of an ICY metadata block. The protocol names no encoding; modern servers send
/// UTF-8, but many Shoutcast and Icecast stations still send the single-byte code page of their source
/// software. Decoding those as UTF-8 turned "Beyoncé" into a replacement character and a Cyrillic title
/// into a row of them.
/// </summary>
/// <remarks>
/// The rule: a block that is valid UTF-8 is UTF-8 - a single-byte title almost never is by accident,
/// because every byte at or above 0x80 would have to form a well-shaped multi-byte sequence. Anything else
/// is single-byte, and the choice between Windows-1252 and Windows-1251 is made per word: a Cyrillic word
/// in Windows-1251 is made almost entirely of bytes at or above 0xC0, while a Western word carries its
/// accented letters among plain ASCII ones ("Beyoncé", "Canção", "Größe"). The encoding the majority of
/// such words votes for wins; a tie keeps Windows-1252, the encoding the video engine assumes.
/// </remarks>
public static class IcyTextDecoder
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // The code-page encodings ship in the shared framework; asking the provider directly avoids registering
    // it process-wide.
    private static readonly Encoding Windows1252 = CodePagesEncodingProvider.Instance.GetEncoding(1252)!;
    private static readonly Encoding Windows1251 = CodePagesEncodingProvider.Instance.GetEncoding(1251)!;

    public static string Decode(ReadOnlySpan<byte> bytes, out IcyTextEncoding encoding)
    {
        try
        {
            var text = StrictUtf8.GetString(bytes);
            encoding = IcyTextEncoding.Utf8;
            return text;
        }
        catch (DecoderFallbackException)
        {
        }

        encoding = LooksLikeWindows1251(bytes) ? IcyTextEncoding.Windows1251 : IcyTextEncoding.Windows1252;
        return (encoding == IcyTextEncoding.Windows1251 ? Windows1251 : Windows1252).GetString(bytes);
    }

    /// <summary>Counts, over the words that carry a non-ASCII letter, how many are mostly non-ASCII.</summary>
    internal static bool LooksLikeWindows1251(ReadOnlySpan<byte> bytes)
    {
        var cyrillicWords = 0;
        var westernWords = 0;
        var high = 0;
        var ascii = 0;

        for (var index = 0; index <= bytes.Length; index++)
        {
            var value = index < bytes.Length ? bytes[index] : (byte)0;
            if (IsAsciiLetter(value))
            {
                ascii++;
                continue;
            }

            if (IsHighLetter(value))
            {
                high++;
                continue;
            }

            // A word ended.
            if (high > 0)
            {
                if (high > ascii)
                {
                    cyrillicWords++;
                }
                else
                {
                    westernWords++;
                }
            }

            high = 0;
            ascii = 0;
        }

        return cyrillicWords > westernWords;
    }

    private static bool IsAsciiLetter(byte value) => value is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z';

    /// <summary>
    /// A byte that is a letter in Windows-1251: А-я at 0xC0-0xFF, plus Ё/ё and the Ukrainian and
    /// Belarusian letters placed lower in the table. In Windows-1252 0xC0-0xFF are accented Latin
    /// letters, so a word's composition, not the byte alone, decides.
    /// </summary>
    private static bool IsHighLetter(byte value) =>
        value >= 0xC0 || value is 0xA8 or 0xB8 or 0xAA or 0xBA or 0xAF or 0xBF or 0xB2 or 0xB3 or 0xA5 or 0xB4 or 0xA1 or 0xA2;
}
