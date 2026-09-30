using System.Windows;

namespace StreamsPlayer.App;

/// <summary>
/// The failure boundary every asynchronous UI handler in the application runs inside (SP-0119).
/// </summary>
/// <remarks>
/// Almost every user action is an <c>async void</c> handler, and an exception that escapes one reaches the
/// dispatcher's last-resort handler - which, by the SP-0119 policy, ends the process, because an exception
/// raised outside every boundary may have left shared state half-written. Inside a boundary the scope is
/// known: one handler failed, so that one action is abandoned, the fault is logged, and the user is told
/// once, while the rest of the application keeps running.
/// <para>
/// Two shapes satisfy the source gate in the test project (<c>HandlerBoundarySourceTests</c>): an
/// <c>async void</c> method whose whole body is a <c>try</c> with an unfiltered <c>catch (Exception)</c>
/// that calls <see cref="Report"/>, or a delegate handed to <see cref="Run"/> instead of an
/// <c>async</c> lambda.
/// </para>
/// </remarks>
internal static class HandlerBoundary
{
    private static readonly HashSet<string> Notified = new(StringComparer.Ordinal);
    private static CurrentLog? _log;
    private static bool _noticeOpen;

    /// <summary>Wires the boundary to the session log; called once, before the first window exists.</summary>
    public static void Initialize(CurrentLog log) => _log = log;

    /// <summary>Runs an asynchronous handler body inside the boundary. Use in place of an <c>async</c> lambda.</summary>
    public static async void Run(string handler, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            Report(handler, exception);
        }
    }

    /// <summary>
    /// Records that <paramref name="handler"/> failed and tells the user. Never throws.
    /// A callback behind a decorative feature - a backdrop, a thumbnail - passes <c>notifyUser: false</c>
    /// (SP-0166): the fault is logged in full and costs only that feature, with no dialog over it.
    /// </summary>
    /// <remarks>
    /// A cancellation is an ending the user or the code asked for, not a fault, so it is logged and nothing
    /// is shown. The notice is shown at most once per handler per session and never while another one is
    /// open: a failing timer tick would otherwise stack a dialog every few seconds.
    /// </remarks>
    public static void Report(string handler, Exception exception, bool notifyUser = true)
    {
        try
        {
            if (exception is OperationCanceledException)
            {
                _log?.Event("HANDLER CANCELLED", $"handler={handler}");
                return;
            }

            _log?.Event("HANDLER FAULT", $"handler={handler}", $"type={exception.GetType().Name}");
            _log?.Error($"Handler {handler} failed", exception);
            if (notifyUser)
            {
                Notify(handler);
            }
        }
        catch (Exception reportFailure)
        {
            // The boundary is the last thing standing between the fault and the process; it cannot fail too.
            _log?.Error("Handler fault report failed", reportFailure);
        }
    }

    private static void Notify(string handler)
    {
        var application = Application.Current;
        if (application is null ||
            application.Dispatcher.HasShutdownStarted ||
            !application.Dispatcher.CheckAccess() ||
            _noticeOpen ||
            !Notified.Add(handler))
        {
            return;
        }

        _noticeOpen = true;
        try
        {
            var text = LocalizationService.Get("HandlerFaultNotice") + Environment.NewLine + Environment.NewLine +
                       LocalizationService.Get("FailureCauseUnknown");
            var title = LocalizationService.Get("ProductName");
            var owner = application.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive && window.IsVisible)
                        ?? (application.MainWindow is { IsVisible: true } main ? main : null);
            if (owner is null)
            {
                MessageBox.Show(text, title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show(owner, text, title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            _noticeOpen = false;
        }
    }
}
