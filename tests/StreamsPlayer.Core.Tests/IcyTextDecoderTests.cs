using System.Text;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>SP-0131: a metadata block is decoded the way the station wrote it.</summary>
public sealed class IcyTextDecoderTests
{
    private static readonly Encoding Windows1252 = CodePagesEncodingProvider.Instance.GetEncoding(1252)!;
    private static readonly Encoding Windows1251 = CodePagesEncodingProvider.Instance.GetEncoding(1251)!;

    [Theory]
    [InlineData("StreamTitle='Beyoncé - Halo';")]
    [InlineData("StreamTitle='Кино - Группа крови';")]
    [InlineData("StreamTitle='Plain ASCII';")]
    public void ValidUtf8IsReadAsUtf8Unchanged(string block)
    {
        var text = IcyTextDecoder.Decode(Encoding.UTF8.GetBytes(block), out var encoding);

        Assert.Equal(block, text);
        Assert.Equal(IcyTextEncoding.Utf8, encoding);
    }

    [Theory]
    [InlineData("StreamTitle='Beyoncé - Halo';")]
    [InlineData("StreamTitle='João Gilberto - Canção do Amor Demais';")]
    [InlineData("StreamTitle='Die Ärzte - Schrei nach Liebe';")]
    [InlineData("StreamTitle='Édith Piaf - Non, je ne regrette rien';")]
    public void Windows1252TitleDecodesToTheWrittenText(string block)
    {
        var text = IcyTextDecoder.Decode(Windows1252.GetBytes(block), out var encoding);

        Assert.Equal(block, text);
        Assert.Equal(IcyTextEncoding.Windows1252, encoding);
    }

    [Theory]
    [InlineData("StreamTitle='Кино - Группа крови';")]
    [InlineData("StreamTitle='Океан Ельзи - Обійми';")]
    [InlineData("StreamTitle='Metallica - Ещё один день';")]
    [InlineData("StreamTitle='Я';")]
    public void Windows1251TitleDecodesToTheWrittenText(string block)
    {
        var text = IcyTextDecoder.Decode(Windows1251.GetBytes(block), out var encoding);

        Assert.Equal(block, text);
        Assert.Equal(IcyTextEncoding.Windows1251, encoding);
    }

    // Release audit 26.1001.0140: a title whose only accented word is a lone accented letter or a short Latin word
    // must stay Windows-1252, not turn into Cyrillic mojibake.
    [Theory]
    [InlineData("StreamTitle='Dov'è - Mina';")]
    [InlineData("StreamTitle='Ela é linda';")]
    [InlineData("StreamTitle='L'été - Dalida';")]
    [InlineData("StreamTitle='Tout à fait';")]
    [InlineData("StreamTitle='Où est la gare';")]
    public void ALoneAccentedWesternWordIsNotMistakenForCyrillic(string block)
    {
        var text = IcyTextDecoder.Decode(Windows1252.GetBytes(block), out var encoding);

        Assert.Equal(block, text);
        Assert.Equal(IcyTextEncoding.Windows1252, encoding);
    }

    [Fact]
    public void ZeroPaddingOfTheBlockDoesNotChangeTheChoice()
    {
        var bytes = new byte[64];
        var payload = Windows1251.GetBytes("StreamTitle='Кино';");
        payload.CopyTo(bytes, 0);

        var text = IcyTextDecoder.Decode(bytes, out var encoding);

        Assert.Equal(IcyTextEncoding.Windows1251, encoding);
        Assert.Equal("Кино", IcyMetadataParser.ExtractStreamTitle(text));
    }
}
