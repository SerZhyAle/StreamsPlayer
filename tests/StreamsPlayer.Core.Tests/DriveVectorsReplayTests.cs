using System.Net;
using System.Text;
using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0285: the two Drive corpora of DEVICE-EXCHANGE, vendored byte for byte under `Vectors/drive-v1/` and replayed through
/// this product's Drive reader. `records-v1.json` (sections 6 and 8.1, rung 1) holds the record files of the Drive app-data
/// folder and is replayed through <see cref="DriveRecords"/> and <see cref="DriveDirectory"/>. `drive-packets-v1.json`
/// (section 24, rung 3) holds the packet manifests of the same folder. This product has no packet codec and reads no packet,
/// so that corpus is replayed as a boundary only: no manifest is taken for a record, and none is an error. The round trip
/// that rung 3 owes is not claimed here. The two cases the reader got wrong until SP-0289 are replayed as live tests.
/// </summary>
public sealed class DriveVectorsReplayTests
{
    private const string RecordsSha256 = "ccd59c550503c87470f4568829c35fc5487e4b603b5b54dead1efc45d73b5e39";
    private const string PacketsSha256 = "3a3461bb1385645242799a818f6f514cfce65bbbce4af6aa70644622357a98f5";
    private const string RecordsFile = "drive-v1/records-v1.json";
    private const string PacketsFile = "drive-v1/drive-packets-v1.json";
    private const string OwnDeviceId = "this-pc";
    private const string DeviceId = "AAECAwQFBgcICQoLDA0ODw";
    private const string DeviceFileName = "fmsx-device-AAECAwQFBgcICQoLDA0ODw.json";
    private const string BroadcastFileName = "fmsx-broadcast-ICEiIyQlJicoKSorLC0uLw.json";
    private const string ResourceFileName = "fmsx-resource-EBESExQVFhcYGRobHB0eHw.json";
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A case of the records corpus: its text as the bytes of a Drive file, and the file name the corpus gives it, if any.</summary>
    private static (byte[] Bytes, string? FileName) RecordCase(string group, string name)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(ExchangeFramesCorpus.PathOf(RecordsFile)));
        var item = document.RootElement.GetProperty(group).EnumerateArray()
            .Single(candidate => candidate.GetProperty("name").GetString() == name);
        return (Encoding.UTF8.GetBytes(item.GetProperty("text").GetString()!),
            item.TryGetProperty("fileName", out var fileName) ? fileName.GetString() : null);
    }

    /// <summary>The Drive app-data folder holding exactly <paramref name="files"/>, polled once by this product's directory.</summary>
    private static async Task<ExchangeDirectorySnapshot> PollFolderAsync(params (string FileName, byte[] Bytes)[] files)
    {
        using var http = new HttpClient(new DriveFolder(files));
        var directory = new DriveDirectory(new DriveRestClient(http, _ => Task.FromResult("token")));
        return await directory.PollAsync(OwnDeviceId, default);
    }

    // ---------------------------------------------------------------- the files and their shape

    [Fact]
    public void VendoredFiles_AreTheStoresBytes_AndProvenanceNamesTheHashes()
    {
        Assert.Equal(RecordsSha256, ExchangeFramesCorpus.Sha256Of(RecordsFile));
        Assert.Equal(PacketsSha256, ExchangeFramesCorpus.Sha256Of(PacketsFile));
        var provenance = File.ReadAllText(ExchangeFramesCorpus.PathOf("drive-v1/PROVENANCE.txt"));
        Assert.Contains(RecordsSha256, provenance);
        Assert.Contains(PacketsSha256, provenance);
    }

    [Fact]
    public void RecordsCorpus_ShapeIsPinned_SoARegenerationIsNoticed()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(ExchangeFramesCorpus.PathOf(RecordsFile)));
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("rung").GetInt32());
        Assert.Equal(7, root.GetProperty("valid").GetArrayLength());
        Assert.Equal(5, root.GetProperty("tolerated").GetArrayLength());
        Assert.Equal(10, root.GetProperty("skipped").GetArrayLength());
    }

    [Fact]
    public void PacketsCorpus_ShapeIsPinned_SoARegenerationIsNoticed()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(ExchangeFramesCorpus.PathOf(PacketsFile)));
        var cases = document.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        Assert.Equal(22, cases.Length);
        Assert.Equal(14, cases.Count(item => item.GetProperty("expect").GetString() == "read"));
        Assert.Equal(8, cases.Count(item => item.GetProperty("expect").GetString() == "skip"));
    }

    // ---------------------------------------------------------------- the record reader

    [Theory]
    [InlineData("device-full", "device", "AAECAwQFBgcICQoLDA0ODw", 1800)]
    [InlineData("device-minimal", "device", "MDEyMzQ1Njc4OTo7PD0-Pw", 1800)]
    [InlineData("device-receiver-without-plays", "device", "MDEyMzQ1Njc4OTo7PD0-Pw", 1800)]
    [InlineData("broadcast-audio-lan-relay-tunnel", "broadcast", "AAECAwQFBgcICQoLDA0ODw", 120)]
    public void ValidDeviceAndBroadcastRecords_AreReadWithTheirTypeIdentityAndTtl(string name, string type, string deviceId, int ttlSeconds)
    {
        var (bytes, fileName) = RecordCase("valid", name);
        var record = DriveRecords.Parse(bytes, fileName!);
        Assert.NotNull(record);
        Assert.Equal(type, record.Type);
        Assert.Equal(deviceId, record.DeviceId);
        Assert.Equal(ttlSeconds, record.TtlSeconds);
    }

    [Theory]
    [InlineData("valid", "resource-full")]
    [InlineData("valid", "resource-minimal")]
    [InlineData("valid", "request-announce")]
    [InlineData("tolerated", "public-port-out-of-range-dropped")]
    [InlineData("tolerated", "resource-without-access")]
    public void ResourceAndRequestRecords_AreNotReadAsDriveRecords(string group, string name)
    {
        // DEVICE-EXCHANGE section 17 item AG: this product imports no resource record, and a request is an action, not a record.
        var (bytes, fileName) = RecordCase(group, name);
        var file = fileName ?? ResourceFileName;
        Assert.False(DriveRecords.IsRecordName(file));
        Assert.Null(DriveRecords.Parse(bytes, file));
    }

    [Theory]
    [InlineData("unknown-members-and-seconds-timestamp")]
    [InlineData("unknown-presence-reads-offline")]
    [InlineData("blank-plays-pair-dropped")]
    public void ToleratedDeviceRecords_AreReadWithTheirIdentityAndTtl(string name)
    {
        // The corpus' canonical text is a codec's output. The reader keeps identity and TTL; presence and plays are the
        // directory's to read, and the directory's cases are below.
        var (bytes, _) = RecordCase("tolerated", name);
        var record = DriveRecords.Parse(bytes, DeviceFileName);
        Assert.NotNull(record);
        Assert.Equal("device", record.Type);
        Assert.Equal(DeviceId, record.DeviceId);
        Assert.Equal(1800, record.TtlSeconds);
    }

    [Theory]
    [InlineData("unknown-type")]
    [InlineData("unknown-kind")]
    [InlineData("unknown-action")]
    [InlineData("schema-above-1")]
    [InlineData("above-16-kib")]
    [InlineData("device-without-id")]
    [InlineData("record-not-an-object")]
    [InlineData("wrapper-without-ttl")]
    public void SkippedRecords_DecodeToNothing_AndAreNeverAnError(string name)
    {
        var (bytes, _) = RecordCase("skipped", name);
        Assert.Null(DriveRecords.Parse(bytes, DeviceFileName));
    }

    // ---------------------------------------------------------------- the directory

    [Theory]
    [InlineData("broadcast-without-descriptor")]
    [InlineData("broadcast-without-mode")]
    public async Task BroadcastRecordsWithoutDescriptorOrMode_AreNotListed(string name)
    {
        // The reader accepts these two records; the directory's record rule drops them, so no snapshot shows them.
        var (bytes, _) = RecordCase("skipped", name);
        var snapshot = await PollFolderAsync((BroadcastFileName, bytes));
        Assert.Empty(snapshot.LiveBroadcastIds);
        Assert.Empty(snapshot.Groups);
    }

    [Fact]
    public async Task FreshOnlineDeviceRecord_IsListedOnline()
    {
        var (bytes, fileName) = RecordCase("valid", "device-full");
        var snapshot = await PollFolderAsync((fileName!, bytes));
        var device = Assert.Single(snapshot.Devices!);
        Assert.Equal(DeviceId, device.DeviceId);
        Assert.Equal("Pixel 8", device.DeviceName);
        Assert.True(device.IsOnline);
    }

    [Fact]
    public void EveryPacketManifest_IsNotADriveRecord_AndNoneIsAnError()
    {
        // Section 24 binds packets this product does not read: the record reader takes no manifest for a device or a broadcast.
        using var document = JsonDocument.Parse(File.ReadAllBytes(ExchangeFramesCorpus.PathOf(PacketsFile)));
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            var manifest = Encoding.UTF8.GetBytes(item.GetProperty("manifest").GetRawText());
            Assert.Null(DriveRecords.Parse(manifest, DeviceFileName));
            Assert.Null(DriveRecords.Parse(manifest, BroadcastFileName));
        }
    }

    // ---------------------------------------------------------------- the directory's presence and lifetime (SP-0289)

    [Fact]
    public async Task FreshOfflineDeviceRecord_IsListedOffline()
    {
        // DEVICE-EXCHANGE 6.1: the writer's own presence decides, so its offline record reads offline until it expires.
        var (bytes, fileName) = RecordCase("valid", "device-minimal");
        var snapshot = await PollFolderAsync((fileName!, bytes));
        Assert.False(Assert.Single(snapshot.Devices!).IsOnline);
    }

    [Fact]
    public async Task FreshDeviceRecordWithUnknownPresence_IsListedOffline()
    {
        // The corpus case unknown-presence-reads-offline: a presence outside the closed set reads offline.
        var (bytes, _) = RecordCase("tolerated", "unknown-presence-reads-offline");
        var snapshot = await PollFolderAsync((DeviceFileName, bytes));
        Assert.False(Assert.Single(snapshot.Devices!).IsOnline);
    }

    [Fact]
    public async Task DeviceWrapperWithoutTtl_IsSkipped()
    {
        var (bytes, _) = RecordCase("skipped", "wrapper-without-ttl");
        var snapshot = await PollFolderAsync((DeviceFileName, bytes));
        Assert.Empty(snapshot.Devices!);
    }

    /// <summary>
    /// A Drive app-data folder of fixed files: a listing that names each file by its own name as its id, the bytes of each
    /// file on download, and the server clock on every answer, so every TTL is judged by the same clock (DEVICE-EXCHANGE 8.1 item C).
    /// </summary>
    private sealed class DriveFolder(params (string FileName, byte[] Bytes)[] files) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            HttpContent content = uri.Query.Contains("alt=media")
                ? new ByteArrayContent(files.Single(file => file.FileName == uri.Segments.Last()).Bytes)
                : new StringContent(JsonSerializer.Serialize(new
                {
                    files = files.Select(file => new { id = file.FileName, name = file.FileName, modifiedTime = Now, size = file.Bytes.Length.ToString() }),
                }));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            response.Headers.Date = Now;
            return Task.FromResult(response);
        }
    }
}
