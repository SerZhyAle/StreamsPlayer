using System.Windows;
using System.Windows.Controls;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0225: the "won't connect" check. It runs only because a person pressed a button that opens it, walks the
/// ladder of <c>DEVICE-EXCHANGE</c> item CY for the failure the screen showed, stops at the first step that does not
/// pass and says it in two parts (item DA): what happened, then what to try. The address, the code and the steps
/// with their states sit behind the details control. The result goes to the session log as one redacted line.
/// </summary>
public partial class ExchangeDiagnosisWindow : Window
{
    private readonly ExchangeDiagnosisRequest _request;
    private readonly Func<IExchangeDiagnosisProbes> _probes;
    private CancellationTokenSource? _run;
    private ExchangeDiagnosisResult? _result;
    private ExchangeDiagnosisStep? _shown;

    internal ExchangeDiagnosisWindow(ExchangeDiagnosisRequest request, Func<IExchangeDiagnosisProbes>? probes = null)
    {
        _request = request;
        _probes = probes ?? (() => new ExchangeDiagnosisProbes());
        InitializeComponent();
        HappenedText.Text = HappenedLine(request);
        Loaded += (_, _) => HandlerBoundary.Run(nameof(ExchangeDiagnosisWindow) + ".Run", RunAsync);
        Closed += (_, _) =>
        {
            _run?.Cancel();
            _run?.Dispose();
        };
    }

    // The line of "what happened" as the screen showed it: a failed broadcast's line names the broadcast.
    private static string HappenedLine(ExchangeDiagnosisRequest request) => request.Subject is null
        ? LocalizationService.Get(request.HappenedKey) : LocalizationService.Format(request.HappenedKey, request.Subject);

    private async Task RunAsync()
    {
        _run?.Cancel();
        _run?.Dispose();
        var run = _run = new CancellationTokenSource();
        _result = null;
        _shown = null;
        SetRunning(true);
        try
        {
            if (_request.Connected || _request.Host.Length == 0 || _request.Port is < 1 or > 65535)
            {
                // Nothing to walk: the server is connected, or no server is known yet.
                ShowWithoutWalk();
                return;
            }

            var result = await ExchangeDiagnosisEngine.RunAsync(_request.Row, _request.Host, _request.Port, _probes(),
                run.Token);
            if (run.IsCancellationRequested)
            {
                return;
            }

            _result = result;
            HandlerBoundary.Record(result.ToRedactedLine());
            Show(result);
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested)
        {
            // The window closed or the check restarted; a newer run owns the face.
        }
        finally
        {
            if (ReferenceEquals(_run, run))
            {
                SetRunning(false);
            }
        }
    }

    private void ShowWithoutWalk()
    {
        HappenedText.Text = _request.Connected ? HappenedLine(_request) : LocalizationService.Get("ExchangeDiagnosisNoServer");
        TryPanel.Visibility = Visibility.Collapsed;
        NextButton.Visibility = Visibility.Collapsed;
        DetailsExpander.Visibility = Visibility.Collapsed;
    }

    private void Show(ExchangeDiagnosisResult result)
    {
        HappenedText.Text = HappenedLine(_request);
        ServerText.Text = LocalizationService.Format("ExchangeDiagnosisServerLine", _request.Endpoint);
        CodeText.Text = LocalizationService.Format("ExchangeDiagnosisCodeLine", result.Row.Code);
        StepList.Items.Clear();
        foreach (var item in result.Steps.Where(item => item.State != ExchangeDiagnosisState.NotApplicable))
        {
            StepList.Items.Add(new TextBlock
            {
                Text = LocalizationService.Get(StateKey(item.State)) + " - " +
                    LocalizationService.Get(ExchangeDiagnosisSteps.MessageKey(item.Step)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 3)
            });
        }

        DetailsExpander.Visibility = Visibility.Visible;
        ShowStep(result.AdviceStep);
    }

    private void ShowStep(ExchangeDiagnosisStep? step)
    {
        _shown = step;
        TryPanel.Visibility = step is null ? Visibility.Collapsed : Visibility.Visible;
        if (step is { } shown)
        {
            TryText.Text = LocalizationService.Get(ExchangeDiagnosisSteps.MessageKey(shown));
        }

        NextButton.Visibility = step is { } current && _result?.NextAfter(current) is not null
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetRunning(bool running)
    {
        RunningBar.Visibility = RunningText.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        AgainButton.IsEnabled = !running;
        if (running)
        {
            TryPanel.Visibility = Visibility.Collapsed;
            NextButton.Visibility = Visibility.Collapsed;
        }
    }

    private static string StateKey(ExchangeDiagnosisState state) => state switch
    {
        ExchangeDiagnosisState.Passed => "ExchangeDiagnosisStatePassed",
        ExchangeDiagnosisState.Failed => "ExchangeDiagnosisStateFailed",
        ExchangeDiagnosisState.CouldNotCheck => "ExchangeDiagnosisStateOffered",
        _ => "ExchangeDiagnosisStateNotReached"
    };

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_shown is { } current && _result?.NextAfter(current) is { } next)
        {
            ShowStep(next);
        }
    }

    private void Again_Click(object sender, RoutedEventArgs e) =>
        HandlerBoundary.Run(nameof(ExchangeDiagnosisWindow) + ".Again", RunAsync);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ExchangeHelp_Click(object sender, RoutedEventArgs e) => ExchangeHelp.Open(this, ExchangePortalPage.Home);
}
