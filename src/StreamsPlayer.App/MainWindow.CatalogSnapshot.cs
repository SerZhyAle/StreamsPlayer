using System.IO;
using System.Windows;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0052: the bundled catalog snapshot. Three entry points - the first launch, a failed or refused
/// catalog update, and the settings - all lead to the same operation, and all three are refusable. The
/// snapshot is never applied without the user asking for it in that moment.
/// </summary>
public partial class MainWindow
{
    private bool _applyingCatalogSnapshot;

    /// <summary>
    /// The one first-launch offer. Eligible only while the catalog has never been downloaded and holds
    /// no catalog rows at all: a user who already has a list does not need a seed, and the settings
    /// action covers every other moment.
    /// </summary>
    private bool CatalogSnapshotOfferEligible =>
        _preferencesLoaded &&
        BundledCatalogSnapshot.Exists &&
        !_applyingCatalogSnapshot &&
        _state.LastCatalogRefreshAt is null &&
        _state.AppliedSnapshotDate is null &&
        !_state.CatalogSnapshotOfferDeclined &&
        !_state.Channels.Any(channel => channel.SourceOrigin == SourceOrigin.Catalog);

    /// <summary>
    /// SP-0088: a modal question where an inline bar used to be. Nothing is applied unless the user
    /// says yes, and the eligibility rule above is unchanged - this still appears at most once in the
    /// product's life, which is why "no" is persisted rather than latched.
    /// </summary>
    private async Task OfferCatalogSnapshotAsync()
    {
        if (!CatalogSnapshotOfferEligible)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"{LocalizationService.Get("CatalogSnapshotOffer")}{Environment.NewLine}{Environment.NewLine}" +
                LocalizationService.Get("CatalogSnapshotOfferTip"),
            LocalizationService.Get("CatalogSnapshotTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
        {
            await ApplyBundledSnapshotAsync(this);
            return;
        }

        // The settings action is the way back for a user who changes their mind.
        _state = await PersistAsync(_state with { CatalogSnapshotOfferDeclined = true });
        _log.Event("CATALOG SNAPSHOT", "op=offer", "result=declined");
    }

    /// <summary>
    /// SP-0059: the one question a clean install asks. It replaces the inline offer above rather than
    /// joining it - where this appears, that bar does not, and the built-in copy is this dialog's
    /// second button instead. Every branch is the user's own press; nothing is downloaded or applied
    /// on the way in or on the way out.
    /// </summary>
    private async Task AskWhereChannelsComeFromAsync()
    {
        // ContextIdle sits below layout, render and input, so this resumes only once the main window has
        // nothing left to draw. Loaded's tail is otherwise early enough for the dialog to centre itself
        // on a window the user has not seen yet.
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);

        var dialog = new FirstRunCatalogWindow(BundledCatalogSnapshot.Exists) { Owner = this };
        dialog.ShowDialog();
        _log.Event("FIRST RUN", "op=channel_source", $"result={dialog.Choice}",
            $"bundled={BundledCatalogSnapshot.Exists}");

        switch (dialog.Choice)
        {
            case FirstRunCatalogChoice.Internet:
                await RefreshCatalogAsync();
                break;
            case FirstRunCatalogChoice.Bundled:
                await ApplyBundledSnapshotAsync(this);
                break;
            default:
                // The built-in copy was this dialog's second button, so refusing the dialog refuses it
                // too. Without this write the inline bar would re-offer on the next launch exactly what
                // the user just declined.
                _state = await PersistAsync(_state with { CatalogSnapshotOfferDeclined = true });
                break;
        }
    }

    /// <summary>
    /// Offered instead of a bare transport error when an update cannot happen. Returns without applying
    /// anything unless the user says yes.
    /// </summary>
    private async Task OfferSnapshotAfterFailedRefreshAsync(string reason)
    {
        if (!BundledCatalogSnapshot.Exists)
        {
            MessageBox.Show(this, reason, LocalizationService.Get("CatalogUpdateFailedTitle"),
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var answer = MessageBox.Show(
            this,
            LocalizationService.Format("CatalogSnapshotAfterFailure", reason),
            LocalizationService.Get("CatalogUpdateFailedTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        await ApplyBundledSnapshotAsync(this);
    }

    /// <summary>
    /// The single operation behind all three entry points. Core reads, merges and persists; this method
    /// only reports, refreshes the view, and keeps the failure visible without touching the catalog.
    /// </summary>
    private async Task ApplyBundledSnapshotAsync(Window owner)
    {
        if (!_preferencesLoaded || _applyingCatalogSnapshot)
        {
            return;
        }

        if (!BundledCatalogSnapshot.Exists)
        {
            MessageBox.Show(owner, LocalizationService.Get("CatalogSnapshotUnavailable"),
                LocalizationService.Get("CatalogSnapshotTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _applyingCatalogSnapshot = true;
        SetBusy(true);
        SetStatus("CatalogSnapshotWorking");
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        try
        {
            // Not routed through PersistAsync: that helper saves state alone, and the snapshot has to
            // install its atlas in the same write. The Core service is the only writer here, exactly as
            // it is for an online refresh.
            var result = await new CatalogSnapshotService(_store).ApplyBundledAsync(_state);
            _state = result.State;
            _log.Event("CATALOG SNAPSHOT", $"added={result.Added}", $"updated={result.Updated}",
                $"date={result.SourceDate:yyyy-MM-dd}");
            PopulateFacets();
            ApplyFilter();
            // Carries the snapshot's date and the fact that no download has happened, so the outcome
            // line itself never lets bundled data pass for a fresh catalog.
            SetStatus("CatalogSnapshotResult", result.Added, result.Updated, result.SourceDate.ToLocalTime());
        }
        // The payload is read before anything is written, so a damaged one leaves the stored catalog
        // exactly as it was; an IO failure comes from the state folder and is reported the same way.
        catch (Exception exception) when (exception is InvalidDataException or IOException
            or UnauthorizedAccessException)
        {
            _log.Error("Bundled catalog snapshot could not be applied", exception);
            SetStatus("CatalogSnapshotFailed");
            MessageBox.Show(owner, exception.Message, LocalizationService.Get("CatalogSnapshotTitle"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _applyingCatalogSnapshot = false;
            SetBusy(false);
        }
    }

    /// <summary>
    /// The catalog's provenance line. A snapshot never sets <c>LastCatalogRefreshAt</c>, so a list that
    /// came from one says so, names the snapshot's date, and keeps stating that no download has happened.
    /// </summary>
    private void SetCatalogStatus()
    {
        if (_state.LastCatalogRefreshAt is DateTimeOffset refreshed)
        {
            SetStatus("MainLastUpdated", refreshed.ToLocalTime());
        }
        else if (_state.AppliedSnapshotDate is DateTimeOffset snapshotDate)
        {
            SetStatus("MainSnapshotApplied", snapshotDate.ToLocalTime());
        }
        else
        {
            SetStatus("MainReadyNoRefresh");
        }
    }
}
