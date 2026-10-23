using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class PlayerKeyCommandsTests
{
    private static PlayerKeyInput Press(
        PlayerKey key,
        PlayerKeyModifiers modifiers = PlayerKeyModifiers.None,
        bool isRepeat = false,
        bool editorOwnsFocus = false,
        bool handled = false,
        bool fullscreen = false) =>
        new(key, modifiers, isRepeat, editorOwnsFocus, handled, fullscreen);

    [Theory]
    [InlineData(PlayerKey.F, PlayerKeyCommand.ToggleFullscreen)]
    [InlineData(PlayerKey.F11, PlayerKeyCommand.ToggleFullscreen)]
    [InlineData(PlayerKey.M, PlayerKeyCommand.ToggleMute)]
    [InlineData(PlayerKey.A, PlayerKeyCommand.OpenAudioTracks)]
    [InlineData(PlayerKey.R, PlayerKeyCommand.ToggleRecording)]
    public void BareKey_MapsToItsCommand(PlayerKey key, PlayerKeyCommand expected) =>
        Assert.Equal(expected, PlayerKeys.Resolve(Press(key)));

    [Theory]
    [InlineData(PlayerKey.F, PlayerKeyCommand.ToggleFullscreen)]
    [InlineData(PlayerKey.M, PlayerKeyCommand.ToggleMute)]
    public void BareKey_BehavesTheSameInFullscreen(PlayerKey key, PlayerKeyCommand expected) =>
        Assert.Equal(expected, PlayerKeys.Resolve(Press(key, fullscreen: true)));

    [Fact]
    public void CtrlUp_RaisesAndCtrlDown_LowersTheVolume()
    {
        Assert.Equal(PlayerKeyCommand.VolumeUp, PlayerKeys.Resolve(Press(PlayerKey.Up, PlayerKeyModifiers.Ctrl)));
        Assert.Equal(PlayerKeyCommand.VolumeDown, PlayerKeys.Resolve(Press(PlayerKey.Down, PlayerKeyModifiers.Ctrl)));
    }

    [Fact]
    public void CtrlUpDown_RepeatsSoAHeldKeyRampsTheVolume()
    {
        Assert.Equal(PlayerKeyCommand.VolumeUp, PlayerKeys.Resolve(Press(PlayerKey.Up, PlayerKeyModifiers.Ctrl, isRepeat: true)));
        Assert.Equal(PlayerKeyCommand.VolumeDown, PlayerKeys.Resolve(Press(PlayerKey.Down, PlayerKeyModifiers.Ctrl, isRepeat: true)));
    }

    [Fact]
    public void Escape_LeavesFullscreenOnly()
    {
        Assert.Equal(PlayerKeyCommand.ExitFullscreen, PlayerKeys.Resolve(Press(PlayerKey.Escape, fullscreen: true)));
        Assert.Equal(PlayerKeyCommand.None, PlayerKeys.Resolve(Press(PlayerKey.Escape, fullscreen: false)));
    }

    [Theory]
    [InlineData(PlayerKey.R, PlayerKeyModifiers.Shift)]
    [InlineData(PlayerKey.M, PlayerKeyModifiers.Ctrl)]
    [InlineData(PlayerKey.F, PlayerKeyModifiers.Alt)]
    [InlineData(PlayerKey.A, PlayerKeyModifiers.Windows)]
    [InlineData(PlayerKey.F11, PlayerKeyModifiers.Shift)]
    [InlineData(PlayerKey.F, PlayerKeyModifiers.Ctrl | PlayerKeyModifiers.Shift)]
    public void ModifiedLetter_IsNotThePlayersKey(PlayerKey key, PlayerKeyModifiers modifiers) =>
        Assert.Equal(PlayerKeyCommand.None, PlayerKeys.Resolve(Press(key, modifiers)));

    [Theory]
    [InlineData(PlayerKey.F)]
    [InlineData(PlayerKey.F11)]
    [InlineData(PlayerKey.M)]
    [InlineData(PlayerKey.A)]
    [InlineData(PlayerKey.R)]
    public void HeldKey_DoesNotRepeatItsToggle(PlayerKey key) =>
        Assert.Equal(PlayerKeyCommand.None, PlayerKeys.Resolve(Press(key, isRepeat: true)));

    [Theory]
    [InlineData(PlayerKey.F)]
    [InlineData(PlayerKey.F11)]
    [InlineData(PlayerKey.M)]
    [InlineData(PlayerKey.A)]
    [InlineData(PlayerKey.R)]
    [InlineData(PlayerKey.Escape)]
    public void KeyTypedIntoAnEditor_IsLeftToTheEditor(PlayerKey key) =>
        Assert.Equal(PlayerKeyCommand.None, PlayerKeys.Resolve(Press(key, editorOwnsFocus: true, fullscreen: true)));

    [Fact]
    public void CtrlUpDown_InsideAnEditor_IsLeftToTheEditor()
    {
        Assert.Equal(PlayerKeyCommand.None, PlayerKeys.Resolve(Press(PlayerKey.Up, PlayerKeyModifiers.Ctrl, editorOwnsFocus: true)));
        Assert.Equal(PlayerKeyCommand.None, PlayerKeys.Resolve(Press(PlayerKey.Down, PlayerKeyModifiers.Ctrl, editorOwnsFocus: true)));
    }

    [Theory]
    [InlineData(PlayerKey.F)]
    [InlineData(PlayerKey.M)]
    [InlineData(PlayerKey.R)]
    [InlineData(PlayerKey.Escape)]
    public void AlreadyHandledPress_IsNotHandledTwice(PlayerKey key) =>
        Assert.Equal(PlayerKeyCommand.None, PlayerKeys.Resolve(Press(key, handled: true, fullscreen: true)));

    [Fact]
    public void AlreadyHandledCtrlUp_IsNotHandledTwice() =>
        Assert.Equal(PlayerKeyCommand.None, PlayerKeys.Resolve(Press(PlayerKey.Up, PlayerKeyModifiers.Ctrl, handled: true)));

    [Fact]
    public void UpAndDown_NeedControlToChangeTheVolume()
    {
        Assert.Equal(PlayerKeyCommand.None, PlayerKeys.Resolve(Press(PlayerKey.Up)));
        Assert.Equal(PlayerKeyCommand.None, PlayerKeys.Resolve(Press(PlayerKey.Down)));
        Assert.Equal(PlayerKeyCommand.None, PlayerKeys.Resolve(Press(PlayerKey.Up, PlayerKeyModifiers.Shift)));
        Assert.Equal(PlayerKeyCommand.None, PlayerKeys.Resolve(Press(PlayerKey.Down, PlayerKeyModifiers.Alt)));
    }

    [Fact]
    public void CtrlWithAnotherModifier_IsNotTheVolumeKey()
    {
        Assert.Equal(PlayerKeyCommand.None, PlayerKeys.Resolve(Press(PlayerKey.Up, PlayerKeyModifiers.Ctrl | PlayerKeyModifiers.Shift)));
        Assert.Equal(PlayerKeyCommand.None, PlayerKeys.Resolve(Press(PlayerKey.Down, PlayerKeyModifiers.Ctrl | PlayerKeyModifiers.Alt)));
    }

    [Fact]
    public void AnyOtherKey_IsNothing() =>
        Assert.Equal(PlayerKeyCommand.None, PlayerKeys.Resolve(Press(PlayerKey.Other, fullscreen: true)));
}
