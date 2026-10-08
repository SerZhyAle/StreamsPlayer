using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0075: the TV schedule - what is on now and next on a TV channel, from an XMLTV address the user
/// supplies.
///
/// <para>The network is touched in exactly one place, <see cref="DownloadTvScheduleAsync"/>, reached only
/// from the Tools window's button after a confirmation that names the host (APP-BEHAVIOUR rule 4). Startup
/// reads the local file and nothing else. When the stored data runs out the status line suggests an
/// update; it never fetches one.</para>
///
/// <para>The schedule and the user's bindings live in their own files, so no path here can alter a catalog
/// row: a refresh, the merge and the MANUAL/IMPORTED protection are untouched.</para>
/// </summary>
public partial class MainWindow
{
    // Minute-level programme changes need no finer clock; the tick is local work only - no I/O.
    private static readonly TimeSpan TvScheduleTickInterval = TimeSpan.FromSeconds(30);

    private readonly TvScheduleStore _tvScheduleStore = new(AppPaths.DataDirectory);
    private TvScheduleIndex _tvSchedule = TvScheduleIndex.Empty;
    // The catalog list the index was built against. A refresh, an import or an edit replaces the list, and
    // the next tick rebuilds the name binding for it - no hook in every place that changes the catalog.
    private IReadOnlyList<StreamChannel>? _tvScheduleCatalog;
    private DispatcherTimer? _tvScheduleTimer;
    private bool _tvScheduleRunningOutShown;
    // SP-0171: the rows that show a "now" line. The tick walks this set, not _rowCache.
    private readonly TvScheduleLines<ChannelRow> _tvScheduleLines = new();

    /// <summary>The current schedule, for the Tools window's status line.</summary>
    internal TvScheduleIndex TvSchedule => _tvSchedule;

    /// <summary>Reads what an earlier explicit download left on disk. Never touches the network.</summary>
    private async Task LoadTvScheduleAsync()
    {
        var document = await _tvScheduleStore.LoadScheduleAsync();
        var bindings = await _tvScheduleStore.LoadBindingsAsync();
        ApplyTvSchedule(document, bindings);
        if (document is not null)
        {
            _log.Event("TV SCHEDULE", "op=load", $"channels={document.Channels.Count}",
                $"matched={_tvSchedule.MatchedCount}/{_tvSchedule.EligibleCount}",
                $"bindings={bindings.Count}", $"coverage_end={_tvSchedule.CoverageEnd:O}");
        }
    }

    private void ApplyTvSchedule(TvScheduleDocument? document, IEnumerable<TvScheduleBinding> bindings)
    {
        _tvScheduleCatalog = _state.Channels;
        _tvSchedule = TvScheduleIndex.Build(document, bindings, _tvScheduleCatalog);
        _tvScheduleRunningOutShown = false;
        if (_tvSchedule.HasSchedule)
        {
            _tvScheduleTimer ??= CreateTvScheduleTimer();
            _tvScheduleTimer.Start();
        }
        else
        {
            _tvScheduleTimer?.Stop();
        }

        ReconcileTvScheduleLines();
        SuggestTvScheduleUpdate();
    }

    private DispatcherTimer CreateTvScheduleTimer()
    {
        var timer = new DispatcherTimer { Interval = TvScheduleTickInterval };
        timer.Tick += (_, _) => HandlerBoundary.Run("TvScheduleTimer.Tick", () =>
        {
            if (!ReferenceEquals(_tvScheduleCatalog, _state.Channels))
            {
                _tvScheduleCatalog = _state.Channels;
                _tvSchedule = TvScheduleIndex.Build(_tvSchedule.Document, _tvSchedule.Bindings.Values, _tvScheduleCatalog);
                ReconcileTvScheduleLines();
            }
            else
            {
                // SP-0171: only the rows bound to a guide, and no address parsed - not every cached row.
                _tvScheduleLines.Tick(DateTimeOffset.Now, ShowScheduleNow);
            }

            SuggestTvScheduleUpdate();
            return Task.CompletedTask;
        });
        Closed += (_, _) => timer.Stop();
        return timer;
    }

    private static void ShowScheduleNow(ChannelRow row, string? title) => row.SetScheduleNow(title);

    /// <summary>
    /// Binds one row against the current index: on creation, and when an edit gives the row a new address.
    /// </summary>
    private void AttachScheduleLine(ChannelRow row) =>
        _tvScheduleLines.Attach(row, row.Channel.Url, _tvSchedule, DateTimeOffset.Now, ShowScheduleNow);

    /// <summary>
    /// SP-0171: the one bulk pass - after a new schedule, a binding edit or a changed catalog list. The
    /// clock never triggers it. Timed into the log because it is the part that still follows catalog size.
    /// </summary>
    private void ReconcileTvScheduleLines()
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        _tvScheduleLines.Reconcile(_rowCache.Values, row => row.Channel.Url, _tvSchedule, DateTimeOffset.Now, ShowScheduleNow);
        if (_tvSchedule.HasSchedule)
        {
            _log.Event("TV SCHEDULE", "op=reconcile", $"rows={_rowCache.Count}",
                $"bound={_tvScheduleLines.BoundCount}", $"ms={timer.ElapsedMilliseconds}");
        }
    }

    private void SuggestTvScheduleUpdate()
    {
        // A suggestion, once per schedule, and only on an idle status line: the running operation owns it
        // while one is running. Nothing is downloaded because of it.
        if (!_busy && !_tvScheduleRunningOutShown && _tvSchedule.IsRunningOut(DateTimeOffset.Now))
        {
            _tvScheduleRunningOutShown = true;
            _log.Event("TV SCHEDULE", "op=running_out", $"coverage_end={_tvSchedule.CoverageEnd:O}");
            SetStatus("TvScheduleRunningOut");
        }
    }

    /// <summary>Hands a player window its channel's now/next lookup against whatever schedule is current.</summary>
    private void AttachTvSchedule(PlayerWindow window, StreamChannel channel) =>
        window.AttachTvSchedule(now => _tvSchedule.NowAndNext(channel.Url, now));

    private async Task DownloadTvScheduleAsync(Window owner)
    {
        var address = (owner as SettingsWindow)?.TvScheduleAddress;
        if (!TvScheduleService.TryParseSource(address, out var source))
        {
            MessageBox.Show(owner, LocalizationService.Get("TvScheduleInvalidAddress"),
                LocalizationService.Get("TvScheduleTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var answer = MessageBox.Show(owner, LocalizationService.Format("TvScheduleConfirm", source.Host),
            LocalizationService.Get("TvScheduleTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question,
            MessageBoxResult.No);
        _log.Event("TV SCHEDULE", "op=consent", $"host={source.Host}",
            $"result={(answer == MessageBoxResult.Yes ? "accepted" : "declined")}");
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        // The download reports on the main window's bar, where its Cancel button lives (APP-BEHAVIOUR
        // rule 3). Left open, the modal Settings window would sit over that button for the whole transfer.
        // SP-0161 / SP-0188: through CloseForRunningAction, so the close is the action's own and the running flag
        // lets it through; every dialog this action shows later is owned by the main window.
        (owner as SettingsWindow)?.CloseForRunningAction();
        await RunTvScheduleDownloadAsync(source);
    }

    private async Task RunTvScheduleDownloadAsync(Uri source)
    {
        _cancellableOperation = new CancellationTokenSource();
        _reportingProgress = true;
        SetBusy(true, cancellable: true);
        SetStatus("TvScheduleDownloadProgressUnknown", 0);
        try
        {
            var progress = OnDispatcher<DownloadProgress>(report => ShowDownloadProgress(
                report, "TvScheduleDownloadProgress", "TvScheduleDownloadProgressUnknown", "TvScheduleReading"));
            // S6-3: the service follows redirects itself, and only on the host the user agreed to - the
            // shared catalog client would follow one to any host. Timeout and agent as that client has them.
            using var scheduleClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
            scheduleClient.DefaultRequestHeaders.UserAgent.ParseAdd("StreamsPlayer/0.1");
            var document = await new TvScheduleService(scheduleClient)
                .DownloadAsync(source, DateTimeOffset.Now, progress, _cancellableOperation.Token);
            _reportingProgress = false;
            // SP-0161: the transfer is over, so the run can no longer be stopped - the Cancel button goes
            // dark for the read and the save instead of swallowing a click.
            EndCancellablePhase();
            var programmes = document.Channels.Sum(channel => channel.Programmes.Count);
            _log.Event("TV SCHEDULE", "op=download", "ok=true", $"host={source.Host}",
                $"channels={document.Channels.Count}", $"programmes={programmes}");

            // A guide with nothing ahead would replace a working schedule with an empty one.
            if (programmes == 0)
            {
                SetStatus("TvScheduleEmpty");
                return;
            }

            if (!await _tvScheduleStore.SaveScheduleAsync(document))
            {
                _log.Event("TV SCHEDULE", "op=save", "ok=false");
                SetStatus("TvScheduleSaveFailed");
                return;
            }

            ApplyTvSchedule(document, _tvSchedule.Bindings.Values.ToList());
            SetStatus("TvScheduleDone", _tvSchedule.MatchedCount, _tvSchedule.EligibleCount);
        }
        // Abandoning is not failing, so this precedes the general catch; the previous schedule stays.
        catch (OperationCanceledException) when (_cancellableOperation?.IsCancellationRequested == true)
        {
            _log.Event("CANCEL", "op=tv_schedule");
            SetStatus("TvScheduleCancelled");
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidDataException
            or TimeoutException or OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            _log.Event("TV SCHEDULE", "op=download", "ok=false", $"host={source.Host}",
                $"err={exception.GetType().Name}", $"msg={exception.Message}");
            SetStatus("TvScheduleFailed", FailureCauseText.Describe(exception));
        }
        finally
        {
            _reportingProgress = false;
            SetBusy(false);
            _cancellableOperation?.Dispose();
            _cancellableOperation = null;
        }
    }

    private Task RemoveTvScheduleAsync(Window owner)
    {
        if (!_tvScheduleStore.HasAnyFile)
        {
            MessageBox.Show(owner, LocalizationService.Get("TvScheduleRemoveNothing"),
                LocalizationService.Get("TvScheduleTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            return Task.CompletedTask;
        }

        if (MessageBox.Show(owner, LocalizationService.Get("TvScheduleRemoveConfirm"),
                LocalizationService.Get("TvScheduleTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return Task.CompletedTask;
        }

        try
        {
            var removed = _tvScheduleStore.Delete();
            _log.Event("TV SCHEDULE", "op=remove", "ok=true", $"files={removed}");
            ApplyTvSchedule(null, []);
            SetStatus("TvScheduleRemoved");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log.Event("TV SCHEDULE", "op=remove", "ok=false", $"err={exception.GetType().Name}", $"msg={exception.Message}");
            MessageBox.Show(owner, LocalizationService.Format("TvScheduleRemoveFailed", FailureCauseText.Describe(exception)),
                LocalizationService.Get("TvScheduleTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        return Task.CompletedTask;
    }

    /// <summary>The channel menu's entry for binding by hand; absent while there is no schedule to bind to.</summary>
    private MenuItem? BuildTvScheduleMenuItem(ChannelRow row)
    {
        if (!_tvSchedule.HasSchedule)
        {
            return null;
        }

        var item = new MenuItem { Header = LocalizationService.Get("MenuTvScheduleBinding"), Tag = row };
        item.Click += (_, _) => HandlerBoundary.Run("TvScheduleBindingMenuItem.Click", () => EditTvScheduleBindingAsync(row.Channel));
        return item;
    }

    private async Task EditTvScheduleBindingAsync(StreamChannel channel)
    {
        var dialog = new TvScheduleBindingWindow(
            StreamTitleFormatter.Display(channel.Title),
            _tvSchedule.Channels,
            _tvSchedule.ChannelFor(channel.Url)?.Id) { Owner = DialogOwner };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var key = CatalogUrlIdentity.Normalize(channel.Url);
        var bindings = _tvSchedule.Bindings.Values
            .Where(binding => !string.Equals(CatalogUrlIdentity.Normalize(binding.Url), key, StringComparison.Ordinal))
            .ToList();
        if (dialog.Choice != TvScheduleBindingChoice.Automatic)
        {
            bindings.Add(new TvScheduleBinding(channel.Url, dialog.SelectedChannelId));
        }

        if (!await _tvScheduleStore.SaveBindingsAsync(bindings))
        {
            _log.Event("TV SCHEDULE", "op=bind", "ok=false");
            SetStatus("TvScheduleSaveFailed");
            return;
        }

        _log.Event("TV SCHEDULE", "op=bind", "ok=true", $"choice={dialog.Choice}",
            $"url={CatalogUrlIdentity.Redact(channel.Url)}");
        ApplyTvSchedule(_tvSchedule.Document, bindings);
    }
}
