using System.Text.RegularExpressions;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0180 wave C: the WPF-only parts of the pre-release audit fixes cannot run in this test project -
/// it deliberately does not load the App - so the shape that makes each one true is pinned over the
/// App's own sources, read as text (<see cref="AppSourceFile"/>). Each test names the defect it holds shut.
/// </summary>
public sealed class AuditWaveCSourceTests
{
    private static AppSourceFile Source(string fileName) => Assert.Single(AppSourceFile.LoadAll(fileName));

    /// <summary>The masked text of one member body - a block or an expression body - found by its declaration.</summary>
    private static string Body(AppSourceFile file, string declarationPattern)
    {
        var match = Regex.Match(file.Masked, declarationPattern);
        Assert.True(match.Success, $"{file.Name}: no declaration matches {declarationPattern}");

        var open = file.Masked.IndexOf('{', match.Index);
        var arrow = file.Masked.IndexOf("=>", match.Index, StringComparison.Ordinal);
        if (arrow >= 0 && (open < 0 || arrow < open))
        {
            return file.Masked[arrow..file.Masked.IndexOf(';', arrow)];
        }

        var depth = 0;
        for (var position = open; position < file.Masked.Length; position++)
        {
            depth += file.Masked[position] switch { '{' => 1, '}' => -1, _ => 0 };
            if (depth == 0)
            {
                return file.Masked[open..(position + 1)];
            }
        }

        throw new InvalidOperationException($"{file.Name}: unbalanced braces after {declarationPattern}");
    }

    // C1: SetFacet replaced ItemsSource/SelectedItem outside any suppression flag, so every refill raised
    // SelectionChanged -> FilterChanged -> a debounced ApplyFilterNow that scrolls to the start.
    [Fact]
    public void FacetRefillIsGuardedSoItDoesNotScrollTheListToTheTop()
    {
        var body = Body(Source("MainWindow.Facets.cs"), @"private void PopulateFacets\(\)");

        var guardOn = body.IndexOf("_updatingLocalizedOptions = true", StringComparison.Ordinal);
        var firstFacet = body.IndexOf("SetFacet(", StringComparison.Ordinal);
        var lastFacet = body.LastIndexOf("SetFacet(", StringComparison.Ordinal);
        var guardOff = body.IndexOf("_updatingLocalizedOptions = wasUpdating", StringComparison.Ordinal);
        var finallyAt = body.IndexOf("finally", lastFacet, StringComparison.Ordinal);

        Assert.True(guardOn >= 0 && guardOn < firstFacet, "the guard must be raised before the first facet is refilled");
        Assert.True(guardOff > lastFacet, "the guard must be restored after the last facet is refilled");
        Assert.True(finallyAt >= 0 && finallyAt < guardOff, "the guard must be restored in a finally");
    }

    [Fact]
    public void AFacetThatFellBackToAllStillSchedulesAFilterEvaluation()
    {
        var body = Body(Source("MainWindow.Facets.cs"), @"private void PopulateFacets\(\)");

        Assert.Matches(@"selectionChanged\s*&&\s*IsLoaded\s*\)\s*\{\s*ScheduleFilterEvaluation\(\)", body);
    }

    [Fact]
    public void AUserFacetChangeStillReachesTheFilter()
    {
        var markup = Source("MainWindow.xaml").Text;
        foreach (var facet in new[] { "CategoryFilter", "TopicFilter", "LanguageFilter", "CountryFilter" })
        {
            Assert.Matches($@"<ComboBox\s+x:Name=""{facet}""[^>]*SelectionChanged=""FilterChanged""", markup);
        }

        var handler = Body(Source("MainWindow.CatalogView.cs"), @"private void FilterChanged\(");
        Assert.Contains("ScheduleFilterEvaluation()", handler, StringComparison.Ordinal);
    }

    // C3: the constructor walked the visual tree from the window content, before layout, and a ScrollViewer
    // has no visual children until its template is applied - so no group was found.
    [Fact]
    public void SettingsGroupsAreFoundFromThePagePanelsNotThroughTheUnappliedScrollViewer()
    {
        var body = Body(Source("SettingsWindow.Context.cs"), @"IEnumerable<Expander> AllExpanders\(\)");

        Assert.DoesNotContain("SettingsContent", body, StringComparison.Ordinal);
        Assert.Contains("PageAt(", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGroupExpansionMemoryIsWiredAndRestoredInTheConstructor()
    {
        var constructor = Body(Source("SettingsWindow.xaml.cs"), @"internal SettingsWindow\(");

        Assert.Matches(@"expander\.Expanded\s*\+=\s*Group_ExpandedChanged", constructor);
        Assert.Matches(@"expander\.Collapsed\s*\+=\s*Group_ExpandedChanged", constructor);
        Assert.Contains("_uiContext.Groups.TryGetValue", constructor, StringComparison.Ordinal);
        Assert.Contains("SettingsUiStateSanitizer.Sanitize", constructor, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDeferredViewportRestoreCannotEndTheProcess()
    {
        var body = Body(Source("SettingsWindow.Context.cs"), @"private void RestoreViewport\(\)");

        Assert.Contains("try", body, StringComparison.Ordinal);
        Assert.Matches(@"catch\s*\(Exception\s+\w+\)", body);
    }

    [Fact]
    public void TheDecorativeNavigationReportsNeverRaiseADialog()
    {
        var file = Source("SettingsWindow.Context.cs");
        var reports = file.Invocations("HandlerBoundary.Report");

        Assert.Equal(2, reports.Count);
        foreach (var report in reports)
        {
            Assert.Equal(3, report.Arguments.Count);
            var last = file.Text.Substring(report.Arguments[2].Start, report.Arguments[2].Length);
            Assert.Matches(@"notifyUser\s*:\s*false", last);
        }
    }

    // C4: the list box's item peer exposes ToString(); a record's own prints the descriptor's address.
    [Theory]
    [InlineData(@"internal sealed record BroadcastRow\(")]
    [InlineData(@"internal sealed record BroadcastDeviceHeading\(")]
    public void BroadcastListItemsOverrideToStringSoNoAddressReachesAutomation(string declaration)
    {
        var body = Body(Source("BroadcastsWindow.xaml.cs"), declaration);

        Assert.Matches(@"public\s+override\s+string\s+ToString\(\)", body);
    }

    [Fact]
    public void TheBroadcastRowIsAnnouncedAsItsTitleAndDetailOnly()
    {
        var text = Source("BroadcastsWindow.xaml.cs").Text;

        Assert.Contains(@"public override string ToString() => $""{Title}, {Detail}"";", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BroadcastHandlersReportUnderWindowQualifiedNames()
    {
        var file = Source("BroadcastsWindow.xaml.cs");
        var reports = file.Invocations("HandlerBoundary.Report");

        Assert.Equal(2, reports.Count);
        foreach (var report in reports)
        {
            var name = file.Text.Substring(report.Arguments[0].Start, report.Arguments[0].Length);
            Assert.Contains("nameof(BroadcastsWindow)", name, StringComparison.Ordinal);
        }
    }

    // C5: "Copy all" put the raw address on the clipboard with no confirmation.
    [Fact]
    public void CopyAllAsksTheHandOutConfirmationsBeforeTheClipboardIsTouched()
    {
        var file = Source("ChannelInfoWindow.xaml.cs");
        var body = Body(file, @"private void CopyAll_Click\(");

        var confirm = body.IndexOf("ConfirmAddressHandOut()", StringComparison.Ordinal);
        var clipboard = body.IndexOf("Clipboard.SetText", StringComparison.Ordinal);

        Assert.True(confirm >= 0, "Copy all must confirm the address hand-out");
        Assert.True(confirm < clipboard, "the confirmation must come before the clipboard is written");
        Assert.Contains("ChannelHandOutWarnings.KeysFor(_channel.Url)",
            Body(file, @"private bool ConfirmAddressHandOut\("), StringComparison.Ordinal);
    }

    // C6: DragMove throws when the left button is already up, and the synchronous handler had no catch.
    [Fact]
    public void TheCompactPanelDragChecksTheButtonAndSurvivesItBeingReleased()
    {
        var body = Body(Source("CompactPanelWindow.xaml.cs"), @"private void Window_MouseLeftButtonDown\(");

        Assert.Contains("Mouse.LeftButton != MouseButtonState.Pressed", body, StringComparison.Ordinal);
        Assert.Matches(@"try\s*\{\s*DragMove\(\);\s*\}\s*catch\s*\(InvalidOperationException\)", body);
    }

    // C2: the advance is pre-live only, and a superseded attempt's answer is ignored.
    [Fact]
    public void TheEndpointAdvanceIsDecidedByTheCoreRuleWithTheLiveAndRecoveryState()
    {
        var body = Body(Source("PlayerWindow.Attempts.cs"), @"private bool CanAdvanceAttempt\(\)");

        Assert.Contains("BroadcastAttemptAdvance.CanAdvance", body, StringComparison.Ordinal);
        Assert.Contains("reachedLive: _reachedLive", body, StringComparison.Ordinal);
        Assert.Contains("recoveryInFlight: _recoveryInFlight", body, StringComparison.Ordinal);
        Assert.Contains("legOpenInFlight: _legOpenInFlight", body, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOpenFailureIsJudgedByTheAttemptItBelongsTo()
    {
        var report = Body(Source("PlayerWindow.Attempts.cs"), @"private void ReportOpenFailure\(");
        var legs = Source("PlayerWindow.Legs.cs");

        Assert.Contains("epoch != _attemptEpoch", report, StringComparison.Ordinal);
        // The resource is taken through the gate; a bare assignment from the worker is the defect.
        Assert.DoesNotContain("_tunnelForwarder = forwarder;", legs.Masked, StringComparison.Ordinal);
        Assert.DoesNotContain("_relayProxy = proxy;", legs.Masked, StringComparison.Ordinal);
        Assert.Contains("TryHoldAttemptResource(epoch,", legs.Masked, StringComparison.Ordinal);
    }
}
