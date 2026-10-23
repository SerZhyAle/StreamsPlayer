namespace StreamsPlayer.Core;

/// <summary>The keys the live player gives a meaning to; every other key is <see cref="Other"/>.</summary>
public enum PlayerKey
{
    Other,
    F,
    F11,
    M,
    A,
    R,
    Up,
    Down,
    Escape
}

/// <summary>The modifier keys held with a player key. Caps Lock and Num Lock are not modifiers.</summary>
[Flags]
public enum PlayerKeyModifiers
{
    None = 0,
    Ctrl = 1,
    Shift = 2,
    Alt = 4,
    Windows = 8
}

/// <summary>What a player key press asks the window to do.</summary>
public enum PlayerKeyCommand
{
    None,
    ToggleFullscreen,
    ExitFullscreen,
    ToggleRecording,
    ToggleMute,
    VolumeUp,
    VolumeDown,
    OpenAudioTracks
}

/// <summary>One key press, reduced to the facts the decision needs.</summary>
/// <param name="Key">The key, already mapped from the UI framework's key (the real key behind a system key).</param>
/// <param name="Modifiers">The modifiers held at the press.</param>
/// <param name="IsRepeat">The press is an operating-system auto-repeat of a held key.</param>
/// <param name="EditorOwnsFocus">A text field, an editable combo box or an open menu owns the keyboard.</param>
/// <param name="Handled">Something earlier in the event route already consumed the press.</param>
/// <param name="Fullscreen">The player window is currently full screen.</param>
public readonly record struct PlayerKeyInput(
    PlayerKey Key,
    PlayerKeyModifiers Modifiers,
    bool IsRepeat,
    bool EditorOwnsFocus,
    bool Handled,
    bool Fullscreen);

/// <summary>
/// The live player's keyboard map (the media-viewer keyboard contract, rows full screen, mute, volume and
/// audio track). A pure decision so the rules are testable without a window: the App maps the framework
/// key into <see cref="PlayerKeyInput"/> and performs whatever command comes back.
/// </summary>
/// <remarks>
/// Not applicable to a live engine, and so not mapped: next/previous/first/last file, seek, speed, frame
/// step, zoom, rotate, slideshow, A-B loop. <c>R</c> stays record, not rotate (a recorded exception).
/// <c>Esc</c> outside full screen does nothing: closing the player on it would stop playback by accident.
/// </remarks>
public static class PlayerKeys
{
    /// <summary>
    /// The command for <paramref name="input"/>, or <see cref="PlayerKeyCommand.None"/> when the press is not
    /// the player's: a field or an earlier handler owns it, a modifier or an auto-repeat disqualifies it, or
    /// the key means nothing in the current state.
    /// </summary>
    public static PlayerKeyCommand Resolve(PlayerKeyInput input)
    {
        if (input.Handled || input.EditorOwnsFocus)
        {
            return PlayerKeyCommand.None;
        }

        var bare = input.Modifiers == PlayerKeyModifiers.None && !input.IsRepeat;
        var ctrlOnly = input.Modifiers == PlayerKeyModifiers.Ctrl;
        return input.Key switch
        {
            PlayerKey.F or PlayerKey.F11 when bare => PlayerKeyCommand.ToggleFullscreen,
            PlayerKey.R when bare => PlayerKeyCommand.ToggleRecording,
            PlayerKey.M when bare => PlayerKeyCommand.ToggleMute,
            PlayerKey.A when bare => PlayerKeyCommand.OpenAudioTracks,
            PlayerKey.Up when ctrlOnly => PlayerKeyCommand.VolumeUp,
            PlayerKey.Down when ctrlOnly => PlayerKeyCommand.VolumeDown,
            PlayerKey.Escape when input.Fullscreen => PlayerKeyCommand.ExitFullscreen,
            _ => PlayerKeyCommand.None
        };
    }
}
