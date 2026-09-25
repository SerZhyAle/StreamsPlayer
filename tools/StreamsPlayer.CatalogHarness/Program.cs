using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Windows.Media.Imaging;
using StreamsPlayer.Core;

var outputPath = Path.GetFullPath(args.FirstOrDefault() ?? "favicon-sample.png");
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
// SP-0133: the diagnostic fetches the bank through the product's own download - StreamCatalogService's
// DownloadAsync - so it inherits every rule the product applies: the head bound and the body's silence bound
// (SP-0087, SP-0129), the whole-archive ceiling (SP-0069), the truncated-ZIP check and the publish-window retry
// (SP-0107, STREAM-BANK rule 11), and the refusal of an empty bank. A diagnostic that fails differently from the
// thing it diagnoses is worse than none, and a hand-rolled copy of that sequence had already drifted from it once.
// DownloadAsync reads and writes no local state; the store is required by the constructor only, so it is given a
// directory that is never created.
using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("StreamsPlayer-CatalogHarness", "0.1"));
var unusedStore = new StreamCatalogStore(Path.Combine(Path.GetTempPath(), "StreamsPlayer.CatalogHarness.unused"));
var service = new StreamCatalogService(client, unusedStore);

Console.WriteLine($"Downloading {StreamCatalogService.CatalogUrl}");
long archiveBytes = 0;
var outcome = await service.DownloadAsync(
    progress: new SynchronousProgress<DownloadProgress>(value => archiveBytes = value.ReceivedBytes),
    retrying: new SynchronousProgress<PublishWindowRetryNotice>(notice => Console.WriteLine(
        $"Publish window ({notice.Cause}): attempt {notice.NextAttempt} of {notice.MaximumAttempts} in {notice.Delay.TotalSeconds:0} s")),
    CancellationToken.None);
Console.WriteLine($"Archive bytes: {archiveBytes:N0}");
var bank = outcome.Bank;

Console.WriteLine($"Valid channels: {bank.Entries.Count:N0}");
Console.WriteLine($"streams.csv is entry 0: {bank.CsvWasFirstEntry}");
Console.WriteLine($"Atlas bytes: {bank.FaviconAtlas?.Length ?? 0:N0}");
Console.WriteLine($"Maximum favicon index in CSV: {bank.MaximumFaviconIndex?.ToString() ?? "none"}");
foreach (var entry in bank.Entries.Take(5))
{
    Console.WriteLine($"  {entry.MediaKind,-5}  {entry.Title}  {entry.Url}");
}

var sample = bank.Entries.FirstOrDefault(entry => entry.FaviconIndex is not null);
if (bank.FaviconAtlas is not { Length: > 0 } atlasBytes || sample?.FaviconIndex is not int index)
{
    Console.WriteLine("No favicon sample is available in this bank.");
    return;
}

using var atlasStream = new MemoryStream(atlasBytes);
var decoder = BitmapDecoder.Create(atlasStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
var atlas = decoder.Frames[0];
var x = index % 16 * 32;
var y = index / 16 * 32;
if (x + 32 > atlas.PixelWidth || y + 32 > atlas.PixelHeight)
{
    throw new InvalidDataException($"Favicon index {index} is outside the {atlas.PixelWidth}x{atlas.PixelHeight} atlas.");
}

var tile = new CroppedBitmap(atlas, new System.Windows.Int32Rect(x, y, 32, 32));
var encoder = new PngBitmapEncoder();
encoder.Frames.Add(BitmapFrame.Create(tile));
await using (var file = File.Create(outputPath))
{
    encoder.Save(file);
}

Console.WriteLine($"Wrote tile {index} for '{sample.Title}' to {outputPath}");

/// <summary>
/// Reports on the calling thread. <see cref="Progress{T}"/> posts to the thread pool in a console process, so a
/// retry line could print after the result it preceded and the byte count could be read before its last report.
/// </summary>
internal sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
