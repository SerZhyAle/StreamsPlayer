using System.Net;
using System.Globalization;
using System.Text;
using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class DriveSourceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
    private const string DeviceId = "AAECAwQFBgcICQoLDA0ODw";
    private const string BroadcastId = "ICEiIyQlJicoKSorLC0uLw";
    private const string Descriptor = """{"schemaVersion":1,"url":"http://192.168.1.97:8768/live-audio.aac","title":"Kitchen","mode":"AUDIO_ONLY","sourceId":"AAECAwQFBgcICQoLDA0ODw","endpoints":[{"url":"http://192.168.1.97:8768/live-audio.aac","transport":"HTTP","mode":"AUDIO_ONLY","audioCodec":"AAC","sampleRate":44100,"isLive":true}]}""";
    private static object BroadcastRecord() => new { broadcastId = BroadcastId, deviceId = DeviceId,
        title = "Kitchen", mode = "AUDIO_ONLY", descriptor = JsonSerializer.Deserialize<JsonElement>(Descriptor) };

    [Fact]
    public void CorpusDeviceWrapperReadsAndUsesDriveClock()
    {
        // DEVICE-EXCHANGE vectors/records-v1.json device-minimal, independent of our writer.
        var json = """{"schemaVersion":1,"type":"device","writtenAt":"2026-10-07T12:00:00.000Z","ttlSeconds":1800,"record":{"deviceId":"j5AmM0pZUz46odtkwdXCgQ","deviceName":"Minimal","lastSeen":"2026-10-07T12:00:00Z","presence":"online","product":"streamsplayer-windows","updatedAt":"2026-10-07T12:00:00Z"}}""";
        var record = DriveRecords.Parse(Encoding.UTF8.GetBytes(json), "fmsx-device-j5AmM0pZUz46odtkwdXCgQ.json");
        Assert.NotNull(record);
        Assert.False(DriveRecords.IsExpired(record, Now, Now.AddSeconds(1799)));
        Assert.True(DriveRecords.IsExpired(record, Now, Now.AddSeconds(1800)));
        Assert.False(DriveRecords.IsExpired(record, Now, Now.AddHours(-1)));
    }

    [Theory]
    [InlineData("request", 1)]
    [InlineData("resource", 1)]
    [InlineData("future", 1)]
    [InlineData("broadcast", 2)]
    public void UnknownKindsAndSchemasAreSkipped(string type, int schemaVersion)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion, type, record = BroadcastRecord() });
        Assert.Null(DriveRecords.Parse(bytes, $"fmsx-broadcast-{BroadcastId}.json"));
    }

    [Fact]
    public void OversizeAndMisnamedRecordsAreSkipped()
    {
        var bytes = DriveRecords.Wrap("broadcast", BroadcastRecord(), Now);
        Assert.NotNull(DriveRecords.Parse(bytes, $"fmsx-broadcast-{BroadcastId}.json"));
        Assert.Null(DriveRecords.Parse(bytes, "fmsx-broadcast-another.json"));
        Assert.Null(DriveRecords.Parse(new byte[DriveRecords.MaximumBytes + 1], $"fmsx-broadcast-{BroadcastId}.json"));
        Assert.False(DriveRecords.IsRecordName("fmsx-resource-share.json"));
    }

    [Fact]
    public async Task UnchangedRecordIsNotDownloadedAgainAndStillExpires()
    {
        using var handler = new FakeDrive();
        handler.Add("broadcast", BroadcastId, BroadcastRecord());
        handler.Add("device", DeviceId, new { deviceId = DeviceId, deviceName = "Phone", presence = "online" });
        using var http = new HttpClient(handler);
        var directory = new DriveDirectory(new DriveRestClient(http, _ => Task.FromResult("token")));
        var first = await directory.PollAsync("this-pc", default);
        var view = Assert.Single(Assert.Single(first.Groups).Broadcasts);
        Assert.Equal("Phone", first.Groups[0].DeviceName);
        Assert.Equal(ExchangeBroadcastSupport.Supported, view.Support);
        handler.Clock = Now.AddSeconds(89);
        var unchanged = await directory.PollAsync("this-pc", default);
        Assert.Single(unchanged.LiveBroadcastIds);
        Assert.Same(first, unchanged);
        handler.Clock = Now.AddSeconds(90);
        Assert.Empty((await directory.PollAsync("this-pc", default)).LiveBroadcastIds);
        Assert.Equal(2, handler.Downloads);
        Assert.Equal(3, handler.Listings);
    }

    [Fact]
    public async Task ProtocolDatesRemainGregorianUnderANonGregorianInterfaceCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            using var handler = new FakeDrive();
            handler.Add("broadcast", BroadcastId, BroadcastRecord());
            using var http = new HttpClient(handler);
            var directory = new DriveDirectory(new DriveRestClient(http, _ => Task.FromResult("token")));
            Assert.Single((await directory.PollAsync("this-pc", default)).LiveBroadcastIds);
            using var wrapper = JsonDocument.Parse(DriveRecords.Wrap("broadcast", BroadcastRecord(), Now));
            Assert.Equal("2026-10-25T12:00:00.000Z", wrapper.RootElement.GetProperty("writtenAt").GetString());
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public async Task ChangedModificationTimeDownloadsAgainAndOwnRecordsStayHidden()
    {
        using var handler = new FakeDrive();
        handler.Add("broadcast", BroadcastId, BroadcastRecord());
        using var http = new HttpClient(handler);
        var directory = new DriveDirectory(new DriveRestClient(http, _ => Task.FromResult("token")));
        Assert.Empty((await directory.PollAsync(DeviceId, default)).Groups);
        handler.Files[0] = handler.Files[0] with { ModifiedTime = Now.AddSeconds(10) };
        await directory.PollAsync(DeviceId, default);
        Assert.Equal(2, handler.Downloads);
    }

    [Fact]
    public async Task PublisherWritesCorpusWrapperRenewsByElapsedTimeAndDeletesOnlyItsOwnFiles()
    {
        using var handler = new FakeDrive();
        using var http = new HttpClient(handler);
        var owned = new Dictionary<string, string>();
        var saves = 0;
        var publisher = new DrivePublisher(new DriveRestClient(http, _ => Task.FromResult("token")), owned,
            _ => { saves++; return Task.CompletedTask; });
        var outgoing = new[] { new DriveAnnouncement(BroadcastId, BroadcastRecord()) };
        await publisher.SynchronizeAsync(DeviceId, outgoing, Now, TimeSpan.Zero, default);
        Assert.Equal(2, handler.Writes.Count);
        var broadcast = DriveRecords.Parse(handler.Writes[0], $"fmsx-broadcast-{BroadcastId}.json");
        Assert.NotNull(broadcast);
        Assert.Equal(90, broadcast.TtlSeconds);
        Assert.Equal(DeviceId, broadcast.DeviceId);
        await publisher.SynchronizeAsync(DeviceId, outgoing, Now.AddHours(-3), TimeSpan.FromSeconds(10), default);
        Assert.Equal(2, handler.Writes.Count);
        await publisher.SynchronizeAsync(DeviceId, outgoing, Now.AddHours(-3), TimeSpan.FromSeconds(30), default);
        Assert.Equal(4, handler.Writes.Count);
        Assert.Equal(2, handler.Patches);
        await publisher.SynchronizeAsync(DeviceId, [], Now, TimeSpan.FromSeconds(31), default);
        Assert.Equal(2, handler.Deletes);
        Assert.Empty(owned);
        Assert.Equal(6, saves);
        Assert.Equal(0, handler.Listings);
    }

    [Fact]
    public void DuplicateAnnouncementUsesTheServerRecordInTheSameDeviceGroup()
    {
        var record = new ExchangeBroadcastRecord(BroadcastId, DeviceId, "Kitchen", "AUDIO_ONLY", null);
        var serverView = new ExchangeBroadcastView(record, ExchangeBroadcastSupport.Supported);
        var driveView = new ExchangeBroadcastView(record with { Title = "Drive duplicate" }, ExchangeBroadcastSupport.Supported);
        ExchangeDirectorySnapshot Snapshot(ExchangeBroadcastView view) => new(1,
            [new ExchangeDeviceGroup(DeviceId, "Phone", true, [view])], [], [BroadcastId]);
        var merged = DriveDirectory.Merge(Snapshot(serverView), Snapshot(driveView));
        Assert.Same(serverView, Assert.Single(Assert.Single(merged!.Groups).Broadcasts));
    }

    [Fact]
    public void AKeptBroadcastStaysListedWhileEitherOwnDirectoryStillHasIt()
    {
        var descriptor = FastMediaSorterBroadcastDescriptor.Read(Descriptor).Broadcast!;
        var row = FastMediaSorterBroadcastImport.Apply([], descriptor, Now, BroadcastId,
            directoryEntryIdentity: "server-entry").Channel;
        var driveView = new ExchangeBroadcastView(new ExchangeBroadcastRecord(BroadcastId, DeviceId,
            "Kitchen", "AUDIO_ONLY", descriptor), ExchangeBroadcastSupport.Supported);
        var drive = new ExchangeDirectorySnapshot(1, [new ExchangeDeviceGroup(DeviceId, "Phone", true, [driveView])],
            [descriptor.SourceId!], [BroadcastId]);
        var emptyServer = new ExchangeDirectorySnapshot(2, [], [], []);
        var merged = DriveDirectory.Merge(emptyServer, drive)!;
        Assert.Empty(ExchangeDirectoryChannels.MarkEnded([row], merged.LiveSourceIds, merged.LiveBroadcastIds, Now));
        Assert.False(Assert.Single(ExchangeDirectoryChannels.ReconcileEntries([row], ["server-entry", "drive-entry"]))
            .FastMediaSorterBroadcast!.DirectoryEntryRemoved);
        var ended = Assert.Single(ExchangeDirectoryChannels.MarkEnded([row], [], [], Now));
        Assert.Equal(row.Id, ended.Id);
        Assert.Equal(Now, ended.FastMediaSorterBroadcast!.DirectoryEndedAt);
    }

    [Fact]
    public async Task OversizeDownloadWithoutLengthIsBounded()
    {
        using var content = new ByteArrayContent(new byte[20000]);
        content.Headers.ContentLength = null;
        Assert.Null(await DriveRestClient.ReadBoundedAsync(content, DriveRecords.MaximumBytes, default));
    }

    [Fact]
    public void BackoffIsJitteredBoundedAndResetBySuccess()
    {
        var delay = new DrivePollDelay();
        Assert.Equal(TimeSpan.FromSeconds(0.5), delay.After(false, 0));
        Assert.Equal(TimeSpan.FromSeconds(2), delay.After(false, 1));
        for (var i = 0; i < 30; i++) Assert.InRange(delay.After(false, 1).TotalSeconds, 1, 300);
        Assert.Equal(TimeSpan.FromSeconds(10), delay.After(true, 0));
        Assert.Equal(TimeSpan.FromSeconds(1), delay.After(false, 1));
    }

    private sealed class FakeDrive : HttpMessageHandler
    {
        public List<DriveFile> Files { get; } = [];
        private readonly Dictionary<string, byte[]> _bodies = [];
        public DateTimeOffset Clock { get; set; } = Now;
        public int Downloads { get; private set; }
        public int Listings { get; private set; }
        public int Patches { get; private set; }
        public int Deletes { get; private set; }
        public List<byte[]> Writes { get; } = [];
        public void Add(string type, string id, object record)
        {
            var name = $"fmsx-{type}-{id}.json";
            var bytes = DriveRecords.Wrap(type, record, Now.AddYears(10));
            Files.Add(new DriveFile(id, name, Now, bytes.Length));
            _bodies[id] = bytes;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            var uri = request.RequestUri!;
            object response = new { };
            HttpContent? content = null;
            if (request.Method == HttpMethod.Get && !uri.Query.Contains("alt=media"))
            {
                Listings++;
                Assert.Contains("spaces=appDataFolder", uri.Query);
                response = new { files = Files.Select(file => new { id = file.Id, name = file.Name,
                    modifiedTime = file.ModifiedTime, size = file.Size?.ToString() }) };
            }
            else if (request.Method == HttpMethod.Get)
            {
                Downloads++;
                content = new ByteArrayContent(_bodies[uri.Segments.Last()]);
            }
            else if (request.Method == HttpMethod.Delete) Deletes++;
            else
            {
                if (request.Method == HttpMethod.Patch) { Patches++; Writes.Add(await request.Content!.ReadAsByteArrayAsync(cancellationToken)); }
                else
                {
                    var parts = Assert.IsAssignableFrom<MultipartContent>(request.Content);
                    Writes.Add(await parts.Last().ReadAsByteArrayAsync(cancellationToken));
                }
                response = new { id = "owned-" + Writes.Count };
            }
            var result = new HttpResponseMessage(HttpStatusCode.OK)
            { Content = content ?? new StringContent(JsonSerializer.Serialize(response)) };
            result.Headers.Date = Clock;
            return result;
        }
    }
}
