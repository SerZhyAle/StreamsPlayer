using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Windows.Media.Imaging;
using StreamsPlayer.Core;

var outputPath = Path.GetFullPath(args.FirstOrDefault() ?? "favicon-sample.png");
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("StreamsPlayer-CatalogHarness", "0.1"));

Console.WriteLine($"Downloading {StreamCatalogService.CatalogUrl}");
using var response = await client.GetAsync(StreamCatalogService.CatalogUrl, HttpCompletionOption.ResponseHeadersRead);
response.EnsureSuccessStatusCode();
await using var zipStream = await response.Content.ReadAsStreamAsync();
var bank = StreamBankReader.Read(zipStream);

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
