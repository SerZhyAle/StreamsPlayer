using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0271: the first instance's end of the <c>APP-COMMANDS</c> 0.1 profile - the five base commands, the busy answer
/// of rule 10, the risk gate of rule 13 and the log line of rule 16.
/// </summary>
/// <remarks>
/// <para>
/// A command runs on the UI thread, one at a time, in the order the pipe delivers them. It is given the delivery bound
/// to start there: a UI thread that pumps nothing (a hang, a long synchronous step) is answered <c>busy</c> instead of
/// holding the caller until its own bound, and a command not yet started is withdrawn so it can never run late.
/// </para>
/// <para>
/// Busy (research R1 of the ticket): WPF exposes no "is a modal open". <see cref="ComponentDispatcher.IsThreadModal"/>
/// is what <see cref="Window.ShowDialog"/> raises; a message box raises nothing but disables its owner window, which
/// <c>IsWindowEnabled</c> sees. The first-run catalog window is a dialog of the first kind.
/// </para>
/// </remarks>
public partial class App
{
    /// <summary>SP-0279, item J: shorter than the delivery bound, so a busy answer reaches the caller inside it.</summary>
    private static readonly TimeSpan CommandStartBound = AppCommandLimits.StartBound;

    /// <summary>SP-0271: the single-instance lock <see cref="Program"/> already took, when it did.</summary>
    internal SingleInstanceLock? HandedLock { get; init; }

    /// <summary>SP-0271: the launch target arguments when they are not the process's own (a verb started this copy).</summary>
    internal IReadOnlyList<string>? LaunchArguments { get; init; }

    /// <summary>
    /// SP-0274: a start verb other than <c>show</c> and <c>play</c> that started this copy. The window runs it once its catalog
    /// has loaded, through the same path a forwarded command takes.
    /// </summary>
    internal AppStartCommand? StartCommand { get; init; }

    /// <summary>The exit code of <see cref="StartCommand"/> once it has answered; <see langword="null"/> before that.</summary>
    internal int? StartCommandExitCode { get; private set; }

    /// <summary>
    /// Runs <paramref name="start"/> on the UI thread as the window's first command, then reports it the way a second
    /// process would: on standard output, and to <c>--result</c>.
    /// </summary>
    internal async Task RunStartCommandAsync(AppStartCommand start)
    {
        ArgumentNullException.ThrowIfNull(start);
        var response = await OnCommandAsync(start.Request, CancellationToken.None);
        StartCommandExitCode = CommandConsole.Report(response, start.Json, start.ResultPath);
    }

    private async Task<AppCommandResponse> OnCommandAsync(AppCommandRequest request, CancellationToken cancellationToken)
    {
        var operation = Dispatcher.InvokeAsync(() => ExecuteCommand(request, cancellationToken));
        try
        {
            var started = await Task.WhenAny(operation.Task, Task.Delay(CommandStartBound, cancellationToken)).ConfigureAwait(false);
            if (started != operation.Task && operation.Abort())
            {
                return Logged(request, AppCommandResponse.Busy(request.RequestId, AppCommandMessages.NotResponding));
            }
        }
        catch (OperationCanceledException)
        {
            operation.Abort();
            throw;
        }

        // Started on the UI thread. The base verbs are finished by now; play and resume wait for the first sound or
        // picture, and the record verbs for the first recorded data or the finished file, inside the verb's own bound,
        // off the UI thread, while the dispatcher goes on pumping.
        var pending = await operation.Task.ConfigureAwait(false);
        return await pending.ConfigureAwait(false);
    }

    /// <summary>
    /// Runs on the UI thread up to its first wait and returns what finishes the command. Every verb but <c>play</c>,
    /// <c>playback resume</c> and the two <c>record</c> verbs is finished before it returns.
    /// </summary>
    private Task<AppCommandResponse> ExecuteCommand(AppCommandRequest request, CancellationToken cancellationToken)
    {
        var words = request.Command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var parse = AppCommandLine.Parse([.. words, .. request.Args], AppCommandManifest.Embedded);
        if (parse.Kind == AppCommandParseKind.Refused)
        {
            return LoggedAsync(request, Task.FromResult(parse.ToRefusal(request.RequestId)));
        }

        if (parse.Kind != AppCommandParseKind.Verb ||
            !string.Equals(parse.Entry!.Name, request.Command, StringComparison.OrdinalIgnoreCase))
        {
            // The request names a canonical verb, never an alias or a launch (rule 7).
            return LoggedAsync(request, Task.FromResult(AppCommandResponse.Refused(request.RequestId, AppCommandReason.UnknownVerb,
                AppCommandExit.Usage, AppCommandMessages.UnknownVerb(words.FirstOrDefault() ?? string.Empty))));
        }

        if (_mainWindow is not { } window || BusyCause(window) is not null)
        {
            return LoggedAsync(request, Task.FromResult(AppCommandResponse.Busy(request.RequestId, AppCommandMessages.Busy)));
        }

        var refused = AppCommandPolicy.CheckRisk(request.RequestId, parse.Entry, window.AcceptsRestrictedCommands,
            LocalizationService.Get("AcceptsRestrictedCommands"));
        return LoggedAsync(request,
            refused is not null ? Task.FromResult(refused) : Run(parse, request, window, cancellationToken));
    }

    /// <summary>Waits for what <see cref="ExecuteCommand"/> started, then logs the outcome - on the UI thread, where the log is written.</summary>
    private async Task<AppCommandResponse> LoggedAsync(AppCommandRequest request, Task<AppCommandResponse> pending)
    {
        try
        {
            return Logged(request, await pending);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Rule 16 logs every accepted command; the listener answers the throw as failed.
            Logged(request, AppCommandResponse.Failed(request.RequestId, AppCommandReason.Error, AppCommandExit.Failed,
                AppCommandMessages.Failed));
            _log?.Error("Command failed", exception);
            throw;
        }
    }

    private Task<AppCommandResponse> Run(
        AppCommandParse parse, AppCommandRequest request, MainWindow window, CancellationToken cancellationToken)
    {
        var verb = parse.Entry!.Name;
        var requestId = request.RequestId;
        switch (verb)
        {
            case AppPlaybackCommands.PlayVerb:
                return window.PlayForCommandAsync(requestId, parse, request, cancellationToken);
            case AppPlaybackCommands.ResumeVerb:
                return window.ResumeForCommandAsync(requestId, parse.Entry, cancellationToken);
            case AppPlaybackCommands.VolumeVerb:
                return window.SetVolumeForCommandAsync(requestId, parse);
            case AppPlaybackCommands.StopVerb:
                return Task.FromResult(window.StopForCommand(requestId, parse));
            case AppPlaybackCommands.PauseVerb:
                return Task.FromResult(window.PauseForCommand(requestId));
            case AppPlaybackCommands.StatusVerb:
                return Task.FromResult(window.StatusForCommand(requestId));
            case AppRecordCommands.StartVerb:
                return window.RecordStartForCommandAsync(requestId, parse.Entry, parse, request, cancellationToken);
            case AppRecordCommands.StopVerb:
                return window.RecordStopForCommandAsync(requestId, parse.Entry, parse, cancellationToken);
            case AppChannelCommands.AddVerb:
                return window.ChannelAddForCommandAsync(requestId, parse, request.WorkingDir);
            default:
                return Task.FromResult(RunBase(verb, requestId, window));
        }
    }

    private AppCommandResponse RunBase(string verb, string requestId, MainWindow window)
    {
        switch (verb)
        {
            case "status":
                return AppCommandResponse.Done(requestId, AppCommandMessages.Ready(ProductInfo.Version), new JsonObject
                {
                    ["version"] = ProductInfo.Version,
                    ["pid"] = Environment.ProcessId,
                    ["acceptsRestrictedCommands"] = window.AcceptsRestrictedCommands,
                    ["activeSessions"] = Windows.OfType<PlayerWindow>().Count() + (window.IsPlayingAudio ? 1 : 0),
                    ["profile"] = AppPaths.IsRelocated ? "relocated" : "default"
                });
            case "show":
                var foreground = window.BringForwardForCommand();
                _log?.Event("COMMAND SHOW", $"foreground={foreground}");
                return AppCommandResponse.Done(requestId, AppCommandMessages.Shown);
            case "quit":
                // Answered first: the close takes the listener down with it, and nothing cancels the close (SP-0120).
                Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    if (!_mainWindowClosing && !_mainWindowClosed)
                    {
                        window.Close();
                    }
                });
                return AppCommandResponse.Done(requestId, AppCommandMessages.Closing);
            case "version":
                return AppCommandResponse.Done(requestId,
                    AppCommandMessages.Version(ProductInfo.Version, AppCommandManifest.Embedded.CliVersion));
            case "commands":
                return AppCommandResponse.Done(requestId, "Run \"StreamsPlayer.exe commands\" for the list.");
            default:
                return AppCommandResponse.Refused(requestId, AppCommandReason.UnknownVerb, AppCommandExit.Usage,
                    AppCommandMessages.UnknownVerb(verb));
        }
    }

    /// <summary>Why the application cannot take a command now, or <see langword="null"/> when it can (rule 10).</summary>
    private string? BusyCause(MainWindow window)
    {
        if (_mainWindowClosing || _mainWindowClosed || !window.IsLoaded)
        {
            return "closing-or-starting";
        }

        if (ComponentDispatcher.IsThreadModal)
        {
            return "modal";
        }

        foreach (Window open in Windows)
        {
            var handle = new WindowInteropHelper(open).Handle;
            if (handle != IntPtr.Zero && !IsWindowEnabled(handle))
            {
                return "modal";
            }
        }

        return null;
    }

    /// <summary>
    /// Rule 16: one line per command - verb, origin, the caller's process id, the outcome. No argument value, no path,
    /// no request id.
    /// </summary>
    private AppCommandResponse Logged(AppCommandRequest request, AppCommandResponse response)
    {
        var verb = AppCommandLine.IsVerb(request.Command) ? request.Command.ToLowerInvariant().Replace(' ', '-') : "invalid";
        _log?.Event("COMMAND",
            $"verb={verb}",
            $"origin={(AppCommandLine.IsVerbWord(request.Origin) ? request.Origin : "other")}",
            $"caller_pid={request.CallerPid?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}",
            $"args={request.Args.Count}",
            $"status={response.Status}",
            $"exit={response.ExitCode}",
            $"reason={response.Reason ?? "none"}");
        return response;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr window);
}

/// <summary>
/// SP-0274: a command that started this copy (cold start <c>start</c>, not <c>show</c> or <c>play</c>). <see cref="Json"/> and
/// <see cref="ResultPath"/> are the caller's output options, as the second process would have applied them.
/// </summary>
internal sealed record AppStartCommand(AppCommandRequest Request, bool Json, string? ResultPath);
