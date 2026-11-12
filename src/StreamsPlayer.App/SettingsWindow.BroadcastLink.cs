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

    // The one sentence under the link password: a rejected password the person typed, or the producer's word that the
    // password changed. SP-0252 moved the status line of the page to the broadcast window, so this is all that stays.
    private string? _linkNoticeKey;

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
        var notice = _linkNoticeKey ?? (Broadcast.OutcomeKey == "BroadcastLinkChanged" ? Broadcast.OutcomeKey : null);
        BroadcastLinkStatusText.Text = notice is null ? string.Empty : LocalizationService.Get(notice);
        BroadcastLinkStatusText.Visibility = notice is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BroadcastLinkChoice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_showingBroadcast || BroadcastLinkChoiceBox.SelectedIndex < 0)
        {
            return;
        }

        _linkNoticeKey = null;
        var choice = LinkChoices[BroadcastLinkChoiceBox.SelectedIndex];
        if (choice == Broadcast.Settings.Choice)
        {
            return;
        }

        if (!ConfirmLiveBroadcastChange("BroadcastConfirmRevokeLink"))
        {
            ShowBroadcasting();
            return;
        }

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
            _linkNoticeKey = "BroadcastLinkPasswordInvalid";
            ShowBroadcasting();
            return;
        }

        if (!ConfirmLiveBroadcastChange("BroadcastConfirmRevokeLink"))
        {
            return;
        }

        _linkNoticeKey = null;
        HandlerBoundary.Run(nameof(BroadcastLinkSave_Click), async () =>
        {
            await Broadcast.SetLinkChoiceAsync(BroadcastLinkChoice.Custom, typed);
            BroadcastLinkPasswordBox.Clear();
            ShowBroadcasting();
        });
    }

    private void BroadcastLinkRenew_Click(object sender, RoutedEventArgs e)
    {
        if (ConfirmLiveBroadcastChange("BroadcastConfirmRevokeLink"))
        {
            HandlerBoundary.Run(nameof(BroadcastLinkRenew_Click), () => Broadcast.RenewLinkPasswordAsync());
        }
    }
}
