using System.Windows;
using System.Windows.Controls;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// LIVE-BROADCAST item R in Settings: the person chooses the password of the outward link - made for each broadcast,
/// set by the person, or none. The choice applies to the next broadcast and, with one on the air, to it at once (the
/// previous password then stops working). A password the person types goes to the platform secret store and
/// nowhere else; this window never shows or logs it.
/// </summary>
public partial class SettingsWindow
{
    private static readonly BroadcastLinkChoice[] LinkChoices =
        [BroadcastLinkChoice.Generated, BroadcastLinkChoice.Custom, BroadcastLinkChoice.None];

    private void PopulateBroadcastLink()
    {
        _showingBroadcast = true;
        BroadcastLinkChoiceBox.ItemsSource = new[]
        {
            LocalizationService.Get("BroadcastLinkGenerated"),
            LocalizationService.Get("BroadcastLinkCustom"),
            LocalizationService.Get("BroadcastLinkNone")
        };
        _showingBroadcast = false;
    }

    private void ShowBroadcastLinkState(IReadOnlyCollection<BroadcastSource> running)
    {
        var choice = Broadcast.Settings.Choice;
        BroadcastLinkChoiceBox.SelectedIndex = Array.IndexOf(LinkChoices, choice);
        BroadcastLinkCustomPanel.Visibility = choice == BroadcastLinkChoice.Custom ? Visibility.Visible : Visibility.Collapsed;
        BroadcastLinkNoneText.Visibility = choice == BroadcastLinkChoice.None ? Visibility.Visible : Visibility.Collapsed;
        BroadcastLinkRenewButton.Visibility = choice == BroadcastLinkChoice.Generated ? Visibility.Visible : Visibility.Collapsed;
        BroadcastLinkRenewButton.IsEnabled = running.Count > 0;
    }

    private void BroadcastLinkChoice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_showingBroadcast || BroadcastLinkChoiceBox.SelectedIndex < 0)
        {
            return;
        }

        var choice = LinkChoices[BroadcastLinkChoiceBox.SelectedIndex];
        HandlerBoundary.Run(nameof(BroadcastLinkChoice_Changed), async () =>
        {
            await Broadcast.SetLinkChoiceAsync(choice, customPassword: null);
            ShowBroadcasting();
        });
    }

    private void BroadcastLinkSave_Click(object sender, RoutedEventArgs e)
    {
        var typed = BroadcastLinkPasswordBox.Password;
        if (!BroadcastLinkPassword.IsAcceptable(typed))
        {
            BroadcastStatusText.Text = LocalizationService.Get("BroadcastLinkPasswordInvalid");
            return;
        }

        HandlerBoundary.Run(nameof(BroadcastLinkSave_Click), async () =>
        {
            await Broadcast.SetLinkChoiceAsync(BroadcastLinkChoice.Custom, typed);
            BroadcastLinkPasswordBox.Clear();
            ShowBroadcasting();
        });
    }

    private void BroadcastLinkRenew_Click(object sender, RoutedEventArgs e) =>
        HandlerBoundary.Run(nameof(BroadcastLinkRenew_Click), () => Broadcast.RenewLinkPasswordAsync());
}
