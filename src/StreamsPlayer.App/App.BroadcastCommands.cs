using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// STRWIN-0305: the first instance's end of <c>broadcast start</c> and <c>broadcast stop</c>. Both go through the broadcast
/// producer the Broadcast window uses, so the consent gate, the choice of network, the link password and "start with the
/// application" are the window's; <see cref="AppBroadcastCommands"/> decides every answer.
/// </summary>
public partial class App
{
    /// <summary>
    /// Starts the source <c>--source</c> names and waits until it is live or has failed, inside the verb's own bound. A
    /// source the window would not offer is refused before anything opens (B5).
    /// </summary>
    private async Task<AppCommandResponse> BroadcastStartForCommandAsync(
        string requestId, AppCommandParse parse, CancellationToken cancellationToken)
    {
        if (AppBroadcastCommands.SourceOf(parse.Options) is not { } source)
        {
            // The parser holds --source to the manifest's values; reaching here is a manifest and code that disagree.
            return AppCommandResponse.Refused(requestId, AppCommandReason.BadArgument, AppCommandExit.Usage,
                AppBroadcastMessages.StartOptionsOnce);
        }

        if (Broadcast.IsStarting(source))
        {
            return AppBroadcastCommands.StartUnderWay(requestId);
        }

        if (Broadcast.Running.Contains(source))
        {
            return Live(requestId, source, already: true);
        }

        if (AppBroadcastCommands.StartRefusal(requestId, Broadcast.RefusalFor(source)) is { } refused)
        {
            return refused;
        }

        // The producer ends a start the caller stopped waiting for itself and answers false (SP-0252).
        if (await Broadcast.StartAsync(source, cancellationToken, checkVideoEncoder: true))
        {
            return Live(requestId, source, already: false);
        }

        var verdict = Broadcast.StartVerdicts.FirstOrDefault(item => item.Source == source);
        return AppBroadcastCommands.StartFailed(requestId, verdict?.ReasonKey, cancellationToken.IsCancellationRequested);
    }

    /// <summary>
    /// Stops what the request names - a start still under way is cancelled, a live broadcast ended the way the window's
    /// Stop and Stop all end it, so the choice of "start with the application" is unmade too.
    /// </summary>
    private async Task<AppCommandResponse> BroadcastStopForCommandAsync(string requestId, AppCommandParse parse)
    {
        var starting = Enum.GetValues<BroadcastSource>().Where(Broadcast.IsStarting).ToArray();
        var running = Broadcast.Running;
        var plan = AppBroadcastCommands.PlanStop(requestId, AppBroadcastCommands.SourceOf(parse.Options),
            AppPlaybackCommands.IsTrue(parse.Options, "all"), [.. running.Union(starting)]);
        if (plan.Refusal is { } refusal)
        {
            return refusal;
        }

        foreach (var source in plan.Targets.Where(item => starting.Contains(item)))
        {
            Broadcast.CancelStart(source);
        }

        // A cancelled start may still be opening capture. The producer serializes stop with that work.
        if (plan.All && plan.Targets.Count > 0)
        {
            await Broadcast.StopAllAsync();
        }
        else
        {
            foreach (var source in plan.Targets)
            {
                await Broadcast.StopAsync(source);
            }
        }

        return AppBroadcastCommands.Stopped(requestId, plan.Targets.Count);
    }

    private AppCommandResponse Live(string requestId, BroadcastSource source, bool already)
    {
        var facts = Broadcast.LiveFacts(source);
        return AppBroadcastCommands.Started(requestId, source, already, facts?.LanAddress is not null, facts?.Server is not null);
    }
}
