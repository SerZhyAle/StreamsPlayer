using System.Runtime.InteropServices;
using System.Windows;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public partial class SettingsWindow
{
    private ExchangeSourceService Exchange => ((App)Application.Current).Exchange;
    private bool _exchangeBusy;
    private bool _showingExchange;

    private void InitializeExchange()
    {
        ExchangeAddressBox.Text = Exchange.Account.Host;
        ExchangePortBox.Text = Exchange.Account.Port == 0 ? "" : Exchange.Account.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ExchangeLoginBox.Text = Exchange.Account.Login;
        Exchange.Changed += ExchangeChanged;
        Closed += (_, _) =>
        {
            Exchange.Changed -= ExchangeChanged;
            ExchangeSecretBox.Clear();
        };
        ShowExchange();
    }

    private void ExchangeChanged()
    {
        if (!Dispatcher.HasShutdownStarted)
        {
            Dispatcher.BeginInvoke(ShowExchange);
        }
    }

    private void ShowExchange()
    {
        _showingExchange = true;
        ExchangeEnabledCheckBox.IsChecked = Exchange.Account.Enabled;
        ExchangeEnabledCheckBox.IsEnabled = !_exchangeBusy && Exchange.Account.Token is not null;
        ExchangeAutoAcceptCastsCheckBox.IsChecked = Exchange.Account.AutoAcceptCasts;
        ExchangeAutoAcceptCastsCheckBox.IsEnabled = !_exchangeBusy && Exchange.Account.Token is not null;
        ExchangeEnrollButton.IsEnabled = !_exchangeBusy;
        ExchangeForgetButton.IsEnabled = !_exchangeBusy;
        ExchangeTrustButton.Visibility = Exchange.Account.Token is not null && Exchange.PresentedFingerprint is not null
            ? Visibility.Visible : Visibility.Collapsed;
        // A failed enrollment's reason is kept above the connection line: the receiver it put back reports
        // "online" within a second, and the reason must not vanish with it.
        var status = LocalizationService.Get(Exchange.StatusKey);
        ExchangeStatusText.Text = Exchange.EnrollmentOutcomeKey is { } outcome && outcome != Exchange.StatusKey
            ? LocalizationService.Get(outcome) + Environment.NewLine + status
            : status;
        ExchangeFingerprintText.Text = Exchange.PresentedFingerprint is { } presented
            ? LocalizationService.Format("ExchangeCertificatePrompt", Exchange.PreviousFingerprint ?? "-", presented)
            : "";
        _showingExchange = false;
    }

    private async Task ExchangeAutoAcceptCastsAsync()
    {
        if (_showingExchange)
        {
            return;
        }

        await RunExchangeActionAsync(() => Exchange.SetAutoAcceptCastsAsync(ExchangeAutoAcceptCastsCheckBox.IsChecked == true));
    }

    private async Task ExchangeEnrollAsync()
    {
        await RunExchangeActionAsync(async () =>
        {
            if (!int.TryParse(ExchangePortBox.Text, out var port) || port is < 1 or > 65535
                || string.IsNullOrWhiteSpace(ExchangeAddressBox.Text) || string.IsNullOrWhiteSpace(ExchangeLoginBox.Text)
                || !HasExchangeProof())
            {
                MessageBox.Show(this, LocalizationService.Get("ExchangeInvalidEntry"), LocalizationService.Get("ExchangeTitle"));
                return;
            }

            var host = ExchangeAddressBox.Text.Trim();
            var pin = await Exchange.InspectCertificateAsync(host, port);
            if (pin is null)
            {
                return;
            }

            if (pin != Exchange.Account.Pin || host != Exchange.Account.Host || port != Exchange.Account.Port)
            {
                if (!ConfirmExchangePin(pin))
                {
                    return;
                }
            }

            char[] characters;
            using (var password = ExchangeSecretBox.SecurePassword)
            {
                var pointer = Marshal.SecureStringToBSTR(password);
                characters = new char[password.Length];
                try
                {
                    Marshal.Copy(pointer, characters, 0, characters.Length);
                }
                catch
                {
                    Array.Clear(characters);
                    throw;
                }
                finally
                {
                    Marshal.ZeroFreeBSTR(pointer);
                }
            }

            try
            {
                ExchangeSecretBox.Clear();
                await Exchange.EnrollAsync(host, port, ExchangeLoginBox.Text.Trim(),
                    ExchangeMethodBox.SelectedIndex == 1, characters, pin);
            }
            finally
            {
                Array.Clear(characters);
            }
        });
        ExchangeSecretBox.Clear();
    }

    private bool ConfirmExchangePin(string pin) => MessageBox.Show(this,
        LocalizationService.Format("ExchangeCertificatePrompt", Exchange.Account.Pin ?? "-", pin),
        LocalizationService.Get("ExchangeTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning,
        MessageBoxResult.No) == MessageBoxResult.Yes;

    private bool HasExchangeProof()
    {
        using var proof = ExchangeSecretBox.SecurePassword;
        return proof.Length > 0;
    }

    private async Task ExchangeTrustAsync()
    {
        if (Exchange.PresentedFingerprint is { } pin && ConfirmExchangePin(pin))
        {
            await RunExchangeActionAsync(() => Exchange.AcceptReplacementPinAsync(pin));
        }
    }

    private async Task ExchangeEnabledAsync()
    {
        if (!_showingExchange)
        {
            var enabled = ExchangeEnabledCheckBox.IsChecked == true;
            await RunExchangeActionAsync(() => Exchange.SetEnabledAsync(enabled));
        }
    }

    private async Task ExchangeForgetAsync()
    {
        if (MessageBox.Show(this, LocalizationService.Get("ExchangeForget"), LocalizationService.Get("ExchangeTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunExchangeActionAsync(Exchange.ForgetAsync);
        if (Exchange.Account.Host.Length == 0)
        {
            ExchangeAddressBox.Clear();
            ExchangePortBox.Clear();
            ExchangeLoginBox.Clear();
        }
        ExchangeSecretBox.Clear();
    }

    private async Task RunExchangeActionAsync(Func<Task> action)
    {
        if (_exchangeBusy)
        {
            return;
        }

        _exchangeBusy = _actionRunning = true;
        ShowExchange();
        try
        {
            await action();
        }
        catch (Exception)
        {
            // Account details and server exceptions never enter the diagnostic log.
            MessageBox.Show(this, LocalizationService.Get("ExchangeStorageFailed"), LocalizationService.Get("ExchangeTitle"));
        }
        finally
        {
            _exchangeBusy = _actionRunning = false;
            ShowExchange();
        }
    }
}
