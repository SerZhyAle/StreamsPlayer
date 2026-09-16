using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class PngHeaderTests
{
    private static readonly byte[] ValidHeader =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D,
        0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x01, 0x00,
        0x00, 0x00, 0x00, 0x80,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    ];

    [Fact]
    public void TryReadDimensions_ReturnsTrueForValidPngHeader()
    {
        var success = PngHeader.TryReadDimensions(ValidHeader, out var width, out var height);

        Assert.True(success);
        Assert.Equal(256, width);
        Assert.Equal(128, height);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(23)]
    public void TryReadDimensions_RejectsTooShortData(int length)
    {
        var buffer = new byte[length];
        Array.Copy(ValidHeader, buffer, Math.Min(length, ValidHeader.Length));

        Assert.False(PngHeader.TryReadDimensions(buffer, out _, out _));
    }

    [Fact]
    public void TryReadDimensions_RejectsInvalidSignature()
    {
        var bytes = (byte[])ValidHeader.Clone();
        bytes[0] = 0x00;

        Assert.False(PngHeader.TryReadDimensions(bytes, out _, out _));
    }

    [Fact]
    public void TryReadDimensions_RejectsNonIhdrChunk()
    {
        var bytes = (byte[])ValidHeader.Clone();
        bytes[12] = (byte)'J';
        bytes[13] = (byte)'F';
        bytes[14] = (byte)'I';
        bytes[15] = (byte)'F';

        Assert.False(PngHeader.TryReadDimensions(bytes, out _, out _));
    }

    [Fact]
    public void TryReadDimensions_RejectsZeroDimensions()
    {
        var bytes = (byte[])ValidHeader.Clone();
        bytes[16] = 0;
        bytes[17] = 0;
        bytes[18] = 0;
        bytes[19] = 0; // Width = 0

        Assert.False(PngHeader.TryReadDimensions(bytes, out _, out _));
    }
}
