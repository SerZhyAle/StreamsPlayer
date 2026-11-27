using System.Globalization;
using System.Text.Json.Nodes;

namespace StreamsPlayer.Core;

/// <summary>
/// STRWIN-0305: the sentences of the broadcast verbs. English, one sentence each, never an address, a device name, a
/// password or a title - the same rule <see cref="AppCommandMessages"/> keeps.
/// </summary>
public static class AppBroadcastMessages
{
    public const string StartOptionsOnce = "The command \"broadcast start\" takes --source once.";

    public const string StopOptionsOnce = "The command \"broadcast stop\" takes --source or --all, once.";

    /// <summary>B4: <c>APP-COMMANDS</c> item M, <c>module-off</c> - the message names the setting, which no command changes.</summary>
    public const string NotAllowed =
        "Broadcasting is off on this PC; turn on \"Allow this PC to broadcast\" on the Settings page Broadcasting.";

    public const string GuestOnly =
        "This PC is signed in to the exchange server as a guest, and a guest does not broadcast; sign in with your own account in Settings.";

    public const string VideoUnavailable = "This PC cannot broadcast a picture: Windows has no video encoder for it.";

    public const string Starting = "A broadcast of this source is being started; try again in a moment.";

    public const string TooMany = "Too many broadcasts run at once; stop one first.";

    public const string NoReceiver =
        "Nothing can receive this broadcast; allow the local network or connect to the exchange server in Settings.";

    public const string ServerFailed = "The exchange server did not carry this broadcast; the log has the details.";

    public const string PermissionDenied =
        "Windows denies this source to the application; allow it in the Windows privacy settings and start again.";

    public const string DeviceInUse = "The device of this source is held by another program.";

    public const string DeviceMissing = "The device of this source is gone or does not exist.";

    public const string CaptureFailed = "This source could not be captured; the log has the details.";

    public const string PortTaken = "The local network port of this source is taken; choose another one in Settings.";

    public const string EncoderMissing = "The Windows audio encoder is unavailable on this PC, so a broadcast with sound cannot start.";

    public const string StartFailed = "The broadcast could not be started; the log has the details.";

    public const string Cancelled = "The start was stopped before the broadcast went live.";

    public const string StopChoose = "Several broadcasts run; add --source <name> or --all to say which to stop.";

    public const string NothingLive = "Nothing is being broadcast.";

    public static string Started(string source, bool already) =>
        already ? $"The {source} broadcast is already live." : $"The {source} broadcast is live.";

    public static string Stopped(int count) => count switch
    {
        0 => NothingLive,
        1 => "The broadcast is stopped.",
        _ => string.Create(CultureInfo.InvariantCulture, $"{count} broadcasts are stopped.")
    };
}

/// <summary>A broadcast stop's sources, or the refusal when the request does not name them (rule 4).</summary>
public sealed record AppBroadcastStopPlan(IReadOnlyList<BroadcastSource> Targets, bool All, AppCommandResponse? Refusal);

/// <summary>
/// STRWIN-0305: the broadcast command group's decisions that need no window - which options go together, which source a
/// verb names, which stop a request means, and every answer, including the reading of the producer's verdict. The App
/// asks the broadcast producer the window uses; this decides, so the rules are tested without a capture device.
/// </summary>
public static class AppBroadcastCommands
{
    public const string StartVerb = "broadcast start";
    public const string StopVerb = "broadcast stop";

    /// <summary>
    /// The producer's start refusal and failure keys (<c>BroadcastProducerService</c>), and the exchange server's own
    /// (<c>ExchangeFailureRow</c>, all prefixed <c>Exchange</c>), as they reach <see cref="StartFailed"/>.
    /// </summary>
    public const string ExchangeKeyPrefix = "Exchange";

    /// <summary>The rules of the two verbs the manifest alone cannot express (rule 3); <see langword="null"/> when acceptable.</summary>
    public static AppPlaybackCommands.Violation? Check(AppCommandEntry entry, IReadOnlyDictionary<string, IReadOnlyList<string>> options) =>
        entry.Name switch
        {
            StartVerb => Count(options, "source") == 1 ? null : Bad(AppBroadcastMessages.StartOptionsOnce),
            StopVerb => Count(options, "source") > 1 || Count(options, "all") > 1
                || (options.ContainsKey("source") && AppPlaybackCommands.IsTrue(options, "all"))
                ? Bad(AppBroadcastMessages.StopOptionsOnce)
                : null,
            _ => null
        };

    /// <summary>The source <c>--source</c> names, by its <c>LIVE-BROADCAST</c> item G wire name (B1).</summary>
    public static BroadcastSource? SourceOf(IReadOnlyDictionary<string, IReadOnlyList<string>> options) =>
        BroadcastSources.TryParseWireName(AppPlaybackCommands.Last(options, "source"), out var source) ? source : null;

    /// <summary>The answer to a start the consent gate refuses (B4, B6); <see langword="null"/> when it lets the start go on.</summary>
    public static AppCommandResponse? StartRefusal(string requestId, BroadcastStartRefusal? refusal) => refusal switch
    {
        null or BroadcastStartRefusal.AlreadyRunning => null,
        BroadcastStartRefusal.NotAllowed => ModuleOff(requestId, AppBroadcastMessages.NotAllowed),
        BroadcastStartRefusal.GuestAccount => ModuleOff(requestId, AppBroadcastMessages.GuestOnly),
        _ => AppCommandResponse.Failed(requestId, AppCommandReason.NoSpace, AppCommandExit.Resources, AppBroadcastMessages.TooMany)
    };

    /// <summary>B5: the same source is being started (by the window or another command).</summary>
    public static AppCommandResponse StartUnderWay(string requestId) =>
        AppCommandResponse.Busy(requestId, AppBroadcastMessages.Starting);

    /// <summary>
    /// B6: the answer to a start that did not go live, read from the producer's verdict key. <paramref name="cancelled"/>
    /// is the caller having stopped waiting. A key this table does not know is an ordinary failure.
    /// </summary>
    public static AppCommandResponse StartFailed(string requestId, string? reasonKey, bool cancelled = false)
    {
        if (cancelled)
        {
            return AppCommandResponse.Failed(requestId, AppCommandReason.Cancelled, AppCommandExit.Stopped, AppBroadcastMessages.Cancelled);
        }

        return reasonKey switch
        {
            "BroadcastNotAllowed" => ModuleOff(requestId, AppBroadcastMessages.NotAllowed),
            "BroadcastGuestRefused" => ModuleOff(requestId, AppBroadcastMessages.GuestOnly),
            "BroadcastTooManySources" => StartRefusal(requestId, BroadcastStartRefusal.TooManySources)!,
            "BroadcastVideoUnavailable" => AppCommandResponse.Failed(requestId, AppCommandReason.TargetMissing,
                AppCommandExit.NotReady, AppBroadcastMessages.VideoUnavailable),
            "BroadcastNoReachableEndpoint" => Network(requestId, AppBroadcastMessages.NoReceiver),
            "BroadcastRelayLost" => Network(requestId, AppBroadcastMessages.ServerFailed),
            { } key when key.StartsWith(ExchangeKeyPrefix, StringComparison.Ordinal) => Network(requestId, AppBroadcastMessages.ServerFailed),
            "BroadcastPermissionDenied" => Io(requestId, AppBroadcastMessages.PermissionDenied),
            "BroadcastDeviceInUse" => Io(requestId, AppBroadcastMessages.DeviceInUse),
            "BroadcastDeviceMissing" => Io(requestId, AppBroadcastMessages.DeviceMissing),
            "BroadcastCaptureFailed" => Io(requestId, AppBroadcastMessages.CaptureFailed),
            "BroadcastPortTaken" => Error(requestId, AppBroadcastMessages.PortTaken),
            "BroadcastEncoderMissing" => Error(requestId, AppBroadcastMessages.EncoderMissing),
            _ => Error(requestId, AppBroadcastMessages.StartFailed)
        };
    }

    /// <summary>
    /// B2: a live broadcast - the source the caller named, and where it can be received: the local network, the exchange
    /// server. Never an address, a link or a password.
    /// </summary>
    public static AppCommandResponse Started(string requestId, BroadcastSource source, bool already, bool lan, bool exchange)
    {
        var name = BroadcastSources.WireName(source);
        return AppCommandResponse.Done(requestId, AppBroadcastMessages.Started(name, already), new JsonObject
        {
            ["source"] = name,
            ["already"] = already,
            ["lan"] = lan,
            ["exchange"] = exchange
        });
    }

    /// <summary>
    /// B1: the sources <c>broadcast stop</c> acts on - <c>--all</c> every live or starting one, <c>--source</c> that one if it
    /// is, neither the one there is. Several and no choice is refused (rule 4: never guessed); none is nothing to do.
    /// </summary>
    public static AppBroadcastStopPlan PlanStop(string requestId, BroadcastSource? source, bool all, IReadOnlyCollection<BroadcastSource> live)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (all)
        {
            return new AppBroadcastStopPlan([.. live], true, null);
        }

        if (source is { } named)
        {
            return new AppBroadcastStopPlan(live.Contains(named) ? [named] : [], false, null);
        }

        return live.Count > 1
            ? new AppBroadcastStopPlan([], false, AppCommandResponse.Refused(requestId, AppCommandReason.BadArgument,
                AppCommandExit.Usage, AppBroadcastMessages.StopChoose))
            : new AppBroadcastStopPlan([.. live], false, null);
    }

    /// <summary>The answer of <c>broadcast stop</c>: how many broadcasts it ended, 0 when nothing was live (B7).</summary>
    public static AppCommandResponse Stopped(string requestId, int count) =>
        AppCommandResponse.Done(requestId, AppBroadcastMessages.Stopped(count), new JsonObject { ["stopped"] = count });

    private static AppCommandResponse ModuleOff(string requestId, string message) =>
        AppCommandResponse.Refused(requestId, AppCommandReason.ModuleOff, AppCommandExit.NotReady, message);

    private static AppCommandResponse Network(string requestId, string message) =>
        AppCommandResponse.Failed(requestId, AppCommandReason.Network, AppCommandExit.Network, message);

    private static AppCommandResponse Io(string requestId, string message) =>
        AppCommandResponse.Failed(requestId, AppCommandReason.IoError, AppCommandExit.Io, message);

    private static AppCommandResponse Error(string requestId, string message) =>
        AppCommandResponse.Failed(requestId, AppCommandReason.Error, AppCommandExit.Failed, message);

    private static int Count(IReadOnlyDictionary<string, IReadOnlyList<string>> options, string name) =>
        options.TryGetValue(name, out var values) ? values.Count : 0;

    private static AppPlaybackCommands.Violation Bad(string message) => new(AppCommandReason.BadArgument, message);
}
