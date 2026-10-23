using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0239 P2: the player's hardware keyboard. The decision is <see cref="PlayerKeys.Resolve"/> in Core; this
/// partial only turns a WPF key press into its input, runs the command it names, and reveals the control panel
/// so the effect is visible. Space and Enter stay with the focused control.
/// </summary>
public partial class PlayerWindow
{
    private const double KeyboardVolumeStep = 10;

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        var command = PlayerKeys.Resolve(new PlayerKeyInput(
            MapKey(e.Key == Key.System ? e.SystemKey : e.Key),
            MapModifiers(Keyboard.Modifiers),
            e.IsRepeat,
            KeyboardIsOwnedElsewhere(),
            e.Handled,
            _fullscreen));
        if (command != PlayerKeyCommand.None && RunKeyCommand(command))
        {
            e.Handled = true;
        }
    }

    // False when the command had nothing to act on, so the press is not swallowed for no effect.
    private bool RunKeyCommand(PlayerKeyCommand command)
    {
        switch (command)
        {
            case PlayerKeyCommand.ToggleFullscreen:
                ToggleFullscreen();
                break;
            case PlayerKeyCommand.ExitFullscreen:
                ExitFullscreen();
                break;
            case PlayerKeyCommand.ToggleRecording:
                HandlerBoundary.Run("PlayerRecord.Key", ToggleRecordingAsync);
                break;
            case PlayerKeyCommand.ToggleMute:
                ToggleMute();
                break;
            case PlayerKeyCommand.VolumeUp:
                StepVolume(KeyboardVolumeStep);
                break;
            case PlayerKeyCommand.VolumeDown:
                StepVolume(-KeyboardVolumeStep);
                break;
            case PlayerKeyCommand.OpenAudioTracks:
                if (AudioTracksButton.Visibility != Visibility.Visible)
                {
                    return false;
                }

                // The panel may be auto-hidden: the menu anchors to a button that must be laid out first.
                ShowControls();
                ControlPanel.UpdateLayout();
                OpenAudioTrackMenu();
                break;
            default:
                return false;
        }

        ShowControls();
        return true;
    }

    // The slider's ValueChanged applies the level to the engine, unmutes, and schedules the save.
    private void StepVolume(double delta)
    {
        var target = Math.Clamp(VolumeSlider.Value + delta, VolumeSlider.Minimum, VolumeSlider.Maximum);
        if (_isMuted && delta > 0 && target == VolumeSlider.Value)
        {
            // Muted at the top: the slider has no change to raise, so unmute directly.
            ToggleMute();
            return;
        }

        VolumeSlider.Value = target;
    }

    // A text field, an editable combo box or an open control-panel menu owns the keyboard: no key may also
    // act on the player underneath it (the actions menu holds a text box for a new collection name).
    private bool KeyboardIsOwnedElsewhere() =>
        _openControlPanelMenus.Count > 0
        || Keyboard.FocusedElement is TextBoxBase or ComboBox { IsEditable: true };

    private static PlayerKey MapKey(Key key) => key switch
    {
        Key.F => PlayerKey.F,
        Key.F11 => PlayerKey.F11,
        Key.M => PlayerKey.M,
        Key.A => PlayerKey.A,
        Key.R => PlayerKey.R,
        Key.Up => PlayerKey.Up,
        Key.Down => PlayerKey.Down,
        Key.Escape => PlayerKey.Escape,
        _ => PlayerKey.Other
    };

    private static PlayerKeyModifiers MapModifiers(ModifierKeys modifiers)
    {
        var mapped = PlayerKeyModifiers.None;
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            mapped |= PlayerKeyModifiers.Ctrl;
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            mapped |= PlayerKeyModifiers.Shift;
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            mapped |= PlayerKeyModifiers.Alt;
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            mapped |= PlayerKeyModifiers.Windows;
        }

        return mapped;
    }
}
