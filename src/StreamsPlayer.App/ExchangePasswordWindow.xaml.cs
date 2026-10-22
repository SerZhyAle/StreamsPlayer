using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0221 requirement 5: the account's password changed from a signed-in device (DEVICE-EXCHANGE item AT). The
/// old password and the new one twice, the policy stated above the fields, one request at a time (item AW) - the
/// buttons stay disabled until the answer, and a slow answer is shown as waiting and never sent again. The typed
/// values live in the boxes and in character arrays that are cleared the moment the request has been written;
/// they are never kept in a string, a field or a log.
/// </summary>
public partial class ExchangePasswordWindow : Window
{
    private void ExchangeHelp_Click(object sender, RoutedEventArgs e)
        => ExchangeHelp.Open(this, ExchangePortalPage.Account);

    private readonly ExchangeSourceService _exchange;
    private CancellationTokenSource? _waiting;
    private bool _busy;

    internal ExchangePasswordWindow(ExchangeSourceService exchange)
    {
        _exchange = exchange;
        InitializeComponent();
        Loaded += (_, _) => OldBox.Focus();
        Closed += (_, _) =>
        {
            OldBox.Clear();
            NewBox.Clear();
            AgainBox.Clear();
        };
    }

    private async void Confirm_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_busy)
            {
                return;
            }

            await ChangeAsync();
        }
        catch (Exception exception)
        {
            // The service reports its own refusals as answers; this is a fault in the dialog's own handler.
            HandlerBoundary.Report(nameof(Confirm_Click), exception);
            SetBusy(false);
        }
    }

    private async Task ChangeAsync()
    {
        using var old = OldBox.SecurePassword;
        using var fresh = NewBox.SecurePassword;
        using var again = AgainBox.SecurePassword;
        if (old.Length == 0 || fresh.Length == 0)
        {
            StatusText.Text = LocalizationService.Get("ExchangePasswordEmpty");
            return;
        }

        var oldCharacters = Read(old);
        var newCharacters = Read(fresh);
        var againCharacters = Read(again);
        try
        {
            if (!newCharacters.AsSpan().SequenceEqual(againCharacters))
            {
                StatusText.Text = LocalizationService.Get("ExchangePasswordMismatch");
                Array.Clear(oldCharacters);
                Array.Clear(newCharacters);
                return;
            }
        }
        finally
        {
            Array.Clear(againCharacters);
        }

        StatusText.Text = LocalizationService.Get("ExchangePasswordWaiting");
        SetBusy(true);
        _waiting = new CancellationTokenSource();
        ExchangePasswordChangeResult result;
        try
        {
            // The service clears both arrays once the request is written; whatever happens next, they are cleared.
            result = await _exchange.ChangePasswordAsync(oldCharacters, newCharacters, _waiting.Token);
        }
        finally
        {
            Array.Clear(oldCharacters);
            Array.Clear(newCharacters);
            _waiting.Dispose();
            _waiting = null;
        }

        SetBusy(false);
        StatusText.Text = LocalizationService.Get(result.Key);
        if (result.Key == ExchangeFailureRows.Unanswered.MessageKey)
        {
            // The row is shown as the table words it (item DA: never joined with other text); that nothing was
            // sent again is this window's own note, on its own line.
            StatusText.Text += Environment.NewLine + LocalizationService.Get("ExchangePasswordNotResent");
        }

        if (result.Changed)
        {
            OldBox.Clear();
            NewBox.Clear();
            AgainBox.Clear();
            // The success sentence stays on screen and the dialog turns into its own acknowledgement.
            ConfirmButton.Visibility = Visibility.Collapsed;
            CancelButton.Content = LocalizationService.Get("Close");
            CancelButton.Focus();
        }
        else
        {
            // A refusal left everything as it was: the person may correct and send one more request.
            OldBox.Clear();
            OldBox.Focus();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        // Stops waiting; the request, if it left, is not sent again and its answer is still read by the service.
        _waiting?.Cancel();
        if (!_busy)
        {
            Close();
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        OldBox.IsEnabled = NewBox.IsEnabled = AgainBox.IsEnabled = !busy;
        ConfirmButton.IsEnabled = !busy;
        WaitingBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private static char[] Read(SecureString secure)
    {
        var characters = new char[secure.Length];
        var pointer = Marshal.SecureStringToBSTR(secure);
        try
        {
            Marshal.Copy(pointer, characters, 0, characters.Length);
            return characters;
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
}
