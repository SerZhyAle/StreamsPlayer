using System.Diagnostics;
using System.IO;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0271, <c>APP-COMMANDS</c> rule 6: the entry point. A second process decides its role here, before the WPF
/// application, its resource dictionaries, the log, the settings or any worker exist - so a command is a pipe round
/// trip and an exit, and <c>commands</c> and <c>version</c> never load the interface at all.
/// </summary>
/// <remarks>
/// <para>
/// Three things reach <see cref="App"/> unchanged: the launch grammar (<c>--url</c>, <c>--id</c>, a <c>.spres</c> path -
/// the aliases of the default verb, SP-0272), whose hand-over <see cref="App"/> still makes exactly as SP-0118 and
/// SP-0170 built it; a bare start that the running copy did not answer <c>done</c>; and a verb whose cold start is
/// <c>start</c> with nothing running, which hands <see cref="App"/> the single-instance lock it already took. The lock
/// is taken on this thread, which is the thread <see cref="System.Windows.Application.Run()"/> runs on, so the UI
/// thread releases it as before.
/// </para>
/// <para>A broken manifest never stops the player starting: every argument then takes the path it had before.</para>
/// </remarks>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is [HelpWindowCapture.Argument] &&
            Environment.GetEnvironmentVariable(HelpWindowCapture.DirectoryVariable) is { Length: > 0 } capture)
        {
            return HelpWindowCapture.Run(capture);
        }

        var startup = Decide(args);
        if (startup.ExitCode is { } exitCode)
        {
            return exitCode;
        }

        var app = new App { HandedLock = startup.Lock, LaunchArguments = startup.LaunchArguments, StartCommand = startup.StartCommand };
        app.InitializeComponent();
        var appExit = app.Run();
        // SP-0274: a start verb answered while the window loaded hands its exit code to the process that started it.
        return app.StartCommandExitCode ?? appExit;
    }

    private static Startup Decide(string[] args)
    {
        AppCommandManifest manifest;
        try
        {
            manifest = AppCommandManifest.Embedded;
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException)
        {
            return Startup.RunApp(null);
        }

        var parse = AppCommandLine.Parse(args, manifest);
        return parse.Kind switch
        {
            AppCommandParseKind.Launch => Startup.RunApp(null),
            AppCommandParseKind.Alias => BareStart(parse.Entry!),
            AppCommandParseKind.Refused => Finish(parse.ToRefusal(NewRequestId()),
                args.Any(argument => string.Equals(argument, "--json", StringComparison.OrdinalIgnoreCase)), resultPath: null),
            _ => RunVerb(parse, manifest)
        };
    }

    /// <summary>
    /// The no-argument start, the alias <c>""</c> of <c>show</c>. Anything but <c>done</c> from a running copy - busy,
    /// an older copy, no answer - falls back to the hand-over <see cref="App"/> has always made, so a double-click
    /// behaves as it did (SP-0271 Assumed 2).
    /// </summary>
    private static Startup BareStart(AppCommandEntry show)
    {
        var identity = Identity();
        if (SingleInstanceLock.TryAcquire(identity.MutexName, TimeSpan.Zero, out var held) == SingleInstanceLockResult.Acquired)
        {
            return Startup.RunApp(held);
        }

        ForegroundActivation.AllowAnyProcessToTakeForeground();
        var exchange = AppCommandClient.Send(identity.PipeName, Request(show, [], NewRequestId()),
            AppCommandClient.DeliveryBound, AppCommandPolicy.CompletionBound(show));
        // Only an answer to this request counts (item I.3); the manifest says the bare start "answers": false (item E).
        return exchange is { Delivery: AppCommandDelivery.Answered, Response.Status: AppCommandStatus.Done }
            ? Startup.Exit(AppCommandExit.Done)
            : Startup.RunApp(null);
    }

    private static Startup RunVerb(AppCommandParse parse, AppCommandManifest manifest)
    {
        var entry = parse.Entry!;
        var json = parse.Has("json");
        var resultPath = parse.Value("result");
        var requestId = NewRequestId();
        switch (entry.Name)
        {
            case "commands":
                PrintCommands(manifest, json);
                return Startup.Exit(AppCommandExit.Done);
            case "version":
                return Finish(AppCommandCaller.VersionResponse(requestId, manifest, ProductInfo.Version), json, resultPath);
        }

        var identity = Identity();
        var lockResult = SingleInstanceLock.TryAcquire(identity.MutexName, TimeSpan.Zero, out var held);
        if (lockResult == SingleInstanceLockResult.Acquired)
        {
            switch (AppCommandPolicy.ColdStartOf(entry))
            {
                case AppCommandColdStart.Start:
                    if (entry.Name == AppPlaybackCommands.PlayVerb)
                    {
                        // SP-0279, item H: the window reads a launch target, not a file, so a --url-file is read here, in the
                        // caller's own process and folder, before the start. A problem with it ends the command at once.
                        var urlFile = AppUrlFile.Resolve(requestId, parse.Options, Environment.CurrentDirectory,
                            AppPlaybackMessages.PlayFileAddressNotLaunchable);
                        if (urlFile.Refusal is { } unreadable)
                        {
                            held!.Dispose();
                            return Finish(unreadable, json, resultPath);
                        }

                        parse = parse with { Options = urlFile.Options };
                    }

                    // The verb is the start itself. "show" has no launch target (the window would read the word as one);
                    // "play" starts with its target exactly as a double-clicked shortcut or .spres always did (SP-0272).
                    return Startup.RunApp(held) with
                    {
                        LaunchArguments = StartArguments(parse),
                        StartCommand = StartCommandFor(entry, parse, requestId, json, resultPath)
                    };
                case AppCommandColdStart.Oneshot:
                    // Released first, so a copy the person starts meanwhile is the first instance (rule 10).
                    held!.Dispose();
                    return Finish(AppCommandCaller.Oneshot(requestId, entry), json, resultPath);
                default:
                    held!.Dispose();
                    return Finish(AppCommandPolicy.NotRunning(requestId), json, resultPath);
            }
        }

        // A command that brings the window forward is the one process that may ask Windows to let it (the same call a
        // forwarded launch made before): "show", and "play" unless it was told to stay in the background.
        if (entry.Name == "show" || (entry.Name == AppPlaybackCommands.PlayVerb && !AppPlaybackCommands.IsTrue(parse.Options, "background")))
        {
            ForegroundActivation.AllowAnyProcessToTakeForeground();
        }

        var request = Request(entry, parse.ArgumentsAfterVerb, requestId);
        if (request.Serialize().Length - 1 > AppCommandLimits.MaximumRequestBytes)
        {
            return Finish(AppCommandResponse.Refused(requestId, AppCommandReason.TooLarge, AppCommandExit.Usage,
                AppCommandMessages.TooLarge), json, resultPath);
        }

        var bound = AppCommandPolicy.CompletionBound(entry);
        var clock = Stopwatch.StartNew();
        var exchange = AppCommandClient.Send(identity.PipeName, request, AppCommandClient.DeliveryBound, bound);
        if (parse.FromLaunchSpelling && exchange.Delivery == AppCommandDelivery.NotConnected)
        {
            // A shortcut or a double-clicked file keeps the hand-over it always had when nothing takes the request - a
            // copy that is closing or still starting: the lock wait and the notices of SP-0170, run by App on the
            // process's own arguments, which are exactly the spelling that was typed.
            return Startup.RunApp(null);
        }

        // SP-0275: the mapping is the one the console companion uses (AppCommandCaller), so a command is answered alike by both.
        var response = AppCommandCaller.Interpret(exchange, lockResult, requestId,
            () => OlderFirstInstance(identity, entry, parse, requestId));

        if (entry.Name == "quit" && response.Status == AppCommandStatus.Done)
        {
            response = AppCommandCaller.AwaitClose(identity, requestId, bound - clock.Elapsed);
        }

        return Finish(response, json, resultPath);
    }

    /// <summary>
    /// What <see cref="App"/> is told to start with when a verb with the cold start <c>start</c> becomes the first
    /// instance: nothing for <c>show</c>, the target of <c>play</c> in the launch grammar the window has always read.
    /// </summary>
    private static IReadOnlyList<string> StartArguments(AppCommandParse parse) =>
        parse.Entry!.Name == AppPlaybackCommands.PlayVerb
            ? AppPlaybackCommands.LaunchArguments(parse.Options, Environment.CurrentDirectory)
            : [];

    /// <summary>
    /// The research proposal of 2026-11-14, item 4: a running copy older than the profile answered with its plain line.
    /// A verb that has a frozen alias first re-sends that alias's launch line, so the old copy does what it always did
    /// (the bare start for <c>show</c>, the target for <c>play</c>); the exit is still 2, because this process saw no end.
    /// </summary>
    private static AppCommandResponse OlderFirstInstance(
        SingleInstanceIdentity identity, AppCommandEntry entry, AppCommandParse parse, string requestId)
    {
        if (entry.Aliases.Contains(string.Empty, StringComparer.Ordinal))
        {
            ActivationPipeClient.TrySend(identity.PipeName, [], ActivationPipeClient.ConnectTimeout);
        }
        else if (entry.Name == AppPlaybackCommands.PlayVerb &&
                 AppUrlFile.Resolve(requestId, parse.Options, Environment.CurrentDirectory, AppPlaybackMessages.PlayFileAddressNotLaunchable)
                     is { Refusal: null } urlFile)
        {
            // SP-0279, item H: an address held in a file is sent as the launch line the older copy always understood.
            ActivationPipeClient.TrySend(identity.PipeName,
                AppPlaybackCommands.LaunchArguments(urlFile.Options, Environment.CurrentDirectory), ActivationPipeClient.ConnectTimeout);
        }

        return AppCommandCaller.OlderInstance(requestId);
    }

    /// <summary><c>commands</c>: the manifest's own bytes with <c>--json</c> (rule 18, C1), a readable list otherwise.</summary>
    private static void PrintCommands(AppCommandManifest manifest, bool json)
    {
        if (json)
        {
            CommandConsole.Raw(AppCommandManifest.EmbeddedBytes.Span);
            return;
        }

        CommandConsole.Line(AppCommandCaller.CommandList(manifest, manifest.Exe));
    }

    /// <summary>
    /// Rule 9: the message, or with <c>--json</c> the response line itself, on standard output; <c>--result</c> holds the
    /// data of a finished command, whole or not at all.
    /// </summary>
    private static Startup Finish(AppCommandResponse response, bool json, string? resultPath) =>
        Startup.Exit(CommandConsole.Report(response, json, resultPath));

    /// <summary>
    /// SP-0274: a start verb other than <c>show</c> (whose start is the window itself) and <c>play</c> (whose start is its launch
    /// target) is run by the window once its catalog has loaded, as the first thing it does. Its answer then goes to
    /// <c>--result</c> and standard output as a second process's would.
    /// </summary>
    private static AppStartCommand? StartCommandFor(
        AppCommandEntry entry, AppCommandParse parse, string requestId, bool json, string? resultPath) =>
        entry.Name is "show" or AppPlaybackCommands.PlayVerb
            ? null
            : new AppStartCommand(Request(entry, parse.ArgumentsAfterVerb, requestId), json, AbsolutePath(resultPath));

    /// <summary>
    /// The <c>--result</c> path as the caller meant it: the window that writes it may start in another folder. A path the
    /// file system refuses is kept as given, so the write fails where it always fails (exit 14).
    /// </summary>
    private static string? AbsolutePath(string? path)
    {
        if (path is null)
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static AppCommandRequest Request(AppCommandEntry entry, IReadOnlyList<string> args, string requestId) =>
        AppCommandCaller.NewRequest(entry, args, requestId);

    private static SingleInstanceIdentity Identity() => AppDataProfile.Identity();

    private static string NewRequestId() => AppCommandCaller.NewRequestId();

    /// <summary>What <see cref="Main"/> does next: exit with a code, or run the application, holding the lock or not.</summary>
    private sealed record Startup(int? ExitCode, SingleInstanceLock? Lock)
    {
        /// <summary>The arguments the window reads as its launch target; <see langword="null"/> is the process's own.</summary>
        internal IReadOnlyList<string>? LaunchArguments { get; init; }

        /// <summary>SP-0274: the command the window runs once it has loaded, when a start verb started it.</summary>
        internal AppStartCommand? StartCommand { get; init; }

        internal static Startup Exit(int exitCode) => new(exitCode, null);

        internal static Startup RunApp(SingleInstanceLock? held) => new(null, held);
    }
}
