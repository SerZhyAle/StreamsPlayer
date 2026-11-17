using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0274: <c>channel add</c>. The first instance writes the row - it is the one process that owns the catalog document
/// (SP-0118) - and the write is the Add stream dialog's save without the dialog: the same duplicate test, the same Manual
/// origin and the dialog's defaults. The stream is not played and the window is not brought forward.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// SP-0274: the first command of a window that a start verb started. The App sets it before the window is shown;
    /// <c>MainWindow_Loaded</c> runs it once the catalog is in memory.
    /// </summary>
    internal Func<Task>? StartupCommand { get; set; }

    /// <summary>
    /// The command's work on the UI thread: the dialog's guards, one save, the list and its facets brought up to date, one
    /// log line that names the outcome and never the address, and the answer.
    /// </summary>
    internal async Task<AppCommandResponse> ChannelAddForCommandAsync(string requestId, AppCommandParse parse, string? workingDirectory)
    {
        // The dialog's own guards (AddButton_Click): the catalog is not in memory yet, a refresh is running, the catalog could
        // not be read, or the window is closing. Busy is the answer a caller retries (rule 10).
        if (!_preferencesLoaded || _busy || _catalogStateUnreadable || _shuttingDown)
        {
            return AppCommandResponse.Busy(requestId, AppCommandMessages.Busy);
        }

        // SP-0279, item H: an address from --url-file is read off the UI thread, before the save's own gate is entered.
        var resolved = await AppUrlFile.ResolveAsync(requestId, parse.Options, workingDirectory, AppChannelMessages.AddressNotLaunchable);
        if (resolved.Refusal is { } unreadable)
        {
            return unreadable;
        }

        var url = AppPlaybackCommands.Last(resolved.Options, "url")!;
        var title = AppPlaybackCommands.Last(resolved.Options, "title");
        var collection = AppPlaybackCommands.Last(resolved.Options, "collection");
        AppChannelAddPlan? plan = null;
        var saved = await TryPersistAsync(state =>
        {
            // Decided inside the save's own gate, against the list as it is then, never against a copy read earlier.
            plan = AppChannelCommands.Plan(state.Channels, state.Collections, url, title, collection, Guid.NewGuid(), DateTimeOffset.UtcNow);
            return AppChannelCommands.Apply(state, plan);
        });
        if (!saved || plan is null)
        {
            return AppCommandResponse.Failed(requestId, AppCommandReason.IoError, AppCommandExit.Io, AppChannelMessages.NotSaved);
        }

        if (plan.Outcome != AppChannelAddOutcome.CollectionMissing)
        {
            PopulateFacets();
            ApplyFilter();
        }

        if (plan.Outcome == AppChannelAddOutcome.Added)
        {
            SetStatus("AddedStream", plan.Channel!.Title);
        }

        _log.Event("CHANNEL ADD",
            $"outcome={AppChannelCommands.OutcomeName(plan.Outcome)}",
            $"collection={(collection is null ? "none" : "named")}");
        return AppChannelCommands.Answer(requestId, plan);
    }
}
