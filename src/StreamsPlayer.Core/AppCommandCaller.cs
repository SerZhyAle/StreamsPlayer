using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0275: the second process's side of <c>APP-COMMANDS</c> rule 6, as far as it does not start a window - the answer
/// mapping of a forwarded command, the wait for a close, the answers of a verb with nothing to do, the version answer, the
/// list of commands, and the whole run of a verb that never becomes the first instance. Extracted from the player's entry
/// point so its console companion answers every command exactly as the player's own second process does, and one test
/// suite holds both.
/// </summary>
public static class AppCommandCaller
{
    /// <summary>A fresh request id (the GUID the response repeats, rule 7).</summary>
    public static string NewRequestId() => Guid.NewGuid().ToString("D");

    /// <summary>The request a second process forwards: the command, its words as typed, and who and where it came from.</summary>
    public static AppCommandRequest NewRequest(AppCommandEntry entry, IReadOnlyList<string> args, string requestId) =>
        new(entry.Name, args, requestId)
        {
            WorkingDir = Environment.CurrentDirectory,
            CallerPid = Environment.ProcessId
        };

    /// <summary>
    /// What a delivery came to. <paramref name="olderFirstInstance"/> is the caller's answer to a running copy that is older
    /// than the profile (it replied with its plain line, or hung up with no line at all - item K): the player re-sends a frozen
    /// alias once first, the companion has none. Silence until the deadline is not that evidence and is a timeout; an answer
    /// whose request id is empty or foreign (item I.3) is not proven either and never carries its own exit code.
    /// </summary>
    public static AppCommandResponse Interpret(
        AppCommandExchange exchange,
        SingleInstanceLockResult lockResult,
        string requestId,
        Func<AppCommandResponse> olderFirstInstance)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        ArgumentNullException.ThrowIfNull(olderFirstInstance);

        return exchange.Delivery switch
        {
            AppCommandDelivery.Answered => exchange.Response!,
            AppCommandDelivery.LegacyReply or AppCommandDelivery.Closed => olderFirstInstance(),
            AppCommandDelivery.Unreadable => AppCommandResponse.Failed(
                requestId, AppCommandReason.Timeout, AppCommandExit.NotProven, AppCommandMessages.UnreadableAnswer),
            AppCommandDelivery.NotConnected when lockResult == SingleInstanceLockResult.Inaccessible =>
                AppCommandResponse.Refused(requestId, AppCommandReason.Elevation, AppCommandExit.Permission, AppCommandMessages.Elevated),
            AppCommandDelivery.NotConnected => AppCommandResponse.Busy(requestId, AppCommandMessages.NoAnswer),
            _ => AppCommandResponse.Failed(requestId, AppCommandReason.Timeout, AppCommandExit.NotProven, AppCommandMessages.Timeout)
        };
    }

    /// <summary>
    /// The research proposal of 2026-11-14, item 4: a running copy older than the profile answered with its plain line. This
    /// process saw no end, so the exit is 2, and the sentence says the running copy must be restarted.
    /// </summary>
    public static AppCommandResponse OlderInstance(string requestId) =>
        AppCommandResponse.Refused(requestId, AppCommandReason.Schema, AppCommandExit.NotProven, AppCommandMessages.OlderInstance);

    /// <summary>
    /// SP-0271 Assumed 7: the running copy said its close has begun; it is closed when its lock is free. The lock is released
    /// again at once - this process is not becoming the first instance.
    /// </summary>
    public static AppCommandResponse AwaitClose(SingleInstanceIdentity identity, string requestId, TimeSpan remaining)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var wait = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        if (SingleInstanceLock.TryAcquire(identity.MutexName, wait, out var closed) == SingleInstanceLockResult.Acquired)
        {
            closed!.Dispose();
            return AppCommandResponse.Done(requestId, AppCommandMessages.Closed);
        }

        return AppCommandResponse.Failed(requestId, AppCommandReason.Timeout, AppCommandExit.NotProven, AppCommandMessages.StillClosing);
    }

    /// <summary>
    /// The work of a <c>oneshot</c> verb with nothing running. <c>quit</c> and <c>playback stop</c> have nothing to do:
    /// nothing playing is the goal state. Any other verb is a <c>refuse</c> verb's answer.
    /// </summary>
    public static AppCommandResponse Oneshot(string requestId, AppCommandEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return entry.Name switch
        {
            "quit" => AppCommandResponse.Done(requestId, AppCommandMessages.NothingToQuit),
            AppPlaybackCommands.StopVerb => AppCommandResponse.Done(requestId, AppPlaybackMessages.NothingPlaying,
                new JsonObject { ["stopped"] = 0 }),
            _ => AppCommandPolicy.NotRunning(requestId)
        };
    }

    /// <summary><c>version</c>: the product's name, its stamp and the command set's version (rule 17).</summary>
    public static AppCommandResponse VersionResponse(string requestId, AppCommandManifest manifest, string version)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        return AppCommandResponse.Done(requestId, AppCommandMessages.Version(version, manifest.CliVersion), new JsonObject
        {
            ["product"] = manifest.Product,
            ["version"] = version,
            ["cliVersion"] = manifest.CliVersion
        });
    }

    /// <summary>
    /// <c>commands</c> without <c>--json</c>: one readable line per command. <paramref name="executable"/> is the program the
    /// reader is running, so the last line points at a command that program can run.
    /// </summary>
    public static string CommandList(AppCommandManifest manifest, string executable)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var text = new StringBuilder();
        text.Append(AppCommandMessages.Product).Append(" commands (command set ").Append(manifest.CliVersion).AppendLine("):");
        var width = manifest.Commands.Max(command => command.Name.Length) + 2;
        foreach (var command in manifest.Commands)
        {
            text.Append("  ").Append(command.Name.PadRight(width)).AppendLine(command.Summary);
        }

        text.Append("Run \"").Append(executable).Append(" commands --json\" for the manifest itself.");
        return text.ToString();
    }

    /// <summary>
    /// The whole run of a verb whose second process never becomes the first instance - what the console companion does for
    /// every command it serves. With no copy running the verb's cold start decides (rule 10: <c>oneshot</c> does its nothing,
    /// <c>refuse</c> answers 11); with one running the request is forwarded and the answer is waited for inside the verb's
    /// own bound (rule 8). A <c>start</c> verb is refused by the caller before it gets here; reaching here it is answered as
    /// <c>refuse</c>, never as a start this process cannot make.
    /// </summary>
    public static AppCommandResponse Run(
        AppCommandEntry entry, AppCommandParse parse, SingleInstanceIdentity identity, string requestId)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(parse);
        ArgumentNullException.ThrowIfNull(identity);

        var lockResult = SingleInstanceLock.TryAcquire(identity.MutexName, TimeSpan.Zero, out var held);
        if (lockResult == SingleInstanceLockResult.Acquired)
        {
            // Nothing runs. Released first, so a copy the person starts meanwhile is the first instance (rule 10).
            held!.Dispose();
            return AppCommandPolicy.ColdStartOf(entry) == AppCommandColdStart.Oneshot
                ? Oneshot(requestId, entry)
                : AppCommandPolicy.NotRunning(requestId);
        }

        var request = NewRequest(entry, parse.ArgumentsAfterVerb, requestId);
        if (request.Serialize().Length - 1 > AppCommandLimits.MaximumRequestBytes)
        {
            return AppCommandResponse.Refused(requestId, AppCommandReason.TooLarge, AppCommandExit.Usage, AppCommandMessages.TooLarge);
        }

        var bound = AppCommandPolicy.CompletionBound(entry);
        var clock = Stopwatch.StartNew();
        var exchange = AppCommandClient.Send(identity.PipeName, request, AppCommandClient.DeliveryBound, bound);
        var response = Interpret(exchange, lockResult, requestId, () => OlderInstance(requestId));
        return entry.Name == "quit" && response.Status == AppCommandStatus.Done
            ? AwaitClose(identity, requestId, bound - clock.Elapsed)
            : response;
    }
}
