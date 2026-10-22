using System.Runtime.InteropServices;
using System.Windows;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public partial class SettingsWindow
{
    private ExchangeSourceService Exchange => ((App)Application.Current).Exchange;
    private bool _exchangeBusy;
    private bool _showingExchange;
    private CancellationTokenSource? _exchangeCancellation;

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
        InitializeExchangeCamera();
        ShowExchange();
        if (Exchange.Account is { SignedOut: true, Token: null })
        {
            // Item AT: the entry kept its server and login, so the one thing left to type is the password.
            Loaded += (_, _) => ExchangeSecretBox.Focus();
        }
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
        ExchangeForgetButton.IsEnabled = !_exchangeBusy && Exchange.HasAccount;
        // Item AT: a password is changed from a device that is signed in; with no session the dialog would only fail.
        ExchangeChangePasswordButton.IsEnabled = !_exchangeBusy && Exchange.Account.Token is not null;
        ExchangeAddressBox.IsEnabled = ExchangePortBox.IsEnabled = ExchangeLoginBox.IsEnabled = !_exchangeBusy;
        ExchangeSecretBox.IsEnabled = ExchangeMethodBox.IsEnabled = !_exchangeBusy;
        ExchangeImportStringButton.IsEnabled = ExchangeImportFileButton.IsEnabled = !_exchangeBusy;
        ExchangeImportQrFileButton.IsEnabled = ExchangeImportQrClipboardButton.IsEnabled = !_exchangeBusy;
        ShowExchangeCameraDoor();
        ExchangeReadStringButton.IsEnabled = ExchangeStringBox.IsEnabled = !_exchangeBusy;
        ExchangeCancelButton.Visibility = _exchangeCancellation is not null ? Visibility.Visible : Visibility.Collapsed;
        ExchangeProgress.Visibility = _exchangeCancellation is not null ? Visibility.Visible : Visibility.Collapsed;
        ExchangeTrustButton.Visibility = _importedExchange?.Fingerprint is null && Exchange.Account.Token is not null && Exchange.PresentedFingerprint is not null
            ? Visibility.Visible : Visibility.Collapsed;
        // A failed enrollment's reason is kept above the connection line: the receiver it put back reports
        // "online" within a second, and the reason must not vanish with it.
        var status = LocalizationService.Get(Exchange.StatusKey);
        ExchangeStatusText.Text = Exchange.EnrollmentOutcomeKey is { } outcome && outcome != Exchange.StatusKey
            ? LocalizationService.Get(outcome) + Environment.NewLine + status
            : status;
        if (Exchange.Account is { SignedOut: true, Token: null })
        {
            // Item AT: the account ended this device's sign-in. The server and login stay in the form; the
            // person is told what to type, not that something failed.
            ExchangeStatusText.Text += Environment.NewLine + LocalizationService.Get("ExchangeSignInAgain");
        }

        if (Exchange.OwnKeyMissing)
        {
            // SP-0226 / item DF: an entry made before keys existed works as it did, but cannot take part in protected
            // content. It cannot be given a key silently (the password is never kept, and a known id cannot take one), so the
            // person is told plainly what to do.
            ExchangeStatusText.Text += Environment.NewLine + LocalizationService.Get("ExchangeE2eOwnKeyMissing");
        }

        ExchangeFingerprintText.Text = Exchange.PresentedFingerprint is { } presented
            ? LocalizationService.Format("ExchangeCertificatePrompt", Exchange.PreviousFingerprint ?? "-", presented)
            : "";
        ShowExchangePrompt();
        ShowGuestEntries();
        UpdateBroadcastGuestNote();
        _showingExchange = false;
    }

    /// <summary>
    /// SP-0221 requirement 3: after a door was read the face says which server the string names and how its
    /// certificate will be trusted. The address and port are not on the face (they are one control away, in the
    /// details), and the name is the string's own unverified word - never a title and never an identity.
    /// </summary>
    private void ShowExchangePrompt()
    {
        if (_importedExchange is not { } data)
        {
            ExchangePreview.Visibility = Visibility.Collapsed;
            return;
        }

        ExchangePreview.Visibility = Visibility.Visible;
        ExchangePreviewText.Text = LocalizationService.Get("ExchangeStringReview");
        ExchangeNameText.Text = data.Name is { } name
            ? LocalizationService.Format("ExchangeServerNamed", name)
            : LocalizationService.Get("ExchangeServerUnnamed");
        var known = Exchange.HasAccount && ExchangeAccountIdentity.SameEndpoint(data.Host, data.Port,
            Exchange.Account.Host, Exchange.Account.Port) && Exchange.Account.Pin is not null;
        ExchangeTrustText.Text = LocalizationService.Get(data.Fingerprint is not null ? "ExchangeTrustPinned"
            : known ? "ExchangeTrustKnown" : "ExchangeTrustAsk");
        ExchangeInvitationText.Text = LocalizationService.Get("ExchangeInvitationNote");
        ExchangeInvitationText.Visibility = data.HasInvitation ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowGuestEntries()
    {
        ExchangeGuestBoundText.Text = LocalizationService.Format("ExchangeGuestBound", ExchangeEntryLimits.MaximumGuestEntries);
        ExchangeGuestList.Children.Clear();
        var entries = Exchange.Guests.Entries;
        if (entries.Count == 0)
        {
            ExchangeGuestList.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = LocalizationService.Get("ExchangeGuestEmpty"), TextWrapping = TextWrapping.Wrap
            });
        }

        foreach (var entry in entries)
        {
            // The login names the entry; a host that is a name (not an address) tells two servers apart, and
            // the full endpoint stays in the tooltip, out of the way (requirement 3's spirit for a list).
            var isAddress = System.Net.IPAddress.TryParse(entry.Host.Trim('[', ']'), out _);
            var shown = isAddress ? entry.Login : entry.Login + " @ " + entry.Host;
            var label = LocalizationService.Format("ExchangeGuestLabel", shown);
            var row = new System.Windows.Controls.DockPanel { Margin = new Thickness(0, 2, 0, 2), LastChildFill = true };
            var signOut = new System.Windows.Controls.Button
            {
                Style = (Style)FindResource("RemoveGlyphButton"),
                Content = LocalizationService.Get("ExchangeForget"),
                Tag = entry,
                IsEnabled = !_exchangeBusy,
                Margin = new Thickness(8, 0, 0, 0)
            };
            System.Windows.Automation.AutomationProperties.SetName(signOut,
                label + " - " + LocalizationService.Get("ExchangeForget"));
            signOut.Click += ExchangeGuestSignOut_Click;
            System.Windows.Controls.DockPanel.SetDock(signOut, System.Windows.Controls.Dock.Right);
            row.Children.Add(signOut);
            // SP-0234, EXCHANGE-MARK rule 8b: each account entry leads with the mark, the flat 16 px drawing. The
            // label beside it names the account, so the mark is decoration and stays out of the accessibility tree.
            var mark = new ExchangeMarkIcon { DipSize = 16, Margin = new Thickness(0, 0, 6, 0) };
            System.Windows.Controls.DockPanel.SetDock(mark, System.Windows.Controls.Dock.Left);
            row.Children.Add(mark);
            row.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
                ToolTip = AccountDisplay(entry)
            });
            ExchangeGuestList.Children.Add(row);
        }

        ExchangeGuestStatusText.Text = Exchange.GuestOutcomeKey is { } key ? LocalizationService.Get(key) : "";
    }

    private void ExchangeGuestSignOut_Click(object sender, RoutedEventArgs e) =>
        HandlerBoundary.Run(nameof(ExchangeGuestSignOut_Click), async () =>
        {
            if (sender is not FrameworkElement { Tag: ExchangeAccount entry } || _exchangeBusy)
            {
                return;
            }

            if (MessageBox.Show(this, LocalizationService.Format("ExchangeGuestSignOutPrompt", AccountDisplay(entry)),
                    LocalizationService.Get("ExchangeTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question,
                    MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                return;
            }

            await RunExchangeActionAsync(() => Exchange.SignOutGuestAsync(entry));
        });

    private void ExchangeChangePassword_Click(object sender, RoutedEventArgs e) =>
        HandlerBoundary.Run(nameof(ExchangeChangePassword_Click), () =>
        {
            if (_exchangeBusy || Exchange.Account.Token is null)
            {
                return Task.CompletedTask;
            }

            var dialog = new ExchangePasswordWindow(Exchange) { Owner = this };
            dialog.ShowDialog();
            ShowExchange();
            return Task.CompletedTask;
        });

    /// <summary>
    /// SP-0225 requirement 2: the connection check runs because this button was pressed, never before. It looks at
    /// the server the form names (the typed or imported one), and reads the account's own lines only when that is
    /// the account's server.
    /// </summary>
    private void ExchangeDiagnose_Click(object sender, RoutedEventArgs e)
    {
        var typed = ExchangeAddressBox.Text.Trim();
        var hasTyped = typed.Length > 0 && int.TryParse(ExchangePortBox.Text, out var typedPort) && typedPort is >= 1 and <= 65535;
        var host = hasTyped ? ExchangeAccountIdentity.NormalizeHost(typed) : Exchange.Account.Host;
        var port = hasTyped ? int.Parse(ExchangePortBox.Text, System.Globalization.CultureInfo.InvariantCulture) : Exchange.Account.Port;
        var sameAsAccount = Exchange.HasAccount
            && ExchangeAccountIdentity.SameEndpoint(host, port, Exchange.Account.Host, Exchange.Account.Port);
        // A failed sign-in leaves no account: its outcome line is about the server the attempt addressed, which is the
        // one the form names when the endpoint is unchanged since. The connection line is the account's alone.
        var attempted = Exchange.LastAttemptEndpoint is { } last && ExchangeAccountIdentity.SameEndpoint(host, port, last.Host, last.Port);
        var request = ExchangeDiagnosisRequest.ForStatus(Exchange.EnrollmentOutcomeKey, sameAsAccount ? Exchange.StatusKey : string.Empty,
            host, port, sameAsAccount || attempted);
        new ExchangeDiagnosisWindow(request) { Owner = this }.ShowDialog();
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
        using var cancellation = new CancellationTokenSource();
        if (_exchangeBusy) return;
        var joined = false;
        _exchangeCancellation = cancellation;
        try
        {
            await RunExchangeActionAsync(async () =>
            {
                if (!int.TryParse(ExchangePortBox.Text, out var port) || port is < 1 or > 65535
                    || string.IsNullOrWhiteSpace(ExchangeAddressBox.Text) || string.IsNullOrWhiteSpace(ExchangeLoginBox.Text)
                    || !HasExchangeProof())
                {
                    if (string.IsNullOrWhiteSpace(ExchangeAddressBox.Text) || !int.TryParse(ExchangePortBox.Text, out _))
                    {
                        // Typing the server by hand is the details' job; open them so the person finds the fields.
                        ExchangeDetailsExpander.IsExpanded = true;
                    }

                    MessageBox.Show(this, LocalizationService.Get("ExchangeInvalidEntry"), LocalizationService.Get("ExchangeTitle"));
                    return;
                }

                var host = ExchangeAddressBox.Text.Trim();
                var login = ExchangeLoginBox.Text.Trim();
                ExchangeAccount? accountToLeave = null;
                if (Exchange.RequiresLeave(host, port, login))
                {
                    accountToLeave = Exchange.Account;
                    if (MessageBox.Show(this, LocalizationService.Format("ExchangeReplacePrompt", AccountDisplay(accountToLeave)),
                            LocalizationService.Get("ExchangeTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning,
                            MessageBoxResult.No) != MessageBoxResult.Yes)
                    {
                        return;
                    }
                }

                var requiredPin = _importedExchange?.Fingerprint;
                var probe = await Exchange.ProbeCertificateAsync(host, port, requiredPin, cancellation.Token);
                if (probe is null)
                {
                    return;
                }

                var pin = probe.Fingerprint;
                // A string's fp is the person's acceptance of that leaf unless the entry already holds a pin with
                // another key (item BN): then it is a changed certificate and is confirmed like any other.
                var needsConfirmation = requiredPin is null
                    ? pin != Exchange.Account.Pin
                        || !ExchangeAccountIdentity.SameEndpoint(host, port, Exchange.Account.Host, Exchange.Account.Port)
                    : probe.ReplacesHeldPin;
                if (needsConfirmation && !ConfirmExchangePin(pin))
                {
                    return;
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
                    await Exchange.EnrollAsync(host, port, login,
                        ExchangeMethodBox.SelectedIndex == 1, characters, pin, accountToLeave, cancellation.Token,
                        requireImportedPin: requiredPin is not null, pinReplacementConfirmed: needsConfirmation);
                    joined = Exchange.EnrollmentOutcomeKey is null && Exchange.Account.Token is not null;
                    if (Exchange.GuestOutcomeKey == "ExchangeGuestSignedIn" && Exchange.EnrollmentOutcomeKey is null
                        && Exchange.Account.Token is null)
                    {
                        // A guest account went to the list below; the form above is the own entry's and starts over.
                        _importedExchange = null;
                        ExchangeAddressBox.Clear();
                        ExchangePortBox.Clear();
                        ExchangeLoginBox.Clear();
                        ExchangeDetailsExpander.IsExpanded = false;
                    }

                    if (accountToLeave is not null && Exchange.LeaveOutcomeKey == "ExchangeForgottenOffline")
                    {
                        MessageBox.Show(this, LocalizationService.Get("ExchangeForgottenOffline"),
                            LocalizationService.Get("ExchangeTitle"));
                    }
                }
                finally
                {
                    Array.Clear(characters);
                }
            });
        }
        finally
        {
            _exchangeCancellation = null;
            ShowExchange();
            ExchangeSecretBox.Clear();
        }
        if (joined) _mainWindow.OpenExchangeBroadcasts(this);
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
        if (_exchangeBusy || !Exchange.HasAccount)
        {
            return;
        }

        if (MessageBox.Show(this, LocalizationService.Format("ExchangeLeavePrompt", AccountDisplay(Exchange.Account)), LocalizationService.Get("ExchangeTitle"),
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

    private static string AccountDisplay(ExchangeAccount account)
    {
        var host = account.Host.Contains(':') ? "[" + account.Host.Trim('[', ']') + "]" : account.Host;
        return host + ":" + account.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " (" + account.Login + ")";
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
        catch (ExchangeCertificateException exception)
        {
            MessageBox.Show(this, LocalizationService.Format("ExchangeStringMismatchPrompt",
                exception.Previous ?? "-", exception.Presented), LocalizationService.Get("ExchangeTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
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
