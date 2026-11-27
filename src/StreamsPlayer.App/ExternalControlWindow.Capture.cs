using System.Windows;
using System.Windows.Controls;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public partial class ExternalControlWindow
{
    internal static ExternalControlWindow ForHelpCapture()
    {
        const string deviceId = "demo-tablet";
        const string deviceName = "Demo Tablet";
        var window = new ExternalControlWindow();
        window.RemoteListeningOption.IsChecked = true;
        window.StateText.SetResourceReference(TextBlock.TextProperty, "RemoteStateReady");
        window.ExchangeAction.Visibility = Visibility.Collapsed;
        window.ConsentSection.Visibility = Visibility.Visible;
        var command = RemoteCommands.Single(command => command.Name == AppPlaybackCommands.PauseVerb);
        window.PopulateConsent(new RemoteConsentQuestion(deviceName, command, [], DateTimeOffset.MaxValue));
        window.ConsentCountdown.Text = LocalizationService.Format("RemotePromptCountdown", (int)RemoteCommandTarget.ConsentWindow.TotalSeconds);
        window.ControllersState.Visibility = Visibility.Collapsed;
        var row = new ControllerRow(window, deviceId, expanded: false);
        row.Update(new ExchangeDeviceRecord(deviceId, deviceName, IsOnline: true) { Product = "streamsplayer-android" }, grant: null);
        window.ControllersList.Children.Add(row.View);
        window.RefreshCommands();
        return window;
    }
}
