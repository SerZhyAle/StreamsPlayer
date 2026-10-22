using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StreamsPlayer.App;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;
using ZXing.QrCode.Internal;

namespace StreamsPlayer.Exchange.Tests;

/// <summary>
/// Pictures of a QR for the reader's tests. The module matrix comes from ZXing's encoder - a different code
/// path from the decoder under test - and everything after that (scale, quiet zone, polarity, rotation, canvas,
/// compression) is drawn here, so the reader is shown what a screenshot or a saved picture really looks like.
/// </summary>
internal static class QrPictures
{
    internal static bool[,] Matrix(string text, ErrorCorrectionLevel? level = null)
    {
        var hints = new Dictionary<EncodeHintType, object>
        {
            [EncodeHintType.ERROR_CORRECTION] = level ?? ErrorCorrectionLevel.M,
            [EncodeHintType.CHARACTER_SET] = "UTF-8",
            [EncodeHintType.MARGIN] = 0
        };
        var bits = new QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, 0, 0, hints);
        var matrix = new bool[bits.Width, bits.Height];
        for (var x = 0; x < bits.Width; x++)
        {
            for (var y = 0; y < bits.Height; y++)
            {
                matrix[x, y] = bits[x, y];
            }
        }

        return matrix;
    }

    internal static bool[,] Rotate(bool[,] matrix)
    {
        var size = matrix.GetLength(0);
        var rotated = new bool[size, size];
        for (var x = 0; x < size; x++)
        {
            for (var y = 0; y < size; y++)
            {
                rotated[size - 1 - y, x] = matrix[x, y];
            }
        }

        return rotated;
    }

    /// <summary>Draws one code on a new canvas; the quiet zone is white (or black when inverted).</summary>
    internal static ExchangePixels Draw(bool[,] matrix, int scale = 6, int quiet = 4, bool inverted = false,
        int canvasWidth = 0, int canvasHeight = 0, int offsetX = 0, int offsetY = 0)
    {
        var modules = matrix.GetLength(0);
        var side = (modules + quiet * 2) * scale;
        canvasWidth = Math.Max(canvasWidth, side + offsetX);
        canvasHeight = Math.Max(canvasHeight, side + offsetY);
        var canvas = Blank(canvasWidth, canvasHeight, inverted ? (byte)0 : (byte)255);
        Place(canvas, matrix, scale, quiet, inverted, offsetX, offsetY);
        return canvas;
    }

    internal static ExchangePixels Blank(int width, int height, byte shade)
    {
        var bytes = new byte[width * height * 4];
        Array.Fill(bytes, shade);
        for (var offset = 3; offset < bytes.Length; offset += 4)
        {
            bytes[offset] = 255;
        }

        return new ExchangePixels(bytes, width, height);
    }

    internal static void Place(ExchangePixels canvas, bool[,] matrix, int scale, int quiet, bool inverted, int offsetX, int offsetY)
    {
        var modules = matrix.GetLength(0);
        var side = (modules + quiet * 2) * scale;
        var paper = inverted ? (byte)0 : (byte)255;
        var ink = inverted ? (byte)255 : (byte)0;
        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
            {
                var mx = x / scale - quiet;
                var my = y / scale - quiet;
                var dark = mx >= 0 && my >= 0 && mx < modules && my < modules && matrix[mx, my];
                var shade = dark ? ink : paper;
                var index = ((offsetY + y) * canvas.Width + offsetX + x) * 4;
                canvas.Bgra[index] = canvas.Bgra[index + 1] = canvas.Bgra[index + 2] = shade;
                canvas.Bgra[index + 3] = 255;
            }
        }
    }

    internal static string Save(ExchangePixels pixels, string path, string format = "png", int jpegQuality = 70)
    {
        var source = BitmapSource.Create(pixels.Width, pixels.Height, 96, 96, PixelFormats.Bgra32, null, pixels.Bgra, pixels.Width * 4);
        BitmapEncoder encoder = format == "jpg" ? new JpegBitmapEncoder { QualityLevel = jpegQuality } : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }
}
