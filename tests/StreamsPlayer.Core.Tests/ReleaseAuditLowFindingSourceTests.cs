using System.Text.RegularExpressions;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0212: the Low findings of the 26.1010.0106 release audit whose fixes live in WPF code this test
/// project deliberately does not load. Pinned over the App's own sources, read as text
/// (<see cref="AppSourceFile"/>), on the <c>AuditWaveCSourceTests</c> terms: each test names the defect
/// it holds shut.
/// </summary>
public sealed class ReleaseAuditLowFindingSourceTests
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

    // L9: the freshly loaded exchange account was re-saved on every launch, so a transient failure of
    // that redundant write marked storage unavailable for the whole session.
    [Fact]
    public void TheExchangeAccountIsSavedOnlyWhenItIsNew()
    {
        var body = Body(Source("ExchangeSourceService.cs"), @"internal ExchangeSourceService\(string directory\)");
        var guard = body.IndexOf("if (_store.CreatedFresh)", StringComparison.Ordinal);
        var save = body.IndexOf("_store.Save(Account)", StringComparison.Ordinal);
        Assert.True(guard >= 0, "the constructor no longer guards the account save with CreatedFresh");
        Assert.True(save >= 0, "the constructor no longer saves the account");
        Assert.True(guard < save, "the account save must sit inside the CreatedFresh guard");
    }

    // L10: after a startup read failure, a flush queued behind the first successful re-read held the list
    // it captured before that merge, and wrote it over every remembered placement.
    [Fact]
    public void ASessionThatStartedAgainstAnUnreadableGeometryFileMergesOnEveryFlush()
    {
        var source = Source("PlayerGeometryFile.cs");
        var prime = Body(source, @"internal static async Task PrimeAsync\(\)");
        Assert.Contains("_startupReadFailed = Store.IsUnreadable", prime, StringComparison.Ordinal);

        var flush = Body(source, @"private static async Task FlushAsync\(IReadOnlyList<ChannelWindowGeometry> entries\)");
        var merge = flush.IndexOf("if (_startupReadFailed)", StringComparison.Ordinal);
        var save = flush.IndexOf("Store.SaveAsync(entries)", StringComparison.Ordinal);
        Assert.True(merge >= 0 && merge < save, "every flush of such a session must merge before it writes");
        Assert.DoesNotContain("if (Store.IsUnreadable)", flush, StringComparison.Ordinal);
    }

    // L11: a player window the user closed moments before quitting was still tearing down natively when
    // the process exited, because quit waited only for the windows it closed itself.
    [Fact]
    public void AUserClosedPlayerWindowIsAlsoAwaitedAtQuit()
    {
        var source = Source("MainWindow.xaml.cs");
        var registration = source.Masked.IndexOf("window.Closed += (_, _) => HandlerBoundary.Run(", StringComparison.Ordinal);
        Assert.True(registration >= 0, "MainWindow no longer registers the player window's Closed handler");
        var handler = source.Masked[registration..(registration + 1600)];
        Assert.Contains("_closedPlayerEngines.Add(window.EngineReleased)", handler, StringComparison.Ordinal);
        Assert.Contains("_closedRecordingFinishes.Add(finish)", handler, StringComparison.Ordinal);
    }

    // L12: after a failed catalog load the window holds defaults, and a session save would have
    // overwritten the user's stored filters and scroll offset with them.
    [Fact]
    public void NoBrowsingSessionSaveOverACatalogThatFailedToLoad()
    {
        var body = Body(Source("MainWindow.BrowsingSession.cs"), @"private async Task SaveBrowsingSessionAsync\(bool force = false\)");
        Assert.Contains("if (_catalogStateUnreadable)", body, StringComparison.Ordinal);
    }

    // L13: a directory frame that arrived while a user operation owned the window was dropped and never
    // re-applied, and a directory at rest never sends another frame.
    [Fact]
    public void ADirectoryFrameDroppedWhileBusyIsReappliedOnRelease()
    {
        var apply = Body(Source("MainWindow.Exchange.cs"), @"private async void ApplyExchangeDirectory\(\)");
        Assert.Contains("_exchangeDirectoryPending = true", apply, StringComparison.Ordinal);

        var busy = Body(Source("MainWindow.xaml.cs"), @"private void SetBusy\(bool busy, bool cancellable = false\)");
        Assert.Contains("!busy && _exchangeDirectoryPending", busy, StringComparison.Ordinal);
        Assert.Contains("ApplyExchangeDirectory()", busy, StringComparison.Ordinal);
    }

    // L14: the close work disposed the standard audio playback but never stopped the FastMediaSorter
    // LibVLC leg or its tunnel forwarder - a local listener alive until process death.
    [Fact]
    public void TheCloseWorkStopsTheFastMediaSorterLegToo()
    {
        var body = Body(Source("MainWindow.Previews.cs"), @"private async Task RunCloseWorkAsync\(\)");
        Assert.Contains("StopFastMediaSorterAudioPlayback()", body, StringComparison.Ordinal);
    }

    // L15: the whole close sequence sat in one try/catch, so a fault in the first step skipped the engine
    // disposals and the teardown waits - and the process exited mid-teardown.
    [Fact]
    public void EveryCloseStepIsContainedOnItsOwnAndTheWaitsAreLast()
    {
        var body = Body(Source("MainWindow.Previews.cs"), @"private async Task RunCloseWorkAsync\(\)");
        Assert.True(Regex.Matches(body, "RunCloseStep").Count >= 8,
            "the close steps are no longer each contained in their own step runner");
        var audioDispose = body.IndexOf("StopFastMediaSorterAudioPlayback()", StringComparison.Ordinal);
        var enginesWait = body.IndexOf("Task.WhenAll(_closedPlayerEngines)", StringComparison.Ordinal);
        var finishesWait = body.IndexOf("Task.WhenAll(_closedRecordingFinishes)", StringComparison.Ordinal);
        Assert.True(audioDispose >= 0 && enginesWait >= 0 && finishesWait >= 0,
            "the close work lost one of the audio disposal or the teardown waits");
        Assert.True(audioDispose < enginesWait && enginesWait < finishesWait,
            "the waits are last: a fault in any disposal must not skip them, and a slow teardown must not cost anything above");
    }

    // L16: next/previous from a media key played a channel the user hid or the bank retired.
    [Fact]
    public void AMediaKeyNeverPlaysAHiddenOrRetiredChannel()
    {
        var body = Body(Source("MainWindow.SystemMedia.cs"), @"private bool IsAudioNavEntryAvailable\(int index\)");
        Assert.Contains("IsHiddenBySet(", body, StringComparison.Ordinal);
        Assert.Contains("RetiredAt", body, StringComparison.Ordinal);
    }

    // L18: the status line kept the work's name after a no-channels refusal the user had just read a
    // dialog about.
    [Fact]
    public void ANoChannelsRefusalClearsTheApplyingStatus()
    {
        var source = Source("MainWindow.ImportExport.cs");
        var refusal = source.Masked.IndexOf("bank.Entries.Count == 0", StringComparison.Ordinal);
        Assert.True(refusal >= 0, "the no-channels refusal is gone from the catalog-file import");
        var window = source.Masked[refusal..(source.Masked.IndexOf("return;", refusal, StringComparison.Ordinal) + 1)];
        Assert.Contains("SetStatus(", window, StringComparison.Ordinal);
    }

    // L19: the URL-import failure dialog was raised inside the catch, while the busy state was still set.
    [Fact]
    public void TheUrlImportFailureDialogIsRaisedAfterTheBusyStateClears()
    {
        var source = Source("MainWindow.ImportExport.cs");
        var start = source.Masked.IndexOf("private async Task ImportFromUrlAsync", StringComparison.Ordinal);
        Assert.True(start >= 0, "ImportFromUrlAsync is gone");
        var body = source.Masked[start..];
        var failureDialog = body.IndexOf("importFailureKey is { } failureKey", StringComparison.Ordinal);
        var busyCleared = body.IndexOf("SetBusy(false)", StringComparison.Ordinal);
        Assert.True(failureDialog >= 0, "the failure dialog is not carried past the catch");
        Assert.True(busyCleared >= 0 && busyCleared < failureDialog,
            "the busy state must clear before the failure dialog is raised");
    }

    // L20: the optional system media controls treated an access refusal or a torn-down session object as
    // a process-ending fault instead of an absent feature.
    [Fact]
    public void TheSystemMediaControlsTreatARefusalAsAbsent()
    {
        var body = Body(Source("SystemMediaControls.cs"), @"internal static SystemMediaControls\? TryCreate\(\)");
        Assert.Contains("UnauthorizedAccessException", body, StringComparison.Ordinal);
        Assert.Contains("ObjectDisposedException", body, StringComparison.Ordinal);
    }

    // L21: the audio channel-mode setter swallowed every exception with a bare catch and logged nothing.
    [Fact]
    public void TheChannelModeSetterLogsARefusal()
    {
        var body = Body(Source("StandardAudioPlayback.cs"), @"public AudioChannelMode AudioChannelMode");
        Assert.DoesNotContain("catch { }", body, StringComparison.Ordinal);
        Assert.Contains("catch (Exception exception)", body, StringComparison.Ordinal);
        Assert.Contains("_diagnostics?.Invoke(", body, StringComparison.Ordinal);
    }

    // L22: a faulting native stop skipped the release - RetireAsync caught only TimeoutException, so the
    // leg stayed held for the rest of the session and surfaced as an unobserved task fault.
    [Fact]
    public void AFaultedStopStillReleasesTheLeg()
    {
        var body = Body(Source("StandardAudioPlayback.cs"), @"private async Task RetireAsync\(Leg leg\)");
        var faultCatch = body.IndexOf("catch (Exception exception) when (exception is not OperationCanceledException)", StringComparison.Ordinal);
        var release = body.LastIndexOf("ReleaseContained(leg)", StringComparison.Ordinal);
        Assert.True(faultCatch >= 0, "RetireAsync no longer catches a faulted stop");
        Assert.True(faultCatch < release, "a stop that faulted must be recorded and still release the leg");
    }
}
