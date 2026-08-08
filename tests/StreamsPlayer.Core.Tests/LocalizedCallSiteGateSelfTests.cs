namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0057 criterion 6, second half: proves the gate is capable of failing.
/// </summary>
/// <remarks>
/// A comparison that reached nothing would pass exactly as quietly as a clean one, so each defect the gate
/// exists to catch is fed to it deliberately and asserted to be caught. These run against synthetic
/// sources and a synthetic dictionary, so they stay green while the real ones are being edited.
/// </remarks>
public sealed class LocalizedCallSiteGateSelfTests
{
    private static readonly IReadOnlyDictionary<string, string> English = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Ready"] = "Ready",
        ["ChannelCount"] = "Channels: {0:N0} / {1:N0}",
        ["ChannelPreviewsWorking"] = "Pictures: {0} of {1}",
        ["AddedStream"] = "Added {0}"
    };

    private static IReadOnlyList<string> Problems(string body) =>
        LocalizedCallSiteGate.Inspect([AppSourceFile.Parse("Synthetic.cs", $"class C\n{{\n{body}\n}}\n")], English)
            .Problems;

    private static IReadOnlyList<string> MarkupProblems(string markup) =>
        LocalizedCallSiteGate.InspectMarkup([AppSourceFile.Parse("Synthetic.xaml", markup)], English);

    [Fact]
    public void AMatchingCallSiteIsAccepted() =>
        Assert.Empty(Problems("""
                void M()
                {
                    SetStatus("Ready");
                    SetStatus("ChannelCount", shown, total);
                    SetStatus("AddedStream", title);
                }
            """));

    [Fact]
    public void OneArgumentTooFewIsDetected()
    {
        // The SP-0056 defect, reproduced: the string gained {1} and the call site did not.
        var problem = Assert.Single(Problems("""    void M() { SetStatus("ChannelPreviewsWorking", processed); }"""));

        Assert.Contains("ChannelPreviewsWorking", problem, StringComparison.Ordinal);
        Assert.Contains("needs 2 format argument(s) but SetStatus supplies 1", problem, StringComparison.Ordinal);
        Assert.Contains("FormatException", problem, StringComparison.Ordinal);
        Assert.Contains("Synthetic.cs:3", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryArgumentMissingIsDetected() =>
        Assert.Contains(
            "supplies 0",
            Assert.Single(Problems("""    void M() { SetStatus("ChannelCount"); }""")),
            StringComparison.Ordinal);

    [Fact]
    public void OneArgumentTooManyIsDetected()
    {
        // Silent at runtime, so the gate is the only thing that can report it. It is what a placeholder
        // removed from the string and left behind in the code looks like.
        var problem = Assert.Single(Problems("""    void M() { SetStatus("Ready", extra); }"""));

        Assert.Contains("needs 0 format argument(s) but SetStatus supplies 1", problem, StringComparison.Ordinal);
        Assert.Contains("surplus is ignored", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AKeyThatNoLongerExistsIsDetected()
    {
        var problem = Assert.Single(Problems("""    void M() { SetStatus("ChannelCounts", shown, total); }"""));

        Assert.Contains("not a key in Localization.en.xaml", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void APlaceholderStringReachedThroughTheNoArgumentLookupIsDetected() =>
        Assert.Contains(
            "supplies 0",
            Assert.Single(Problems("""    string M() => LocalizationService.Get("AddedStream");""")),
            StringComparison.Ordinal);

    [Fact]
    public void APlaceholderStringBoundAsAResourceReferenceIsDetected() =>
        Assert.Contains(
            "would read the braces",
            Assert.Single(Problems("""    void M() { Label.SetResourceReference(TextBlock.TextProperty, "AddedStream"); }""")),
            StringComparison.Ordinal);

    [Fact]
    public void ANonLocalizedResourceReferenceIsNotAProblem() =>
        // The same call binds styles and brushes. Only a localized string carrying a placeholder is wrong.
        Assert.Empty(Problems("""    void M() { Item.SetResourceReference(StyleProperty, "AccentMenuItem"); }"""));

    [Fact]
    public void APlaceholderStringBoundFromMarkupIsDetected()
    {
        var problem = Assert.Single(MarkupProblems(
            """<TextBlock Text="{DynamicResource AddedStream}" />"""));

        Assert.Contains("AddedStream", problem, StringComparison.Ordinal);
        Assert.Contains("supplies no arguments", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void APlaceholderFreeStringBoundFromMarkupIsAccepted() =>
        Assert.Empty(MarkupProblems("""
            <TextBlock Text="{DynamicResource Ready}" Style="{StaticResource GlyphButton}" />
            """));

    [Fact]
    public void AWrapperKeyIsCheckedAgainstTheArgumentsTheWrapperSupplies()
    {
        // The count is not visible at the caller, so the table carries it. This is what proves the table
        // is wired to the right argument position rather than merely present.
        Assert.Empty(Problems(
            """    void M() { ShowCountProgress(report.Processed, report.Total, "ChannelPreviewsWorking"); }"""));

        Assert.Contains(
            "needs 0 format argument(s) but ShowCountProgress supplies 2",
            Assert.Single(Problems("""    void M() { ShowCountProgress(a, b, "Ready"); }""")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void AStaleEntryPointTableIsReportedRatherThanIgnored()
    {
        // If a gated wrapper is rewritten to take fewer arguments, the key is no longer where the table
        // says. Skipping that call quietly would leave a hole exactly where someone was already editing.
        var problem = Assert.Single(Problems("""    void M() { ShowCountProgress("Ready"); }"""));

        Assert.Contains("there is no argument 2 to hold a resource key", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void BothBranchesOfAConditionalKeyAreChecked()
    {
        var problems = Problems("""    void M() { SetStatus(ok ? "Ready" : "AddedStream"); }""");

        // "Ready" needs nothing and is fine; "AddedStream" needs one argument and gets none.
        var problem = Assert.Single(problems);
        Assert.Contains("AddedStream", problem, StringComparison.Ordinal);
    }
}
