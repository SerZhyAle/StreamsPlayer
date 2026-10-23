using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StreamsPlayer.App;
using StreamsPlayer.Core;

namespace StreamsPlayer.Exchange.Tests;

/// <summary>
/// SP-0239 P4 item 7, CAPTURE-OUTPUT rule 16: the capture time inside a saved frame equals the one in its name.
/// The frame is written by the real <see cref="CapturedFrameWriter"/> and read back through WIC with a query of this
/// test's own, so the writer's constant is not what vouches for itself. The instants carry an offset that is not the
/// machine's, because a writer that converted one of the two clocks and not the other would pass on any machine whose
/// local offset happens to be the instant's.
/// </summary>
public sealed class CapturedFrameTimeTests : IDisposable
{
    /// <summary>EXIF <c>DateTimeOriginal</c> (tag 36867), spelled here independently of the writer.</summary>
    private const string DateTimeOriginalQuery = "/app1/ifd/exif/{ushort=36867}";

    private const string ExifFormat = "yyyy:MM:dd HH:mm:ss";

    // video_frame_<yyMMdd>_<HHmmss>[_<label>][ (<n>)].jpg - rule 3, as CaptureFileName.For produces it.
    private static readonly Regex FrameName = new(
        @"^video_frame_(?<stamp>\d{6}_\d{6})(?:_.+?)?(?: \(\d+\))?\.jpg$", RegexOptions.CultureInvariant);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sp0239-frametime-" + Guid.NewGuid().ToString("N"));

    public CapturedFrameTimeTests() => Directory.CreateDirectory(_directory);

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

    [Fact]
    public async Task NameAndExifCarryTheSameSecond_WhenTheInstantsOffsetIsNotTheMachines()
    {
        var utc = new DateTime(2026, 6, 15, 21, 40, 5, 987, DateTimeKind.Utc);
        var local = TimeZoneInfo.Local.GetUtcOffset(utc);
        var foreign = local >= TimeSpan.Zero ? local - TimeSpan.FromHours(3) : local + TimeSpan.FromHours(3);
        var instant = new DateTimeOffset(utc).ToOffset(foreign);
        Assert.NotEqual(local, instant.Offset);

        await AssertNameMatchesExif(instant);
    }

    [Theory]
    [InlineData(840)]
    [InlineData(-720)]
    [InlineData(345)]
    [InlineData(-210)]
    [InlineData(0)]
    public async Task NameAndExifCarryTheSameSecond_AcrossOffsets(int offsetMinutes)
    {
        // 23:59:59.999 UTC: a few of these offsets cross midnight, so the date digits are compared as well as the time.
        var utc = new DateTime(2026, 12, 31, 23, 59, 59, 999, DateTimeKind.Utc);
        var instant = new DateTimeOffset(utc).ToOffset(TimeSpan.FromMinutes(offsetMinutes));

        await AssertNameMatchesExif(instant);
    }

    private async Task AssertNameMatchesExif(DateTimeOffset instant)
    {
        var folder = Path.Combine(_directory, Guid.NewGuid().ToString("N"));

        var (path, skipped) = await CapturedFrameWriter.SaveAsync(Frame(), [folder], "Test Channel", instant);

        Assert.Null(skipped);
        // Rule 12: the frame is visible only once complete - the one file is the final name, nothing is left staged.
        var files = Directory.GetFiles(folder);
        Assert.Single(files);
        Assert.Equal(path, files[0]);
        Assert.Empty(Directory.GetFiles(folder, "*.partial"));

        var name = FrameName.Match(Path.GetFileName(path));
        Assert.True(name.Success, $"'{Path.GetFileName(path)}' is not a rule 3 frame name.");
        var fromName = DateTime.ParseExact(name.Groups["stamp"].Value, CaptureFileName.TimestampFormat, CultureInfo.InvariantCulture);
        var fromExif = DateTime.ParseExact(ReadDateTimeOriginal(path), ExifFormat, CultureInfo.InvariantCulture);

        Assert.Equal(fromName, fromExif);
        // The sub-second part is cut, never rounded: a rounding clock would carry 5.987 as 6 in one place and 5 in the other.
        Assert.Equal(instant.Second, fromName.Second);
    }

    private static string ReadDateTimeOriginal(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var metadata = Assert.IsType<BitmapMetadata>(decoder.Frames[0].Metadata);
        return Assert.IsType<string>(metadata.GetQuery(DateTimeOriginalQuery));
    }

    private static BitmapSource Frame()
    {
        const int size = 8;
        var pixels = new byte[size * size * 3];
        for (var index = 0; index < pixels.Length; index++)
        {
            pixels[index] = (byte)(index * 7);
        }

        var frame = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgr24, null, pixels, size * 3);
        frame.Freeze();
        return frame;
    }
}
