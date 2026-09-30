using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>SP-0179: the one folder choice becomes one per kind, once, without moving anybody's files elsewhere.</summary>
public sealed class CaptureFolderChoicesTests
{
    [Fact]
    public void Split_AChosenFolderKeepsReceivingTheRecordings()
    {
        var split = CaptureFolderChoices.Split(new CatalogState { FrameFolder = @"E:\Saved" });

        Assert.Equal(@"E:\Saved", split.FrameFolder);
        Assert.Equal(@"E:\Saved", split.VideoRecordingFolder);
        Assert.Equal(@"E:\Saved", split.AudioRecordingFolder);
        Assert.Equal(CaptureFolderChoices.CurrentSchema, split.CaptureFoldersSchema);
    }

    [Fact]
    public void Split_NoChoiceStaysNoChoice()
    {
        var split = CaptureFolderChoices.Split(new CatalogState());

        Assert.Null(split.VideoRecordingFolder);
        Assert.Null(split.AudioRecordingFolder);
        Assert.Equal(CaptureFolderChoices.CurrentSchema, split.CaptureFoldersSchema);
    }

    [Fact]
    public void Split_RunsOnce_AClearedRecordingFolderStaysCleared()
    {
        var cleared = CaptureFolderChoices.Split(new CatalogState { FrameFolder = @"E:\Saved" })
            with { VideoRecordingFolder = null };

        Assert.Null(CaptureFolderChoices.Split(cleared).VideoRecordingFolder);
    }

    [Fact]
    public void Split_KeepsARecordingFolderAlreadyThere() =>
        Assert.Equal(@"F:\Radio", CaptureFolderChoices.Split(
            new CatalogState { FrameFolder = @"E:\Saved", AudioRecordingFolder = @"F:\Radio" }).AudioRecordingFolder);

    [Theory]
    [InlineData(CaptureKind.VideoFrame, @"E:\Frames")]
    [InlineData(CaptureKind.StreamVideo, @"E:\Video")]
    [InlineData(CaptureKind.StreamAudio, null)]
    public void For_ReadsTheKindsOwnChoice(CaptureKind kind, string? expected) =>
        Assert.Equal(expected, CaptureFolderChoices.For(
            new CatalogState { FrameFolder = @" E:\Frames ", VideoRecordingFolder = @"E:\Video", AudioRecordingFolder = "  ", CaptureFoldersSchema = 1 },
            kind));
}
