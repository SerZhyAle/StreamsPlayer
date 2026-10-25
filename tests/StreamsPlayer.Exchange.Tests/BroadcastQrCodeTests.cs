using StreamsPlayer.App;
using StreamsPlayer.Core;
using ZXing;
using ZXing.Common;

namespace StreamsPlayer.Exchange.Tests;

/// <summary>
/// SP-0223 requirement 7: the QR of a broadcast's link decodes to that link. This is the in-suite round trip through the
/// decoder the product ships; the independent reference scanner (OpenCV) is run over the same pictures by
/// `temp/SP-0223/qr-reference.py` and its answer is recorded in the ticket, because a code and its reader from the
/// same library can share a mistake.
/// </summary>
public sealed class BroadcastQrCodeTests
{
    private static string Descriptor(BroadcastSource source = BroadcastSource.Screen, string? authUser = BroadcastLinkPassword.LinkUser) =>
        BroadcastDescriptorWriter.Write(new BroadcastWirePlan(
            "0123456789abcdef0123456789abcdef", "device-id", "PC screen - Monitor 1, 1920×1080", source, "192.168.1.5", 8768,
            "opaque-token", "exchange.example.test:44022", Relay: true, Tunnel: true, "SHA256:" + new string('a', 43), 44100, 1, 128,
            Seal: BroadcastSealMode.Outward, LinkGroupKey: ExchangeBase64Url.Encode(new byte[32]),
            TunnelPin: "SHA256:" + new string('b', 43), AuthUser: authUser));

    private static string Decode(bool[,] modules, int scale)
    {
        var pixels = BroadcastQrCode.Pixels(modules, scale, out var size);
        var source = new RGBLuminanceSource(pixels, size, size, RGBLuminanceSource.BitmapFormat.Gray8);
        var reader = new BarcodeReaderGeneric
        {
            Options = new DecodingOptions { PossibleFormats = [BarcodeFormat.QR_CODE], TryHarder = true }
        };
        return reader.Decode(source)?.Text ?? throw new InvalidOperationException("The reader found no code.");
    }

    [Theory]
    [InlineData(BroadcastSource.Screen, 6)]
    [InlineData(BroadcastSource.Camera, 8)]
    [InlineData(BroadcastSource.Microphone, 4)]
    public void TheQrOfALinkDecodesToThatLink(BroadcastSource source, int scale)
    {
        var text = FastMediaSorterBroadcastShare.BarcodeText(Descriptor(source));

        var modules = BroadcastQrCode.Modules(text);

        Assert.NotNull(modules);
        Assert.Equal(text, Decode(modules, scale));
    }

    [Fact]
    public void WhatTheScannerReadsImportsBackAsTheSameBroadcast()
    {
        var descriptor = Descriptor();
        var scanned = Decode(BroadcastQrCode.Modules(FastMediaSorterBroadcastShare.BarcodeText(descriptor))!, 6);

        var read = FastMediaSorterBroadcastDescriptor.Read(scanned);

        Assert.True(read.IsAccepted, read.Status.ToString());
        Assert.Equal("password-v1", read.Broadcast!.Auth!.Scheme);
        Assert.Equal(BroadcastLinkPassword.LinkUser, read.Broadcast.Auth.User);
    }

    [Fact]
    public void ThePictureHasTheQuietZoneAScannerNeeds()
    {
        var modules = BroadcastQrCode.Modules("FMSBCAST1:x")!;

        var pixels = BroadcastQrCode.Pixels(modules, 4, out var size);

        Assert.Equal((modules.GetLength(0) + 2 * BroadcastQrCode.QuietModules) * 4, size);
        // The first and the last row, and the first and the last column, are all light.
        for (var index = 0; index < size; index++)
        {
            Assert.Equal(255, pixels[index]);
            Assert.Equal(255, pixels[(size - 1) * size + index]);
            Assert.Equal(255, pixels[index * size]);
            Assert.Equal(255, pixels[index * size + size - 1]);
        }
    }

    [Fact]
    public void ATextThatFitsNoQrCodeGivesNoPicture()
    {
        Assert.Null(BroadcastQrCode.Modules(new string('x', 4000)));
    }
}
