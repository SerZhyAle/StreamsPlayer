using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace StreamsPlayer.App;

public partial class RemoteCommandPrompt : Window
{
    private readonly RemoteConsentQuestion _question;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    private RemoteCommandPrompt(RemoteConsentQuestion question)
    {
        InitializeComponent();
        _question = question;
        Title = RemoteControlText.Title(question);
        QuestionText.Text = RemoteControlText.Command(question);
        KindText.Text = RemoteControlText.Kind(question);
        ShowCountdown();
        _timer.Tick += (_, _) => ShowCountdown();
        Closed += (_, _) =>
        {
            _timer.Stop();
            _question.Finish(false);
        };
    }

    internal static async Task ShowQuestionAsync(Window? owner, RemoteConsentQuestion question)
    {
        if (question.Answer.IsCompleted)
        {
            return;
        }

        var prompt = new RemoteCommandPrompt(question);
        if (owner is { IsVisible: true, WindowState: not WindowState.Minimized })
        {
            prompt.Owner = owner;
        }
        else
        {
            prompt.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        try
        {
            prompt.Show();
            if (prompt.IsVisible)
            {
                prompt._timer.Start();
            }

            await question.Answer;
        }
        finally
        {
            question.Finish(false);
            if (prompt.IsVisible)
            {
                prompt.Close();
            }
        }
    }

    private void ShowCountdown() => CountdownText.Text = RemoteControlText.Countdown(_question);

    private void Window_Activated(object? sender, EventArgs e) => DeclineButton.Focus();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Escape)
        {
            e.Handled = true;
            _question.Finish(false);
        }
    }

    private void Allow_Click(object sender, RoutedEventArgs e) => _question.Finish(true);

    private void Decline_Click(object sender, RoutedEventArgs e) => _question.Finish(false);
}
