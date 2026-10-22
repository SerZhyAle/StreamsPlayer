using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StreamsPlayer.App;
using StreamsPlayer.Core;
using ZXing.QrCode.Internal;

namespace StreamsPlayer.Exchange.Tests;

/// <summary>
/// SP-0221 requirement 2 and its seam criteria: a QR image of a valid string reaches the same codec the
/// pasted string does, and a picture with no QR, with two, or with another payload is refused with one
/// message each. The decoder is proved against known answers - the contract's own strings, drawn the ways a
/// screenshot or a saved file draws them, and two pictures made by another product's encoder.
/// </summary>
public sealed class ExchangeQrReaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sp0221-qr-" + Guid.NewGuid().ToString("N"));

    public ExchangeQrReaderTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder that cannot be removed is the machine's to clean.
        }
    }

    private static string VectorPath => Path.Combine(AppContext.BaseDirectory, "Vectors", "fmsw-224", "connection-string-v1.json");

    public static IEnumerable<object[]> ContractStrings()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(VectorPath));
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            if (item.GetProperty("kind").GetString() != "text" || item.GetProperty("verdict").GetString() == "refused")
            {
                continue;
            }

            if (!item.TryGetProperty("input", out var inputElement))
            {
                continue;
            }

            var input = inputElement.GetString()!;
            // The string is a document: one visible ASCII line. A case with a BOM or white space around it
            // is a case of the reader's trim, which a QR does not exercise.
            if (input.All(character => character is > ' ' and < '\u007f'))
            {
                yield return [item.GetProperty("name").GetString()!, input];
            }
        }
    }

    [Theory]
    [MemberData(nameof(ContractStrings))]
    public void ContractString_DecodesByteForByte_AsScreenshotOrScaledPicture(string name, string text)
    {
        var matrix = QrPictures.Matrix(text);
        var presentations = new (string Label, ExchangePixels Pixels)[]
        {
            ("scale 6, quiet 4", QrPictures.Draw(matrix)),
            ("scale 3 on a 1920x1080 canvas", QrPictures.Draw(matrix, scale: 3, canvasWidth: 1920, canvasHeight: 1080, offsetX: 611, offsetY: 233)),
            ("inverted", QrPictures.Draw(matrix, inverted: true)),
            ("rotated", QrPictures.Draw(QrPictures.Rotate(matrix))),
            ("large module", QrPictures.Draw(matrix, scale: 12, quiet: 2))
        };
        foreach (var (label, pixels) in presentations)
        {
            var outcome = ExchangeQrReader.Decode(pixels);
            Assert.True(outcome.Kind == ExchangeQrOutcomeKind.Found, $"{name} / {label}: {outcome.Kind}");
            Assert.Equal(text, outcome.Text);
            var import = ExchangeConnectionImport.ReadQr(outcome);
            Assert.True(import.Data is not null, $"{name} / {label}: {import.MessageKey}");
        }
    }

    [Fact]
    public async Task SavedFile_Png_AndCompressedJpeg_AreRead()
    {
        const string text = "FMSEXCH1:x=exchange.example.org%3A22022&fp=SHA256%3AUl2rC03A44auS8B7ibLjJaAApHooIx%2F4R%2BSSgDTXUtY&n=Office&u=carol";
        var pixels = QrPictures.Draw(QrPictures.Matrix(text), scale: 4, canvasWidth: 800, canvasHeight: 600, offsetX: 40, offsetY: 30);
        var png = QrPictures.Save(pixels, Path.Combine(_directory, "code.png"));
        var jpg = QrPictures.Save(pixels, Path.Combine(_directory, "code.jpg"), "jpg", 60);
        foreach (var path in new[] { png, jpg })
        {
            var outcome = await ExchangeQrReader.ReadFileAsync(path);
            Assert.Equal(ExchangeQrOutcomeKind.Found, outcome.Kind);
            Assert.Equal(text, outcome.Text);
            var data = ExchangeConnectionImport.ReadQr(outcome).Data!;
            Assert.Equal("exchange.example.org", data.Host);
            Assert.Equal(22022, data.Port);
            Assert.Equal("carol", data.Login);
        }
    }

    [Fact]
    public async Task TransparentBackground_IsReadOverWhite()
    {
        const string text = "FMSEXCH1:x=192.0.2.10%3A22022";
        var matrix = QrPictures.Matrix(text);
        var size = (matrix.GetLength(0) + 8) * 6;
        var bytes = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var mx = x / 6 - 4;
                var my = y / 6 - 4;
                var dark = mx >= 0 && my >= 0 && mx < matrix.GetLength(0) && my < matrix.GetLength(0) && matrix[mx, my];
                var index = (y * size + x) * 4;
                // Dark modules are opaque black; everything else is fully transparent (and black underneath).
                bytes[index + 3] = dark ? (byte)255 : (byte)0;
            }
        }

        var path = QrPictures.Save(new ExchangePixels(bytes, size, size), Path.Combine(_directory, "transparent.png"));
        var outcome = await ExchangeQrReader.ReadFileAsync(path);
        Assert.Equal(ExchangeQrOutcomeKind.Found, outcome.Kind);
        Assert.Equal(text, outcome.Text);
    }

    [Fact]
    public void NoCode_IsNone_AndOneMessage()
    {
        var blank = QrPictures.Blank(640, 480, 200);
        var noise = new byte[640 * 480 * 4];
        new Random(7).NextBytes(noise);
        for (var offset = 3; offset < noise.Length; offset += 4) noise[offset] = 255;
        foreach (var pixels in new[] { blank, new ExchangePixels(noise, 640, 480) })
        {
            var outcome = ExchangeQrReader.Decode(pixels);
            Assert.Equal(ExchangeQrOutcomeKind.None, outcome.Kind);
            Assert.Equal("ExchangeQrNone", ExchangeConnectionImport.ReadQr(outcome).MessageKey);
        }
    }

    [Fact]
    public void TwoCodes_AreSeveral_EvenWhenBothAreExchangeStrings()
    {
        var first = QrPictures.Matrix("FMSEXCH1:x=192.0.2.10%3A22022");
        var second = QrPictures.Matrix("FMSEXCH1:x=192.0.2.11%3A22022");
        var canvas = QrPictures.Blank(1200, 500, 255);
        QrPictures.Place(canvas, first, 6, 4, false, 20, 20);
        QrPictures.Place(canvas, second, 6, 4, false, 600, 20);
        var outcome = ExchangeQrReader.Decode(canvas);
        Assert.Equal(ExchangeQrOutcomeKind.Several, outcome.Kind);
        Assert.Null(outcome.Text);
        var import = ExchangeConnectionImport.ReadQr(outcome);
        Assert.Null(import.Data);
        Assert.Equal("ExchangeQrSeveral", import.MessageKey);
    }

    [Theory]
    [InlineData("hello, this is not a server")]
    [InlineData("https://example.org/a/path")]
    [InlineData("FMSEXCH1:x=192.0.2.10%3A0")]
    public void AnotherPayload_IsRead_ButRefusedByTheCodecWithItsOneMessage(string payload)
    {
        var outcome = ExchangeQrReader.Decode(QrPictures.Draw(QrPictures.Matrix(payload)));
        Assert.Equal(ExchangeQrOutcomeKind.Found, outcome.Kind);
        var import = ExchangeConnectionImport.ReadQr(outcome);
        Assert.Null(import.Data);
        Assert.Equal("ExchangeStringInvalid", import.MessageKey);
    }

    [Fact]
    public void NewerMajor_InAQr_IsTheFormatRowAndNeverAnUpdateDemand()
    {
        var outcome = ExchangeQrReader.Decode(QrPictures.Draw(QrPictures.Matrix("FMSEXCH2:x=192.0.2.10%3A22022")));
        Assert.Equal("ExchangeFormatUnsupported", ExchangeConnectionImport.ReadQr(outcome).MessageKey);
    }

    [Fact]
    public void InvitationString_ReadsAsSignIn_AndKeepsOnlyTheFactOfTheCode()
    {
        const string text = "FMSEXCH1:x=exchange.example.org%3A22022&u=carol&i=a3gPp-pvQPdQN7qwDDVbyQ";
        var outcome = ExchangeQrReader.Decode(QrPictures.Draw(QrPictures.Matrix(text)));
        var data = ExchangeConnectionImport.ReadQr(outcome).Data!;
        Assert.True(data.HasInvitation);
        Assert.Equal("carol", data.Login);
        Assert.DoesNotContain("a3gPp", data.Name ?? "");
    }

    [Fact]
    public async Task HostilePictures_NeverThrow()
    {
        Assert.Equal(ExchangeQrOutcomeKind.Unreadable,
            ExchangeQrReader.Decode(new ExchangePixels([], 0, 0)).Kind);
        Assert.Equal(ExchangeQrOutcomeKind.Unreadable,
            ExchangeQrReader.Decode(new ExchangePixels(new byte[16], 100_000, 100_000)).Kind);
        Assert.Equal(ExchangeQrOutcomeKind.Unreadable,
            ExchangeQrReader.Decode(new ExchangePixels(new byte[16], 10, 10)).Kind);
        var text = Path.Combine(_directory, "not-an-image.png");
        File.WriteAllText(text, "FMSEXCH1:x=192.0.2.10%3A22022");
        Assert.Equal(ExchangeQrOutcomeKind.Unreadable, (await ExchangeQrReader.ReadFileAsync(text)).Kind);
        Assert.Equal(ExchangeQrOutcomeKind.Unreadable,
            (await ExchangeQrReader.ReadFileAsync(Path.Combine(_directory, "missing.png"))).Kind);
        Assert.Equal("ExchangeQrUnreadable",
            ExchangeConnectionImport.ReadQr(new ExchangeQrOutcome(ExchangeQrOutcomeKind.Unreadable)).MessageKey);
    }

    private static string Reference(string file) => Path.Combine(AppContext.BaseDirectory, "Vectors", "qr", file);

    private static void AssertPinned(string file, string sha256) =>
        Assert.Equal(sha256, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Reference(file)))));

    /// <summary>
    /// Made by FastMediaSorter Lite's own QR writer (its dual-mode baseline), not by the library under test:
    /// the one place the decoder meets a producer it shares no code with. The picture and the document it must
    /// carry are pinned by hash (<c>Vectors/qr/PROVENANCE.txt</c>), so the answer is known without this reader.
    /// </summary>
    [Fact]
    public async Task PictureFromAnotherProductsEncoder_CarriesTheDocumentItWasMadeFrom()
    {
        AssertPinned("reference-share-lan-qr.png", "d709744ae72c9ee0f00947b4030a61a09312d19216cf45bc53a7ab610bfad80c");
        AssertPinned("reference-share-lan.fmscfg", "a20cd95de31ec5a3c14085355d1c84e71f65d6b31995fa3fe07b34df8b6c7603");
        var outcome = await ExchangeQrReader.ReadFileAsync(Reference("reference-share-lan-qr.png"));
        Assert.Equal(ExchangeQrOutcomeKind.Found, outcome.Kind);
        Assert.Equal(File.ReadAllText(Reference("reference-share-lan.fmscfg"), new UTF8Encoding(false)), outcome.Text);
    }

    [Fact]
    public async Task LargerPictureFromAnotherProductsEncoder_CarriesItsCompressedDocument()
    {
        AssertPinned("reference-share-full-qr.png", "3facbae235a4ff55d9835b6f77c3c2ce00a2a54de9628d8c568df18ab586f245");
        AssertPinned("reference-share-full.fmscfg", "68f76382f42f1b3dfd14d99178727a0b2951c6b89e687ded2f3842edb0322cd8");
        var outcome = await ExchangeQrReader.ReadFileAsync(Reference("reference-share-full-qr.png"));
        Assert.Equal(ExchangeQrOutcomeKind.Found, outcome.Kind);
        Assert.StartsWith("FMSCFG1:", outcome.Text, StringComparison.Ordinal);
        using var compressed = new MemoryStream(Convert.FromBase64String(outcome.Text!["FMSCFG1:".Length..]));
        using var gzip = new System.IO.Compression.GZipStream(compressed, System.IO.Compression.CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, new UTF8Encoding(false));
        Assert.Equal(File.ReadAllText(Reference("reference-share-full.fmscfg"), new UTF8Encoding(false)), reader.ReadToEnd());
        // Neither document is an exchange string, so each is refused with the codec's one message.
        Assert.Equal("ExchangeStringInvalid", ExchangeConnectionImport.ReadQr(outcome).MessageKey);
    }
}
