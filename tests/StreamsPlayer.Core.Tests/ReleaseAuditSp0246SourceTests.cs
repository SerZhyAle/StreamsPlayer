using System.Text.RegularExpressions;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0246: the Low findings of the 26.1025.1927 release audit whose fixes live in WPF code this test project does not
/// load, pinned over the App's own sources read as text on the <see cref="ReleaseAuditLowFindingSourceTests"/> terms.
/// Each test names the finding it holds shut. A finding whose fix has behaviour a test can run is tested there instead
/// (Core.Tests and Exchange.Tests carry those next to the type).
/// </summary>
public sealed class ReleaseAuditSp0246SourceTests
{
    private static AppSourceFile Source(string fileName) => Assert.Single(AppSourceFile.LoadAll(fileName));

    /// <summary>The masked text of one member body (comments and string contents blanked), found by its declaration.</summary>
    private static string Body(AppSourceFile file, string declarationPattern) =>
        file.Masked[BodyRange(file, declarationPattern)];

    /// <summary>The same body as written, for an assertion about a string literal that masking blanks.</summary>
    private static string BodyText(AppSourceFile file, string declarationPattern) =>
        file.Text[BodyRange(file, declarationPattern)];

    private static Range BodyRange(AppSourceFile file, string declarationPattern)
    {
        var match = Regex.Match(file.Masked, declarationPattern);
        Assert.True(match.Success, $"{file.Name}: no declaration matches {declarationPattern}");

        var open = file.Masked.IndexOf('{', match.Index);
        var arrow = file.Masked.IndexOf("=>", match.Index, StringComparison.Ordinal);
        if (arrow >= 0 && (open < 0 || arrow < open))
        {
            return arrow..file.Masked.IndexOf(';', arrow);
        }

        var depth = 0;
        for (var position = open; position < file.Masked.Length; position++)
        {
            depth += file.Masked[position] switch { '{' => 1, '}' => -1, _ => 0 };
            if (depth == 0)
            {
                return open..(position + 1);
            }
        }

        throw new InvalidOperationException($"{file.Name}: unbalanced braces after {declarationPattern}");
    }

    private static int Count(string text, string needle) =>
        Regex.Matches(text, Regex.Escape(needle)).Count;

    [Fact]
    public void S4_1_AMediaThatFailsToBeCreatedStillHandsOverTheEndedRecordingSegment()
    {
        // The segment that ended is handed over in the finally; the Media must be created inside the try that has it.
        var text = Source("LibVlcVideoBackend.cs").Masked;
        Assert.Matches(@"Media\? next = null;\s*try\s*\{\s*next = new Media\(_libVlc, url\);", text);
        Assert.Contains("next is not null && !ReferenceEquals(_media, next)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void S4_2_ACaptureSessionThatWasCancelledIsNotRunningAndIsReplacedOnStart()
    {
        var file = Source("GridPreviewCoordinator.cs");
        Assert.Contains("IsRunning => _session is { IsCancellationRequested: false }", file.Masked, StringComparison.Ordinal);

        var start = Body(file, @"public async Task StartAsync\(\)");
        var stop = start.IndexOf("await StopSessionAsync()", StringComparison.Ordinal);
        var create = start.IndexOf("_session = new CancellationTokenSource()", StringComparison.Ordinal);
        Assert.True(stop >= 0 && create > stop, "a dead session must be torn down before the new one is created");
        Assert.DoesNotContain("_session is not null", start, StringComparison.Ordinal);
    }

    [Fact]
    public void S6_1_TheExitStepsShareOneCloseBudgetInsteadOfAddingTheirOwnWaits()
    {
        var body = Body(Source("App.xaml.cs"), @"private void CompleteExit\(\)");
        Assert.Equal(3, Count(body, "RemainingCloseTime"));
        Assert.DoesNotContain("TimeSpan.FromSeconds", body, StringComparison.Ordinal);
    }

    [Fact]
    public void S9_1_TurningTheMediaControlsOffKeepsThePausedStation()
    {
        var body = Body(Source("MainWindow.SystemMedia.cs"), @"private void ApplySystemMediaControlsSetting\(\)");
        Assert.DoesNotContain("_audioPausedChannelId = null", body, StringComparison.Ordinal);
        Assert.Contains("DisposeSystemMediaControls()", body, StringComparison.Ordinal);
    }

    [Fact]
    public void S9_3_TheDirectoryHandlersAreUnsubscribedBeforeTheLeaseIsDisposed()
    {
        var text = Source("MainWindow.MyStreams.cs").Masked;
        var dispose = text.IndexOf("lease.Dispose()", StringComparison.Ordinal);
        Assert.True(dispose > 0);
        var before = text[..dispose];
        Assert.True(before.LastIndexOf("Exchange.DirectoryChanged -= OnMyStreamsDirectoryChanged", StringComparison.Ordinal) > before.Length - 600);
        Assert.True(before.LastIndexOf("IncomingBroadcastsChanged -= OnMyStreamsIncomingChanged", StringComparison.Ordinal) > before.Length - 600);
    }

    [Fact]
    public void S10_3_ALanguageSwitchRerendersThePlaylistTitleAndTheBroadcastFailureText()
    {
        var refresh = Body(Source("PlayerWindow.xaml.cs"), @"internal void RefreshLocalization\(\)");
        Assert.Contains("ApplyPlaylistTitle()", refresh, StringComparison.Ordinal);
        Assert.Contains("RefreshBroadcastFailure()", refresh, StringComparison.Ordinal);

        var state = Source("PlayerWindow.BroadcastState.cs");
        var show = Body(state, @"private void ShowBroadcastFailure\(string reason\)");
        Assert.Contains("_broadcastFailureReason = reason", show, StringComparison.Ordinal);
        Assert.Contains("RefreshBroadcastFailure()", show, StringComparison.Ordinal);
    }

    [Fact]
    public void S10_4_AndS10_6_TheStartUpHandOverLogsASwallowedDeleteAndNamesEveryFolderItFilled()
    {
        var file = Source("RecordingStaging.cs");
        Assert.Contains("RecordingFinisher.MoveIntoFirst(file, audio ? audioChain ?? chain : chain, name, log: log)", file.Masked, StringComparison.Ordinal);
        Assert.Contains("var targetFolders = new HashSet<string>", file.Masked, StringComparison.Ordinal);
        Assert.Contains("string.Join(\", \", targetFolders)", file.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void S10_5_AStalePartialIsAgedByWhenItWasCreatedNotByTheSourcesLastWrite()
    {
        var body = Body(Source("RecordingFinisher.cs"), @"internal static void RemoveStalePartials");
        Assert.Contains("CreationTimeUtc", body, StringComparison.Ordinal);
        Assert.DoesNotContain("LastWriteTimeUtc", body, StringComparison.Ordinal);
    }

    [Fact]
    public void S11_1_ACollapsedSectionHeaderTooltipSaysExpand()
    {
        var xaml = Source("MainWindow.xaml").Text;
        foreach (var property in new[] { "PinnedSectionCollapsed", "MainSectionCollapsed" })
        {
            Assert.Matches(
                @"<DataTrigger Binding=""\{Binding " + property + @"\}"" Value=""True"">\s*<Setter Property=""ToolTip"" Value=""\{DynamicResource ExpandSectionTip\}"" />",
                xaml);
        }

        Assert.Equal(2, Count(xaml, "Value=\"{DynamicResource CollapseSectionTip}\""));
    }

    [Fact]
    public void S11_2_AddRefusesAnUnreadableStateABusyWindowAndAShutdownBeforeAndAfterTheDialog()
    {
        var body = Body(Source("MainWindow.xaml.cs"), @"private async void AddButton_Click\(");
        Assert.Equal(2, Count(body, "_busy || _catalogStateUnreadable || _shuttingDown"));
        Assert.True(body.IndexOf("_busy || _catalogStateUnreadable", StringComparison.Ordinal) <
                    body.IndexOf("new AddStreamWindow", StringComparison.Ordinal));
    }

    [Fact]
    public void S11_3_OpenInNewWindowTakesTheSameRouteAsPlayIncludingTheBroadcastPreFlight()
    {
        var body = Body(Source("MainWindow.xaml.cs"), @"private async void OpenNewWindowMenuItem_Click\(");
        Assert.Contains("PlayChannelAsync(row.Channel", body, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenIndependentPlayerWindow(", body, StringComparison.Ordinal);
    }

    [Fact]
    public void S11_5_EveryStartUpStepRunsBehindItsOwnBoundary()
    {
        var text = Source("MainWindow.xaml.cs").Masked;
        foreach (var step in new[] { "StartRequestedPlaybackAsync", "HandOverStagedRecordingsAsync", "AskWhereChannelsComeFromAsync", "OfferCatalogSnapshotAsync" })
        {
            Assert.Contains($"RunStartupStepAsync(nameof({step})", text, StringComparison.Ordinal);
            Assert.DoesNotMatch(@"await\s+" + step + @"\(\)", text);
        }
    }

    [Fact]
    public void S12_5_ARowIsEndedByAbsenceFromTheDriveSnapshotOnlyWhenTheServerDirectoryIsKnown()
    {
        var text = Source("MainWindow.Drive.cs").Masked;
        var endable = text.IndexOf("var endable = server is null", StringComparison.Ordinal);
        var ended = text.IndexOf("MarkEnded(endable", StringComparison.Ordinal);
        Assert.True(endable > 0 && ended > endable);
        Assert.Contains("DirectoryEntryIdentity == identity", text[endable..ended], StringComparison.Ordinal);
    }

    [Fact]
    public void S12_6_TheSignedOutNoticeIsOwnedByTheVisibleWindowAndSkippedOnceTheApplicationIsClosing()
    {
        var text = Source("MainWindow.Exchange.cs").Masked;
        var notice = text.IndexOf("new ExchangeFailureWindow(ExchangeFailureRows.SignedOut)", StringComparison.Ordinal);
        Assert.True(notice > 0);
        var around = text[Math.Max(0, notice - 200)..(notice + 200)];
        Assert.Contains("_shuttingDown", around, StringComparison.Ordinal);
        Assert.Contains("Owner = DialogOwner", around, StringComparison.Ordinal);
    }

    [Fact]
    public void S12_7_ACastRowCarriesTheDirectoryIdentityOfTheEntryThatReceivedIt()
    {
        var text = Source("MainWindow.Exchange.cs").Masked;
        var apply = text.IndexOf("FastMediaSorterBroadcastImport.Apply(", StringComparison.Ordinal);
        Assert.True(apply > 0);
        Assert.Contains("directoryEntryIdentity: ExchangeAccountIdentity.Key(", text[apply..(apply + 400)], StringComparison.Ordinal);
    }

    [Fact]
    public void S12_8_AGuestDirectoryFrameDroppedWhileBusyIsKeptAndReplayedOnRelease()
    {
        var apply = Body(Source("MainWindow.ExchangeBindings.cs"), @"internal async Task ApplyGuestDirectoryAsync\(");
        Assert.Contains("_pendingGuestDirectories[identity] = (entry, snapshot)", apply, StringComparison.Ordinal);

        var busy = Body(Source("MainWindow.xaml.cs"), @"private void SetBusy\(bool busy, bool cancellable = false\)");
        Assert.Contains("!busy && _pendingGuestDirectories.Count > 0", busy, StringComparison.Ordinal);
        Assert.Contains("ApplyPendingGuestDirectoriesAsync", busy, StringComparison.Ordinal);
    }

    [Fact]
    public void S13_4_BothTunnelLegsAdmitOnlyTheirOwnProcessToTheLoopbackListener()
    {
        foreach (var file in new[] { "BroadcastVideoRoute.cs", "FastMediaSorterAudioLeg.cs" })
        {
            var text = Source(file).Masked;
            var start = text.IndexOf("ExchangeTunnelForwarder.Start(", StringComparison.Ordinal);
            Assert.True(start > 0, file);
            Assert.Contains("admitPeer: LoopbackPeerOwner.IsThisProcess", text[start..(start + 300)], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void S15_4_ALanguageSwitchRepopulatesTheDriveAndBroadcastParts()
    {
        var body = Body(Source("SettingsWindow.xaml.cs"), @"internal void ReapplyLocalization\(\)");
        foreach (var call in new[] { "ShowDrive()", "PopulateBroadcastQuality()", "PopulateBroadcastLink()", "ShowBroadcasting()" })
        {
            Assert.Contains(call, body, StringComparison.Ordinal);
        }

        // SP-0252: the choices of the sources moved to the broadcast window, which refills them in the new language too.
        var window = Body(Source("BroadcastWindow.xaml.cs"), @"internal void RefreshLocalization()");
        Assert.Contains("InitializeChoices()", window, StringComparison.Ordinal);
        Assert.Contains("Refresh()", window, StringComparison.Ordinal);
    }

    [Fact]
    public void S15_5_TheLanPortIsAppliedWhenTheEditIsFinishedAndNotOnEveryKeystroke()
    {
        var xaml = Source("SettingsWindow.xaml").Text;
        var box = Regex.Match(xaml, @"<TextBox x:Name=""BroadcastLanPortBox""[^>]*>").Value;
        Assert.Contains("LostFocus=\"BroadcastLanPort_LostFocus\"", box, StringComparison.Ordinal);
        Assert.Contains("KeyDown=\"BroadcastLanPort_KeyDown\"", box, StringComparison.Ordinal);
        Assert.DoesNotContain("TextChanged", box, StringComparison.Ordinal);

        var closing = Body(Source("SettingsWindow.xaml.cs"), @"protected override void OnClosing\(CancelEventArgs e\)");
        Assert.Contains("CommitBroadcastLanPort()", closing, StringComparison.Ordinal);
    }

    [Fact]
    public void R4_1_OpeningSettingsNeverWritesASubstituteForADeviceThatIsMerelyUnplugged()
    {
        foreach (var (file, method) in new[]
        {
            // SP-0252: the lists moved from Settings to the broadcast window; the rule moved with them.
            ("BroadcastWindow.Devices.cs", "private static (IReadOnlyList<BroadcastDeviceChoice> Choices, int Index, bool Any) Devices"),
            ("BroadcastWindow.Devices.cs", "private void FillMicrophones")
        })
        {
            var source = Source(file);
            Assert.DoesNotContain("UpdateSettingsAsync", Body(source, Regex.Escape(method)), StringComparison.Ordinal);
            Assert.Contains("BroadcastDeviceMissing", BodyText(source, Regex.Escape(method)), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void X1_TheAacEncoderUnlocksTheOutputBufferItLocked()
    {
        var text = Source("WindowsAacEncoder.cs").Masked;
        var lockAt = text.IndexOf("buffer.Lock(out IntPtr data", StringComparison.Ordinal);
        var unlockAt = text.IndexOf("buffer.Unlock()", lockAt, StringComparison.Ordinal);
        Assert.True(lockAt > 0 && unlockAt > lockAt);
        Assert.Contains("finally", text[lockAt..unlockAt], StringComparison.Ordinal);
    }

    [Fact]
    public void X2_TheBroadcastAudioRouteOfTheMainWindowIsWatchedForAStall()
    {
        var playback = Source("MainWindow.FastMediaSorterPlayback.cs");
        Assert.Contains("WatchForFastMediaSorterAudioStall(playback", playback.Masked, StringComparison.Ordinal);
        var stop = Body(playback, @"private void StopFastMediaSorterAudioPlayback\(\)");
        Assert.Contains("StopWatchingAudioStall()", stop, StringComparison.Ordinal);

        var watchdog = Source("MainWindow.AudioStallWatchdog.cs");
        var handler = BodyText(watchdog, @"private async Task HandleFastMediaSorterAudioStallAsync\(");
        Assert.Contains("RecoverAudioAsync(row.Channel, \"stall\"", handler, StringComparison.Ordinal);
    }
}
