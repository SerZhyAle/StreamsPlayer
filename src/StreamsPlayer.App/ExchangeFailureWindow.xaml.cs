using System.Windows;
using System.Windows.Input;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public partial class ExchangeFailureWindow : Window
{
    internal ExchangeFailureWindow(ExchangeFailureRow row)
    {
        InitializeComponent();
        HappenedText.Text = LocalizationService.Get(row.MessageKey);
        TryText.Text = row.Step is { } step ? LocalizationService.Get(ExchangeDiagnosisSteps.MessageKey(step)) : "";
        TryText.Visibility = row.Step is null ? Visibility.Collapsed : Visibility.Visible;
        CodeText.Text = LocalizationService.Format("ExchangeDiagnosisCodeLine", row.Code);
        HandlerBoundary.Record($"EXCHANGE FAILURE code={row.Code}");
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }
}
