using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public partial class App : Application
{
    /// <summary>
    /// SP-0120: how long the process waits for the main window's close work - session save and disposals -
    /// before it ends anyway. One deadline for quitting and for a Windows sign-out or shutdown, sized to the
    /// latter: Windows gives an application about five seconds to answer before it shows the "apps are
    /// preventing sign-out" screen, and the work normally takes milliseconds.
    /// </summary>
    private static readonly TimeSpan CloseWorkDeadline = TimeSpan.FromSeconds(4);

    internal ExchangeSourceService Exchange { get; private set; } = null!;

    private CurrentLog? _log;
    private SingleInstanceLock? _instanceLock;
    private ActivationPipeListener? _activationListener;
    private MainWindow? _mainWindow;
    private bool _mainWindowClosed;
    private bool _sessionEnding;
    private int _terminating;
    private int _exitCompleted;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // SP-0118: decided before anything else - the log included - so a later copy writes nothing,
        // rotates no session log and opens no window: it hands its request over and ends.
        var identity = SingleInstanceIdentity.For(
            AppPaths.DataDirectory, AppPaths.DefaultDataDirectory, Process.GetCurrentProcess().SessionId);
        var lockResult = SingleInstanceLock.TryAcquire(identity.MutexName, TimeSpan.Zero, out _instanceLock);
        if (lockResult != SingleInstanceLockResult.Acquired
            && !HandOverToRunningCopy(identity, e.Args, lockResult, out _instanceLock))
        {
            return;
        }

        // SP-0120: the default, OnLastWindowClose, ended the process as soon as the main window was gone - which
        // is before its asynchronous close work had got past its first await. The process now ends when that
        // work is done (EndAfterCloseWorkAsync), or at the deadline.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _log = new CurrentLog(AppPaths.DataDirectory);
        HandlerBoundary.Initialize(_log);
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        ThemeService.Initialize();
        _log.Information("Application startup.");
        // A user-sent archive holds the last ten launches, which can span several builds, while its
        // environment summary names only the build that packed it. This line is what ties a session to its build.
        _log.Event("SESSION START",
            $"version={ProductInfo.Version}",
            $"os={System.Runtime.InteropServices.RuntimeInformation.OSDescription}",
            $"arch={System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}",
            $"runtime={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}",
            $"utc_offset={TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.UtcNow)}");
        // SP-0133: a relocated profile is announced first, and so is a refused relocation - a smoke run whose
        // variable was ignored is running against the owner's profile, and only this line says so.
        if (AppPaths.RequestedDataDirectory is { } requested)
        {
            _log.Event("DATA DIRECTORY", AppPaths.IsRelocated ? "relocated=true" : "relocated=refused",
                $"requested={requested}", $"path={AppPaths.DataDirectory}");
        }

        // Listening before the window exists keeps a later copy's bounded connect from timing out on a
        // slow start; what arrives is marshalled to the dispatcher, which runs it once this method ends.
        _activationListener = new ActivationPipeListener(identity.PipeName, OnForwardedLaunch, OnForwardedLaunchRejected);
        _activationListener.Start();

        Exchange = new ExchangeSourceService(AppPaths.DataDirectory);
        Exchange.Start();

        var window = new MainWindow(_log, StreamLaunchRequest.Parse(e.Args));
        window.InitializeExchangeStatus(Exchange);
        _mainWindow = window;
        MainWindow = window;
        // Registered after the window's own Closed handler, which is what starts the close work read below.
        window.Closed += (_, _) => HandlerBoundary.Run("App.MainWindow.Closed", () => EndAfterCloseWorkAsync(window));
        // SP-0170: the moment the close is under way - nothing cancels it - a forwarded launch is refused, and the
        // sender is told so, rather than a window that is closing being asked to play it.
        window.Closing += (_, _) => _activationListener?.StopAccepting();
        window.Show();
    }

    /// <summary>
    /// SP-0120: ends the process once the main window's close work has finished, or at the deadline. This is the
    /// only ordinary way the process ends; <see cref="ShutdownMode.OnExplicitShutdown"/> is what makes it so.
    /// </summary>
    private async Task EndAfterCloseWorkAsync(MainWindow window)
    {
        _mainWindowClosed = true;
        try
        {
            if (await Task.WhenAny(window.CloseWork, Task.Delay(CloseWorkDeadline)) != window.CloseWork)
            {
                _log?.Event("SHUTDOWN DEADLINE", "path=quit", $"after_ms={CloseWorkDeadline.TotalMilliseconds:F0}");
            }
        }
        finally
        {
            // A session end has its own ending: Windows' own request is what calls Shutdown there.
            if (!_sessionEnding)
            {
                Shutdown();
            }
        }
    }

    /// <summary>
    /// SP-0120: a Windows sign-out or shutdown. Nothing handled it, so the session's save and disposals were
    /// whatever happened to run before the process was killed. The same close work runs here, before Windows is
    /// answered - once the answer is given, the process can be ended at any moment - and the exit path completes
    /// with it, so the log records an ordinary exit. WPF calls <see cref="Application.Shutdown()"/> itself when
    /// this returns without cancelling, which is the end the rest of the process takes.
    /// </summary>
    /// <remarks>
    /// This runs inside the window message that asks the question, so it cannot await; the dispatcher is pumped
    /// in a nested frame instead, which is what lets the close work's continuations run while this waits.
    /// </remarks>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        if (_mainWindow is not { } window || e.Cancel)
        {
            return;
        }

        _sessionEnding = true;
        _log?.Event("SESSION ENDING", $"reason={e.ReasonSessionEnding}");
        if (!_mainWindowClosed)
        {
            window.Close();
        }

        if (!WaitPumping(window.CloseWork, CloseWorkDeadline))
        {
            _log?.Event("SHUTDOWN DEADLINE", "path=session_end", $"after_ms={CloseWorkDeadline.TotalMilliseconds:F0}");
        }

        CompleteExit();
    }

    /// <summary>
    /// Pumps the dispatcher until <paramref name="work"/> completes or <paramref name="deadline"/> passes, and
    /// says whether the work completed. Only for a caller that cannot await.
    /// </summary>
    private static bool WaitPumping(Task work, TimeSpan deadline)
    {
        if (!work.IsCompleted)
        {
            var frame = new DispatcherFrame();
            // Setting Continue from another thread wakes the frame; the continuation runs on the pool for that reason.
            _ = Task.WhenAny(work, Task.Delay(deadline)).ContinueWith(
                _ => frame.Continue = false, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }

        return work.IsCompleted;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        CompleteExit();
        // Here and not in CompleteExit: a session end completes the exit path early, and the process still runs
        // the rest of its shutdown after that - with its last-resort handlers in place. A later copy that never
        // subscribed them makes these removals no-ops.
        TaskScheduler.UnobservedTaskException -= TaskScheduler_UnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException -= CurrentDomain_UnhandledException;
        DispatcherUnhandledException -= App_DispatcherUnhandledException;
        base.OnExit(e);
    }

    /// <summary>
    /// The exit path: the forwarding listener, the log line that marks an ordinary exit, the final power-request
    /// reset and the log flush. Runs once - from <see cref="OnExit"/>, or earlier from a session end
    /// (SP-0120), which has to complete it before it answers Windows.
    /// </summary>
    private void CompleteExit()
    {
        if (Interlocked.Exchange(ref _exitCompleted, 1) != 0)
        {
            return;
        }

        if (Exchange is not null)
        {
            WaitPumping(Exchange.StopAsync(), TimeSpan.FromSeconds(2));
        }

        if (_activationListener is not null)
        {
            // Nothing the listener runs waits on this thread, so blocking here cannot deadlock.
            _activationListener.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _activationListener = null;
        }

        if (_instanceLock is not null)
        {
            _log?.Information("Application shutdown.");
            ThemeService.Shutdown();
            WakeGuard.Reset(); // final safety net: never leave the machine unable to sleep after exit
            _log?.Dispose();
            _instanceLock.Dispose();
            _instanceLock = null;
        }
    }

    /// <summary>
    /// How long a later copy waits for the lock of a copy that refused or did not answer its launch (SP-0170):
    /// the running copy's close work is bounded by <see cref="CloseWorkDeadline"/>, and the lock is released
    /// right after it.
    /// </summary>
    private static readonly TimeSpan LockHandoverWait = CloseWorkDeadline + TimeSpan.FromSeconds(2);

    /// <summary>
    /// SP-0118: this is a later copy. Its request goes to the running one, and it exits with 0. If the
    /// running copy does not answer, it says so and exits - never falling back to a second full instance,
    /// which is what used to overwrite the first one's state.
    /// </summary>
    /// <remarks>
    /// SP-0170: a launch the running copy refuses - it is closing - or does not answer is not lost. The running
    /// copy's lock is waited for, and when it is released this copy becomes the first one and starts with the
    /// launch it was given; that is not a second instance, the first being gone. Only a lock that never
    /// comes free ends in the "did not answer" notice. A launch beyond the receiver's limits, and a lock this
    /// copy may not open (an elevated copy holds it), each get a notice of their own.
    /// </remarks>
    /// <returns><see langword="true"/> when this copy became the first one and <paramref name="acquired"/> holds the lock.</returns>
    private bool HandOverToRunningCopy(
        SingleInstanceIdentity identity,
        IReadOnlyList<string> arguments,
        SingleInstanceLockResult lockResult,
        out SingleInstanceLock? acquired)
    {
        acquired = null;
        ForegroundActivation.AllowAnyProcessToTakeForeground();
        switch (ActivationPipeClient.TrySend(identity.PipeName, arguments, ActivationPipeClient.ConnectTimeout))
        {
            case ActivationSendResult.Delivered:
                Shutdown(0);
                return false;
            case ActivationSendResult.TooLarge:
                EndWithNotice("SecondInstanceLaunchTooLarge");
                return false;
        }

        if (lockResult == SingleInstanceLockResult.Inaccessible)
        {
            EndWithNotice("SecondInstanceElevated");
            return false;
        }

        if (SingleInstanceLock.TryAcquire(identity.MutexName, LockHandoverWait, out acquired)
            == SingleInstanceLockResult.Acquired)
        {
            return true;
        }

        EndWithNotice("SecondInstanceNoAnswer");
        return false;
    }

    /// <summary>
    /// SP-0161 (APP-BEHAVIOUR rule 6): this notice is the one message that shows before the main window
    /// loads, so it reads the chosen language from the catalog state itself - the interface dictionary is
    /// otherwise applied only when that window loads. Ends this copy with exit code 1.
    /// </summary>
    private void EndWithNotice(string messageKey)
    {
        ApplyPersistedLanguage();
        MessageBox.Show(
            LocalizationService.Get(messageKey),
            LocalizationService.Get("ProductName"),
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        Shutdown(1);
    }

    /// <summary>
    /// SP-0161: applies the language the catalog state carries, falling back to the English dictionary
    /// App.xaml merges when the state cannot be read. The fallback is silent on purpose - a second copy
    /// writes nothing (SP-0118), so there is no log to tell and the notice shows either way; a fresh
    /// install has no state file and an English dictionary is what its first window would detect too.
    /// </summary>
    private static void ApplyPersistedLanguage()
    {
        try
        {
            var state = new StreamCatalogStore(AppPaths.DataDirectory).LoadAsync().GetAwaiter().GetResult();
            var language = state.Language
                ?? InterfaceLanguages.Detect(CultureInfo.CurrentUICulture, CultureInfo.InstalledUICulture);
            LocalizationService.Apply(language);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Text.Json.JsonException)
        {
        }
    }

    private void OnForwardedLaunch(IReadOnlyList<string> arguments)
    {
        var request = StreamLaunchRequest.Parse(arguments);
        Dispatcher.BeginInvoke(() => _mainWindow?.ReceiveForwardedLaunch(request));
    }

    private void OnForwardedLaunchRejected(string reason, Exception? exception)
    {
        if (exception is null)
        {
            _log?.Event("LAUNCH FORWARD REJECTED", $"reason={reason}");
        }
        else
        {
            _log?.Error($"Forwarded launch rejected: {reason}", exception);
        }
    }

    /// <summary>
    /// SP-0119 policy (c): an exception that reaches here escaped every <see cref="HandlerBoundary"/>, so
    /// nothing knows what it left half-done - continuing could save inconsistent state. The process ends,
    /// but never silently: the user is told where the log is, the power request is released and the log
    /// is flushed. <see cref="Environment.Exit"/> rather than an orderly shutdown, because the orderly
    /// path runs window-closing code that saves state - the one thing this must not do.
    /// </summary>
    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Terminate("Unhandled WPF dispatcher exception", e.Exception, showNotice: true);
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            // The runtime ends the process after this returns whatever happens; what is left to do is say so.
            Terminate("Unhandled AppDomain exception", exception, showNotice: e.IsTerminating, exit: false);
        }
        else
        {
            _log?.Information("Unhandled AppDomain non-exception error.");
        }
    }

    private void Terminate(string operation, Exception exception, bool showNotice, bool exit = true)
    {
        _log?.Error(operation, exception);
        if (Interlocked.Exchange(ref _terminating, 1) != 0)
        {
            return; // A second fault while the first notice is open: the first path is already ending the process.
        }

        _log?.Event("PROCESS TERMINATING", $"type={exception.GetType().Name}");
        WakeGuard.Reset();
        if (showNotice)
        {
            ShowFatalNoticeWithUiHeld();
        }

        _log?.Dispose();
        if (exit)
        {
            Environment.Exit(1);
        }
    }

    /// <summary>
    /// SP-0178: a message box shown on the UI thread runs a nested message loop, in which every
    /// <c>DispatcherTimer</c> still ticks - the volume, browsing-session and now-playing timers among them,
    /// each of which can save the state SP-0119 forbids saving here. So the UI thread never pumps while the
    /// notice is open: on the UI thread the notice runs on a thread of its own while this one sleeps, and
    /// from any other thread the UI thread is first parked inside a Send-priority callback it never leaves,
    /// which is also where the notice text is read, since resources are UI-thread-affine.
    /// </summary>
    private void ShowFatalNoticeWithUiHeld()
    {
        string? text = null;
        string? caption = null;
        void ReadNotice()
        {
            text = LocalizationService.Format("FatalFaultNotice", AppPaths.DataDirectory);
            caption = LocalizationService.Get("ProductName");
        }

        try
        {
            if (Dispatcher.CheckAccess())
            {
                ReadNotice();
                var noticeThread = new Thread(() => ShowFatalNotice(text, caption)) { IsBackground = true };
                noticeThread.SetApartmentState(ApartmentState.STA);
                noticeThread.Start();
                // Thread.Sleep rather than Join: Join pumps sent messages on an STA thread, Sleep pumps nothing.
                while (noticeThread.IsAlive)
                {
                    Thread.Sleep(50);
                }

                return;
            }

            var ready = new ManualResetEventSlim();
            Dispatcher.BeginInvoke(DispatcherPriority.Send, () =>
            {
                try
                {
                    ReadNotice();
                }
                finally
                {
                    ready.Set();
                }

                // Parked for the rest of the process: the runtime ends it once this handler returns.
                Thread.Sleep(Timeout.Infinite);
            });
            if (!ready.Wait(FatalNoticeUiWait))
            {
                _log?.Information("Fatal fault notice skipped: the UI thread did not respond.");
                return;
            }

            ShowFatalNotice(text, caption);
        }
        catch (Exception noticeFailure)
        {
            _log?.Error("Fatal fault notice failed", noticeFailure);
        }
    }

    /// <summary>How long a non-UI fault waits for the UI thread to be parked before giving up on the notice.</summary>
    private static readonly TimeSpan FatalNoticeUiWait = TimeSpan.FromSeconds(5);

    private void ShowFatalNotice(string? text, string? caption)
    {
        if (text is null || caption is null)
        {
            return;
        }

        try
        {
            MessageBox.Show(text, caption, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception noticeFailure)
        {
            _log?.Error("Fatal fault notice failed", noticeFailure);
        }
    }

    private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e) =>
        _log?.Error("Unobserved task exception", e.Exception);
}
