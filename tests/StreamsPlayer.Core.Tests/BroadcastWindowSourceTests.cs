using System.Text.RegularExpressions;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0252: what the broadcast window and its header button must keep, pinned over the App's own sources read as text
/// (the WPF project is not loaded here). Each test names the requirement it holds shut.
/// </summary>
public sealed class BroadcastWindowSourceTests
{
    private static AppSourceFile Source(string fileName) => Assert.Single(AppSourceFile.LoadAll(fileName));

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

    [Fact]
    public void Requirement_6_ClosingTheWindowNeverStopsABroadcast()
    {
        var window = Source("BroadcastWindow.xaml.cs");
        Assert.DoesNotContain("OnClosing", window.Masked, StringComparison.Ordinal);
        Assert.DoesNotContain("Closing +=", window.Masked, StringComparison.Ordinal);

        // The window's lifetime hooks, its key handler and its Close answer reach no stop of the producer.
        foreach (var declaration in new[]
        {
            @"internal BroadcastWindow\(MainWindow host\)",
            @"private void Window_PreviewKeyDown\(",
            @"private void Close_Click\("
        })
        {
            var body = Body(window, declaration);
            Assert.DoesNotContain("StopAsync", body, StringComparison.Ordinal);
            Assert.DoesNotContain("StopAllAsync", body, StringComparison.Ordinal);
            Assert.DoesNotContain("Dispose", body, StringComparison.Ordinal);
        }

        Assert.Contains("Close()", Body(window, @"private void Close_Click\("), StringComparison.Ordinal);
        Assert.Contains("Key.Escape", Body(window, @"private void Window_PreviewKeyDown\("), StringComparison.Ordinal);
    }

    [Fact]
    public void Requirement_2_ALongStartCanBeCancelledAtEveryWaitItHas()
    {
        var producer = Source("BroadcastProducerService.cs");
        Assert.Contains("StartAsync(BroadcastSource source, CancellationToken cancel = default)", producer.Masked, StringComparison.Ordinal);
        Assert.Contains("void CancelStart(BroadcastSource source)", producer.Masked, StringComparison.Ordinal);
        Assert.Contains("_sourceOperations.WaitAsync(token)", producer.Masked, StringComparison.Ordinal);

        // The directory wait, the start deadline and the device open all end on the person's token.
        Assert.Contains("PrepareSealingAsync(state, cancel)", producer.Masked, StringComparison.Ordinal);
        Assert.Contains("CreateLinkedTokenSource(cancel)", Body(producer, @"private async Task EstablishOnExchangeCoreAsync\("), StringComparison.Ordinal);
        Assert.Contains("WaitForDirectoryAsync(DirectoryWait, cancel)", Source("BroadcastProducerService.Sealed.cs").Masked, StringComparison.Ordinal);
        Assert.Contains("video.StartAsync().WaitAsync(cancel)", Source("BroadcastProducerService.Video.cs").Masked, StringComparison.Ordinal);

        // A cancelled start is rolled back and ends as off, never as a failure with a sentence to read.
        var start = Body(producer, @"internal async Task<bool> StartAsync\(");
        Assert.Matches(@"catch \(OperationCanceledException\) when \(token\.IsCancellationRequested\)[\s\S]*StartRollback[\s\S]*BroadcastProducerState\.Stopped", start);

        Assert.Contains("CancelStart(", Source("BroadcastWindow.xaml.cs").Masked, StringComparison.Ordinal);
    }

    [Fact]
    public void Requirement_1_TheHeaderCarriesOneBroadcastControlAndTheOldTwoAreGone()
    {
        var main = Source("MainWindow.xaml").Text;
        Assert.Contains("x:Name=\"BroadcastButton\"", main, StringComparison.Ordinal);
        Assert.DoesNotContain("ExchangeSourceStatusButton", main, StringComparison.Ordinal);
        Assert.DoesNotContain("BroadcastLiveButton", main, StringComparison.Ordinal);

        // The button only opens the window; the menu of the old indicator is gone from every source.
        Assert.DoesNotContain("BuildAsync", string.Concat(AppSourceFile.LoadAll("*.cs").Select(file => file.Masked)), StringComparison.Ordinal);
        Assert.Contains("OpenBroadcastWindow()", Body(Source("MainWindow.Broadcast.cs"), @"private void BroadcastButton_Click\("), StringComparison.Ordinal);

        // The compact panel's indicator opens the same window.
        Assert.Contains("OpenBroadcastWindow(", Body(Source("MainWindow.CompactPanel.cs"), @"private void CompactPanel_BroadcastRequested\("), StringComparison.Ordinal);
    }

    [Fact]
    public void Requirement_5_SettingsKeepsTheSettingsAndStartsNothing()
    {
        foreach (var file in AppSourceFile.LoadAll("SettingsWindow*.cs"))
        {
            Assert.DoesNotContain("Broadcast.StartAsync", file.Masked, StringComparison.Ordinal);
            Assert.DoesNotContain("Broadcast.StopAsync", file.Masked, StringComparison.Ordinal);
            Assert.DoesNotContain("Broadcast.StopAllAsync", file.Masked, StringComparison.Ordinal);
        }

        // The page links to the window, which is opened once Settings (a modal window) has closed.
        Assert.Contains("RequestBroadcastWindowAfterSettings", Source("SettingsWindow.Broadcast.cs").Masked, StringComparison.Ordinal);
        Assert.Contains("OpenBroadcastWindow()", Body(Source("MainWindow.Settings.cs"), @"internal void OpenSettings\("), StringComparison.Ordinal);

        // And the window links back to the Broadcasting page.
        Assert.Contains("SettingsWindow.BroadcastPageIndex", Body(Source("BroadcastWindow.xaml.cs"), @"private void Settings_Click\("), StringComparison.Ordinal);
    }

    [Fact]
    public void DecisionOne_TheWarningMarkFollowsTheSessionAndTheLiveSourcesOnly()
    {
        var refresh = Body(Source("MainWindow.Broadcast.cs"), @"private void RefreshBroadcastIndicator\(");
        Assert.Contains("Broadcast.ServerNeededButUnreachable", refresh, StringComparison.Ordinal);
        Assert.Contains("BroadcastWarningMark.Visibility", refresh, StringComparison.Ordinal);

        // A change of the exchange session refreshes the button, not only a change of the producer.
        Assert.Contains("RefreshBroadcastIndicator()", Body(Source("MainWindow.Exchange.cs"), @"internal void InitializeExchangeStatus\("), StringComparison.Ordinal);
    }
}
