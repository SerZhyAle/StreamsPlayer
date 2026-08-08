using System.Globalization;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0057 criterion 7: the backstop. These facts describe what a placeholder/argument disagreement costs
/// a user if one ever escapes the gate - a visibly incomplete sentence, never a process exit.
/// </summary>
public sealed class LocalizedFormatTests
{
    private static readonly IFormatProvider Invariant = CultureInfo.InvariantCulture;

    [Theory]
    [InlineData("Ready", "")]
    [InlineData("Channels: {0}", "0")]
    [InlineData("Channels: {0:N0} / {1:N0}", "0,1")]
    // Out of order in the text, ordered in the result: this is a multiset, so a swap is not a difference.
    [InlineData("{1} of {0}", "0,1")]
    // The same argument used twice is two entries, so a duplicated placeholder is a difference.
    [InlineData("{0} and {0}", "0,0")]
    public void PlaceholderIndicesReadsTheTemplate(string template, string expected) =>
        Assert.Equal(expected, string.Join(",", LocalizedFormat.PlaceholderIndices(template)));

    [Theory]
    // A doubled brace is an escape for a literal brace and references no argument.
    [InlineData("{{0}}")]
    [InlineData("Use {{0}} to mean the count")]
    // A group this parser cannot describe yields nothing rather than a wrong index or an exception.
    [InlineData("{}")]
    [InlineData("{abc}")]
    [InlineData("{0")]
    [InlineData("no braces at all")]
    public void PlaceholderIndicesFindsNothingWorthSupplying(string template) =>
        Assert.Empty(LocalizedFormat.PlaceholderIndices(template));

    [Fact]
    public void AnEscapedBraceBesideARealPlaceholderIsNotCounted()
    {
        // The escape must neither swallow the placeholder that follows it nor be counted as one.
        Assert.Equal([0], LocalizedFormat.PlaceholderIndices("{{literal}} {0}"));
    }

    [Theory]
    [InlineData("Ready", 0)]
    [InlineData("Channels: {0}", 1)]
    [InlineData("Channels: {0} / {1}", 2)]
    // One past the highest index, not the number of placeholders: a template using only {2} still needs
    // three arguments, because string.Format indexes into the array.
    [InlineData("Only {2}", 3)]
    public void RequiredArgumentCountIsOnePastTheHighestIndex(string template, int expected) =>
        Assert.Equal(expected, LocalizedFormat.RequiredArgumentCount(template));

    [Fact]
    public void TheExpectedCaseFormatsNormally() =>
        Assert.Equal(
            "Channels: 1,234 / 5,678",
            LocalizedFormat.Apply(CultureInfo.GetCultureInfo("en-US"), "Channels: {0:N0} / {1:N0}", 1234, 5678));

    [Fact]
    public void TheProviderIsHonoured()
    {
        // Not a formality: the application formats in the interface culture, so a helper that dropped the
        // provider would silently re-render every localized number in the invariant one. The Russian
        // rendering is compared for difference rather than against an exact string - its group separator
        // is a non-breaking space whose exact code point has moved between ICU versions.
        var american = LocalizedFormat.Apply(CultureInfo.GetCultureInfo("en-US"), "{0:N1}", 1234.5);
        var russian = LocalizedFormat.Apply(CultureInfo.GetCultureInfo("ru-RU"), "{0:N1}", 1234.5);

        Assert.Equal("1,234.5", american);
        Assert.EndsWith(",5", russian, StringComparison.Ordinal);
        Assert.NotEqual(american, russian);
    }

    [Fact]
    public void SurplusArgumentsAreIgnored()
    {
        // Already true of string.Format. Asserted so the tolerance is a decision rather than an accident -
        // the gate is what reports surplus, because at runtime nothing can.
        Assert.Equal("Channels: 7", LocalizedFormat.Apply(Invariant, "Channels: {0}", 7, 8, 9));
    }

    [Fact]
    public void AShortfallLeavesTheUnsuppliedPositionBlankInsteadOfThrowing()
    {
        // The SP-0056 defect exactly: a string that gained {1} while its call site still passed one value.
        Assert.Equal("Pictures: 12 of ", LocalizedFormat.Apply(Invariant, "Pictures: {0} of {1}", 12));
    }

    [Fact]
    public void EveryArgumentMissingStillReturnsTheSentence() =>
        Assert.Equal("Pictures:  of ", LocalizedFormat.Apply(Invariant, "Pictures: {0} of {1}"));

    [Fact]
    public void ANullArgumentIsTheSameAsAnUnsuppliedOne() =>
        Assert.Equal(
            LocalizedFormat.Apply(Invariant, "Pictures: {0} of {1}", 12),
            LocalizedFormat.Apply(Invariant, "Pictures: {0} of {1}", 12, null));

    [Fact]
    public void ANullArgumentArrayIsTolerated() =>
        Assert.Equal("Pictures:  of ", LocalizedFormat.Apply(Invariant, "Pictures: {0} of {1}", null));

    [Fact]
    public void AShortfallOnAFormattedPlaceholderDoesNotThrow()
    {
        // Padding supplies null, and a specifier applied to null is still the empty string rather than a
        // second exception - the case that would otherwise defeat the padding.
        Assert.Equal("Channels:  / 5", LocalizedFormat.Apply(Invariant, "Channels: {0:N0} / {1:N0}", null, 5));
    }

    [Theory]
    // Unbalanced braces: padding cannot fix these, so the catch is what covers them.
    [InlineData("Channels: {0")]
    [InlineData("Channels: 0}")]
    [InlineData("Channels: {0}}")]
    public void ATemplateTheRuntimeCannotParseIsReturnedVerbatim(string template)
    {
        // Degrading to the template mirrors a missing key, which already renders as the key name.
        Assert.Equal(template, LocalizedFormat.Apply(Invariant, template, 7));
    }

    [Fact]
    public void AKeyNameStandingInForAMissingStringIsUnaffected()
    {
        // LocalizationService.Get degrades an unknown key to the key itself. A key name has no braces, so
        // the two degradations compose instead of interfering.
        Assert.Equal("ChannelPreviewsWorking", LocalizedFormat.Apply(Invariant, "ChannelPreviewsWorking", 1, 2));
    }
}
