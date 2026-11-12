using System.Text.RegularExpressions;
using Xunit;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0251: a screen or camera broadcast without sound needs no audio encoder, so a PC without Windows' own encoder
/// still starts it. The decision lives in <see cref="BroadcastSources.CompanionAudio"/>; the video start reads the
/// App's own source as data because the WPF producer is not loaded here.
/// </summary>
public sealed class BroadcastCompanionAudioTests
{
    private static BroadcastProducerSettings Settings(string screen = "none", string camera = "none") =>
        new() { ScreenAudioSource = screen, CameraAudioSource = camera };

    [Theory]
    [InlineData("none", null)]
    [InlineData("mic", BroadcastSource.Microphone)]
    [InlineData("system-audio", BroadcastSource.SystemSound)]
    [InlineData("screen", null)]
    [InlineData("", null)]
    public void AScreenCarriesOnlyTheSoundItsChoiceNames(string choice, BroadcastSource? expected)
    {
        Assert.Equal(expected, BroadcastSources.CompanionAudio(BroadcastSource.Screen, Settings(screen: choice)));
    }

    [Fact]
    public void AScreenAndACameraReadTheirOwnChoice()
    {
        var settings = Settings(screen: "mic", camera: "system-audio");

        Assert.Equal(BroadcastSource.Microphone, BroadcastSources.CompanionAudio(BroadcastSource.Screen, settings));
        Assert.Equal(BroadcastSource.SystemSound, BroadcastSources.CompanionAudio(BroadcastSource.Camera, settings));
    }

    [Fact]
    public void ADefaultVideoBroadcastHasNoSoundAndSoNeedsNoAudioEncoder()
    {
        var settings = new BroadcastProducerSettings();

        Assert.Null(BroadcastSources.CompanionAudio(BroadcastSource.Screen, settings));
        Assert.Null(BroadcastSources.CompanionAudio(BroadcastSource.Camera, settings));
    }

    [Theory]
    [InlineData(BroadcastSource.Microphone)]
    [InlineData(BroadcastSource.SystemSound)]
    public void AnAudioSourceIsItsOwnSoundAndHasNoCompanion(BroadcastSource source)
    {
        Assert.Null(BroadcastSources.CompanionAudio(source, Settings(screen: "mic", camera: "mic")));
    }

    [Fact]
    public void TheVideoStartCreatesTheAudioEncoderOnlyForABroadcastWithSound()
    {
        var file = Assert.Single(AppSourceFile.LoadAll("BroadcastProducerService.Video.cs"));
        var start = Regex.Match(file.Masked, @"Task<bool>\s+StartVideoAsync\b");
        Assert.True(start.Success, "StartVideoAsync was not found");
        var body = file.Masked[start.Index..];

        Assert.Single(Regex.Matches(body[..body.IndexOf("private void OnVideoChunk", StringComparison.Ordinal)], @"new\s+WindowsAacEncoder\("));
        Assert.Matches(@"if\s*\(companion\s+is\s+not\s+null\)\s*encoder\s*=\s*await\s+Task\.Run\(\(\)\s*=>\s*new\s+WindowsAacEncoder\(", body);
        Assert.Contains("BroadcastSources.CompanionAudio(source, settings)", file.Text, StringComparison.Ordinal);
    }
}
