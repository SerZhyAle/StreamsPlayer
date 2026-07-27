using System.Text;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0034: proves the parity gate is capable of failing. A comparison that reached nothing would pass
/// as quietly as a clean one, so each defect the gate exists to catch is fed to it deliberately and
/// asserted to be caught. These tests exercise the parsing and comparison helpers, not the shipped
/// files, so they stay green while the real dictionaries are being written.
/// </summary>
public sealed class LocalizationGateSelfTests
{
    private const string Header = """
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                            xmlns:sys="clr-namespace:System;assembly=System.Runtime">
        """;

    private static string Dictionary(params string[] entries) =>
        $"{Header}\n{string.Join("\n", entries)}\n</ResourceDictionary>\n";

    private static string Entry(string key, string value) => $"    <sys:String x:Key=\"{key}\">{value}</sys:String>";

    private static LocalizationDictionary Parse(string code, string text, bool hasBom = false) =>
        LocalizationDictionary.Parse(code, $"{code}.xaml", hasBom, text);

    private static readonly LocalizationDictionary English = Parse("en", Dictionary(
        Entry("Ready", "Ready"),
        Entry("ChannelCount", "Channels: {0:N0} / {1:N0}"),
        Entry("UiFlowDirection", "LeftToRight")));

    [Fact]
    public void ACleanTranslationIsAccepted()
    {
        var translated = Parse("de", Dictionary(
            Entry("Ready", "Bereit"),
            Entry("ChannelCount", "Kanäle: {0:N0} / {1:N0}"),
            Entry("UiFlowDirection", "LeftToRight")));

        Assert.Empty(English.Values.Keys.Except(translated.Values.Keys));
        Assert.Empty(translated.Values.Keys.Except(English.Values.Keys));
        Assert.Equal(
            LocalizationDictionary.PlaceholderIndices(English.Values["ChannelCount"]),
            LocalizationDictionary.PlaceholderIndices(translated.Values["ChannelCount"]));
    }

    [Fact]
    public void AMissingKeyIsDetected()
    {
        var translated = Parse("de", Dictionary(
            Entry("Ready", "Bereit"),
            Entry("UiFlowDirection", "LeftToRight")));

        Assert.Equal(["ChannelCount"], English.Values.Keys.Except(translated.Values.Keys));
    }

    [Fact]
    public void AnExtraKeyIsDetected()
    {
        var translated = Parse("de", Dictionary(
            Entry("Ready", "Bereit"),
            Entry("ChannelCount", "Kanäle: {0:N0} / {1:N0}"),
            Entry("UiFlowDirection", "LeftToRight"),
            Entry("Invented", "Erfunden")));

        Assert.Equal(["Invented"], translated.Values.Keys.Except(English.Values.Keys));
    }

    [Fact]
    public void ADuplicatedKeyIsDetected()
    {
        var translated = Parse("de", Dictionary(
            Entry("Ready", "Bereit"),
            Entry("Ready", "Fertig")));

        var duplicates = translated.KeysInFileOrder
            .GroupBy(key => key)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        Assert.Equal(["Ready"], duplicates);
        // The parsed value map keeps one entry, so counting keys alone would have missed this.
        Assert.Single(translated.Values);
        Assert.Equal(2, translated.KeysInFileOrder.Count);
    }

    [Theory]
    // A dropped argument.
    [InlineData("Kanäle: {0:N0}", "0")]
    // A renumbered argument.
    [InlineData("Kanäle: {1:N0} / {2:N0}", "1,2")]
    // A duplicated argument.
    [InlineData("Kanäle: {0:N0} / {0:N0}", "0,0")]
    // No placeholders at all.
    [InlineData("Kanäle", "")]
    public void AChangedPlaceholderSetIsDetected(string translatedValue, string expectedIndices)
    {
        var actual = LocalizationDictionary.PlaceholderIndices(translatedValue);

        Assert.Equal(expectedIndices, string.Join(",", actual));
        Assert.NotEqual(
            LocalizationDictionary.PlaceholderIndices(English.Values["ChannelCount"]),
            actual);
    }

    [Fact]
    public void AFormatSpecifierChangeAloneIsNotAFailure()
    {
        // {0:N0} and {0} take the same argument; a translator may reasonably want a different specifier.
        Assert.Equal(
            LocalizationDictionary.PlaceholderIndices("Channels: {0:N0} / {1:N0}"),
            LocalizationDictionary.PlaceholderIndices("Channels: {0} / {1}"));
    }

    [Fact]
    public void AByteOrderMarkIsDetected()
    {
        var text = Dictionary(Entry("Ready", "Bereit"));
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        // GetBytes does NOT emit the preamble - only a StreamWriter would. Prepending it explicitly is
        // what actually produces the byte sequence the gate has to reject.
        var withBom = utf8.GetPreamble().Concat(utf8.GetBytes(text)).ToArray();
        var path = Path.Combine(Path.GetTempPath(), $"Localization.zz.{Guid.NewGuid():N}.xaml");

        try
        {
            File.WriteAllBytes(path, withBom);
            Assert.True(LocalizationDictionary.Load(path).HasByteOrderMark);

            File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text));
            Assert.False(LocalizationDictionary.Load(path).HasByteOrderMark);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AValueLeftInEnglishIsDetected()
    {
        var translated = Parse("de", Dictionary(
            Entry("Ready", "Ready"),
            Entry("ChannelCount", "Kanäle: {0:N0} / {1:N0}"),
            Entry("UiFlowDirection", "LeftToRight")));

        Assert.Equal(English.Values["Ready"], translated.Values["Ready"]);
        Assert.NotEqual(English.Values["ChannelCount"], translated.Values["ChannelCount"]);
    }

    [Fact]
    public void AWrongLayoutDirectionIsDetected()
    {
        var arabic = Parse("ar", Dictionary(Entry("UiFlowDirection", "LeftToRight")));
        var expected = InterfaceLanguages.For(AppLanguage.Arabic).RightToLeft ? "RightToLeft" : "LeftToRight";

        Assert.Equal("RightToLeft", expected);
        Assert.NotEqual(expected, arabic.Values["UiFlowDirection"]);
    }

    [Fact]
    public void AnEmptyValueIsDetected()
    {
        var translated = Parse("de", Dictionary(Entry("Ready", "")));

        Assert.True(string.IsNullOrWhiteSpace(translated.Values["Ready"]));
    }

    [Fact]
    public void MalformedXamlIsRejectedRatherThanIgnored()
    {
        Assert.ThrowsAny<Exception>(() => Parse("de", $"{Header}\n{Entry("Ready", "Bereit")}"));
    }
}
