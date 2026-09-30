using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>SP-0179: CAPTURE-OUTPUT rules 9-11 - the order folders are tried in, and that each is tried once.</summary>
public sealed class CaptureFolderChainTests
{
    private const string Frames = @"C:\Users\u\Pictures\Frames";
    private const string Downloads = @"C:\Users\u\Downloads";

    [Fact]
    public void For_WithoutAChoice_TriesTheDefaultThenDownloads() =>
        Assert.Equal([Frames, Downloads], CaptureFolderChain.For(null, Frames, Downloads));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void For_ABlankChoiceIsNoChoice(string chosen) =>
        Assert.Equal([Frames, Downloads], CaptureFolderChain.For(chosen, Frames, Downloads));

    [Fact]
    public void For_TheUsersChoiceComesFirst() =>
        Assert.Equal([@"E:\Shots", Frames, Downloads], CaptureFolderChain.For(@" E:\Shots ", Frames, Downloads));

    [Theory]
    [InlineData(@"c:\users\u\pictures\frames\")]
    [InlineData(@"C:\Users\u\Pictures\Frames")]
    public void For_ChoosingTheDefaultDoesNotTryItTwice(string chosen) =>
        Assert.Equal(2, CaptureFolderChain.For(chosen, Frames, Downloads).Count);

    [Fact]
    public void For_ChoosingDownloadsKeepsTheUsersOrder() =>
        Assert.Equal([Downloads, Frames], CaptureFolderChain.For(Downloads, Frames, Downloads));
}
