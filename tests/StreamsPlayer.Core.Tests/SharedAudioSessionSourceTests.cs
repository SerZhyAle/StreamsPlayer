using System.Text.RegularExpressions;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// Enforced over the App's own source (read as text - see <see cref="AppSourceFile"/>): every LibVLC player in the
/// process writes one Windows audio session, so its mute and volume are shared state, not a property of one player.
/// A frame capture that created its player muted left the whole session muted, and a radio started afterwards played
/// to silence while <c>AUDIO HEARD</c> still counted its buffers - the engine's own statistics cannot see a muted session.
/// </summary>
public sealed class SharedAudioSessionSourceTests
{
    [Theory]
    [InlineData("StandardAudioPlayback.cs")]
    [InlineData("FastMediaSorterAudioPlayback.cs")]
    public void ARadioEngine_StatesItsOwnMuteOnEveryPlayerItCreates(string file)
    {
        var source = AppSourceFile.LoadAll(file).Single();

        var created = Regex.Matches(source.Masked, @"\bnew\s+MediaPlayer\s*\(").Count;
        var unmuted = Regex.Matches(source.Masked, @"\bnew\s+MediaPlayer\s*\([^()]*\)\s*\{[^{}]*\bMute\s*=\s*false\b").Count;

        Assert.True(created > 0, $"{source.Name}: no MediaPlayer is created, so this test no longer guards anything.");
        Assert.True(created == unmuted,
            $"{source.Name}: {created} MediaPlayer(s) created, {unmuted} with Mute = false. Radio has no mute control, " +
            "so a mute left in the shared Windows audio session by another player must not carry into a station.");
    }

    [Fact]
    public void TheFrameCapture_NeverWritesTheSharedAudioSession()
    {
        var source = AppSourceFile.LoadAll("VideoFrameCaptureService.cs").Single();

        var write = Regex.Match(source.Masked, @"\b(Mute|Volume)\s*=(?!=)");

        Assert.False(write.Success,
            $"{source.Name}:{(write.Success ? source.LineAt(write.Index) : 0)}: the capture is silent through :no-audio; " +
            "setting Mute or Volume on its player writes the audio session every other player in the process shares.");
    }
}
