using System.Windows;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0215 requirement 7: the one prompt a protected broadcast opens - the password its broadcaster
/// told the person, typed once per descriptor (LIVE-BROADCAST item M). The user name is never asked
/// for: the descriptor's <c>auth</c> carries it. The typed value exists only in this window's answer
/// and then under the platform secret store; no log line and no diagnostic entry ever sees it.
/// </summary>
public partial class BroadcastPasswordWindow : Window
{
    private void ExchangeHelp_Click(object sender, RoutedEventArgs e)
        => ExchangeHelp.Open(this, ExchangePortalPage.Broadcasts);

    /// <summary>The password the person typed, or null when the prompt was dismissed without one.</summary>
    public string? Password { get; private set; }

    public BroadcastPasswordWindow(string broadcastTitle)
    {
        InitializeComponent();
        // The prompt names the broadcast, never the user or the scheme's wire value: the person needs
        // to know which password this door wants, not how the challenge is spelled on the wire.
        PromptText.Text = LocalizationService.Format("BroadcastPasswordPrompt", StreamTitleFormatter.Display(broadcastTitle));
        Loaded += (_, _) => PasswordBox.Focus();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        var password = PasswordBox.Password;
        if (password.Length == 0)
        {
            return;
        }

        Password = password;
        DialogResult = true;
    }
}
