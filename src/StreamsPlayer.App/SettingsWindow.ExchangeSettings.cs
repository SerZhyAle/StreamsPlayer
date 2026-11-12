using System.Globalization;
using System.Windows;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0260 / <c>DEVICE-EXCHANGE</c> items EN to ER: "Save as STANDARD" and "Read STANDARD" on the Exchange page. Each is
/// the person's own act, asks before it sends, and names the groups it acts on; nothing here runs on its own.
/// </summary>
public partial class SettingsWindow
{
    private bool _exchangeSettingsBusy;

    // The last outcome, kept as its key and moment so a language change re-words it.
    private string? _exchangeSettingsKey;
    private DateTimeOffset? _exchangeSettingsMoment;

    private void InitializeExchangeSettings()
    {
        // A session that ends takes the server's feature list with it; the actions hide with it.
        Exchange.SessionOffline += ExchangeChanged;
        Closed += (_, _) => Exchange.SessionOffline -= ExchangeChanged;
    }

    private void ShowExchangeSettings()
    {
        ExchangeSettingsPanel.Visibility = Exchange.Account.Token is not null ? Visibility.Visible : Visibility.Collapsed;
        var available = Exchange.CanExchangeSettings;
        ExchangeSettingsGroupsPanel.Visibility = ExchangeSettingsActionsPanel.Visibility =
            available ? Visibility.Visible : Visibility.Collapsed;
        ExchangeSettingsSaveButton.IsEnabled = ExchangeSettingsReadButton.IsEnabled = !_exchangeSettingsBusy;
        ExchangeSettingsAppearanceCheckBox.IsEnabled = ExchangeSettingsPlaybackCheckBox.IsEnabled =
            ExchangeSettingsInterfaceCheckBox.IsEnabled = !_exchangeSettingsBusy;
        ExchangeSettingsStatusText.Text = _exchangeSettingsKey is { } key
            ? ExchangeSettingsText(key, _exchangeSettingsMoment)
            : available ? "" : LocalizationService.Get("ExchangeSettingsUnavailable");
    }

    private void ExchangeSettingsSave_Click(object sender, RoutedEventArgs e) =>
        HandlerBoundary.Run(nameof(ExchangeSettingsSave_Click), () => ExchangeSettingsActAsync(save: true));

    private void ExchangeSettingsRead_Click(object sender, RoutedEventArgs e) =>
        HandlerBoundary.Run(nameof(ExchangeSettingsRead_Click), () => ExchangeSettingsActAsync(save: false));

    private async Task ExchangeSettingsActAsync(bool save)
    {
        if (_exchangeSettingsBusy || !Exchange.CanExchangeSettings)
        {
            return;
        }

        var groups = ChosenSettingsGroups();
        if (groups.Count == 0)
        {
            ShowExchangeSettingsOutcome("ExchangeSettingsChooseGroup", null);
            return;
        }

        var names = string.Join(", ", groups.Select(SettingsGroupName));
        var prompt = save
            ? LocalizationService.Format("ExchangeSettingsSaveConfirm", names)
            : LocalizationService.Format("ExchangeSettingsReadConfirm", names);
        if (MessageBox.Show(this, prompt, LocalizationService.Get("ExchangeSettingsTitle"), MessageBoxButton.YesNo,
                save ? MessageBoxImage.Question : MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        _exchangeSettingsBusy = true;
        ShowExchangeSettings();
        try
        {
            if (save)
            {
                var payload = StandardSettings.Serialize(_mainWindow.State, groups);
                var saved = await Exchange.SaveStandardSettingsAsync(payload, CancellationToken.None);
                ShowExchangeSettingsOutcome(saved.MessageKey, saved.UpdatedAt ?? DateTimeOffset.Now);
                return;
            }

            var read = await Exchange.ReadStandardSettingsAsync(CancellationToken.None);
            if (_closed)
            {
                // Item ER: the person who asked is gone with the window; nothing is applied behind their back.
                return;
            }

            if (!read.Succeeded || read.Document is not { } document)
            {
                ShowExchangeSettingsOutcome(read.MessageKey, null);
                return;
            }

            var languageChanged = await _mainWindow.ApplyStandardSettingsAsync(document, groups);
            _exchangeSettingsKey = read.MessageKey;
            _exchangeSettingsMoment = read.UpdatedAt ?? DateTimeOffset.Now;
            ShowAppliedPreferences(languageChanged);
        }
        finally
        {
            _exchangeSettingsBusy = false;
            if (!_closed)
            {
                ShowExchangeSettings();
            }
        }
    }

    /// <summary>The controls of the other pages follow what a read applied; a new language re-words the whole window.</summary>
    private void ShowAppliedPreferences(bool languageChanged)
    {
        if (languageChanged)
        {
            ReapplyLocalization();
            return;
        }

        var wasInitializing = _initializing;
        _initializing = true;
        try
        {
            PopulateOptions();
        }
        finally
        {
            _initializing = wasInitializing;
        }
    }

    private void ShowExchangeSettingsOutcome(string key, DateTimeOffset? moment)
    {
        if (_closed)
        {
            return;
        }

        _exchangeSettingsKey = key;
        _exchangeSettingsMoment = moment;
        ShowExchangeSettings();
    }

    private List<string> ChosenSettingsGroups()
    {
        var groups = new List<string>();
        if (ExchangeSettingsAppearanceCheckBox.IsChecked == true)
        {
            groups.Add(StandardSettings.Appearance);
        }

        if (ExchangeSettingsPlaybackCheckBox.IsChecked == true)
        {
            groups.Add(StandardSettings.Playback);
        }

        if (ExchangeSettingsInterfaceCheckBox.IsChecked == true)
        {
            groups.Add(StandardSettings.Interface);
        }

        return groups;
    }

    private static string SettingsGroupName(string group) => group switch
    {
        StandardSettings.Appearance => LocalizationService.Get("ExchangeSettingsGroupAppearance"),
        StandardSettings.Playback => LocalizationService.Get("ExchangeSettingsGroupPlayback"),
        _ => LocalizationService.Get("ExchangeSettingsGroupInterface")
    };

    private static string ExchangeSettingsText(string key, DateTimeOffset? moment)
    {
        var when = (moment ?? DateTimeOffset.Now).ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        return key switch
        {
            "ExchangeSettingsSavedAt" => LocalizationService.Format("ExchangeSettingsSavedAt", when),
            "ExchangeSettingsReadAt" => LocalizationService.Format("ExchangeSettingsReadAt", when),
            _ => LocalizationService.Get(key)
        };
    }
}
