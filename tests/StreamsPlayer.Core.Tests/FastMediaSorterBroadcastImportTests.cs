using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class FastMediaSorterBroadcastImportTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Apply_UsesSourceIdBeforeAddressAndKeepsUserIdentity()
    {
        var original = Channel("http://192.168.1.97:8768/live-audio.aac") with
        {
            Pinned = true,
            SortIndex = -3,
            LastPlayedAt = Now.AddDays(-1),
            FastMediaSorterBroadcast = new FastMediaSorterBroadcastInfo
            {
                SourceId = "phone-1",
                Mode = FastMediaSorterBroadcastDescriptor.AudioOnlyMode,
                SelectedTransport = "HTTP"
            }
        };

        var result = FastMediaSorterBroadcastImport.Apply(
            [original],
            Descriptor("http://192.168.1.97:9000/live-audio", "phone-1", "New address"),
            Now);

        var replaced = Assert.Single(result.Channels);
        Assert.False(result.Added);
        Assert.Equal(original.Id, replaced.Id);
        Assert.Equal("http://192.168.1.97:9000/live-audio", replaced.Url);
        Assert.Equal("New address", replaced.Title);
        Assert.True(replaced.Pinned);
        Assert.Equal(-3, replaced.SortIndex);
        Assert.Equal(Now.AddDays(-1), replaced.LastPlayedAt);
        Assert.True(replaced.IsLive);
        Assert.Equal("phone-1", replaced.FastMediaSorterBroadcast?.SourceId);
    }

    /// <summary>S9-3: a bank row is the catalog merge's to change; the broadcast becomes its own imported row.</summary>
    [Theory]
    [InlineData(SourceOrigin.Catalog)]
    [InlineData(SourceOrigin.LocalCatalog)]
    public void Apply_NeverRewritesABankRowInPlace(SourceOrigin origin)
    {
        var bankRow = Channel("http://192.168.1.166:33559/listen") with
        {
            SourceOrigin = origin,
            Title = "Published title",
            Category = "Radio"
        };

        var result = FastMediaSorterBroadcastImport.Apply(
            [bankRow],
            Descriptor("http://192.168.1.166:33559/listen", "watch-1", "Watch"),
            Now);

        Assert.True(result.Added);
        Assert.Equal(2, result.Channels.Count);
        Assert.Equal(bankRow, result.Channels.Single(channel => channel.Id == bankRow.Id));
        Assert.Equal(SourceOrigin.Imported, result.Channel.SourceOrigin);
        Assert.NotEqual(bankRow.Id, result.Channel.Id);
    }

    [Fact]
    public void Apply_FallsBackToNormalizedUrlIdentityWhenSourceIdIsAbsent()
    {
        var original = Channel("http://192.168.1.166:80/listen");

        var result = FastMediaSorterBroadcastImport.Apply(
            [original],
            Descriptor("http://192.168.1.166/listen", null, "Watch"),
            Now);

        Assert.False(result.Added);
        Assert.Single(result.Channels);
        Assert.Equal(original.Id, result.Channel.Id);
        Assert.Equal("Watch", result.Channel.Title);
    }

    [Fact]
    public void Apply_NewDescriptorCreatesImportedLiveAudioAtTheNextOrder()
    {
        var existing = Channel("https://example.test/other") with { SortIndex = 7 };

        var result = FastMediaSorterBroadcastImport.Apply(
            [existing],
            Descriptor("http://192.168.1.166:33559/listen", "watch-1", "Watch"),
            Now);

        Assert.True(result.Added);
        Assert.Equal(SourceOrigin.Imported, result.Channel.SourceOrigin);
        Assert.Equal(MediaKind.Audio, result.Channel.MediaKind);
        Assert.Equal(8, result.Channel.SortIndex);
        Assert.True(result.Channel.IsLive);
        Assert.Equal("watch-1", result.Channel.FastMediaSorterBroadcast?.SourceId);
    }

    /// <summary>
    /// SP-0159: the camera kinds of contract 0.13 §2.3/§2.4 read into a playable channel - the RTSP
    /// address, the live mark of §2, the real mode and transport stored for the player window.
    /// </summary>
    [Theory]
    [InlineData(FastMediaSorterBroadcastDescriptor.VideoAudioMode)]
    [InlineData(FastMediaSorterBroadcastDescriptor.VideoOnlyMode)]
    public void Apply_CameraDescriptorBecomesAnImportedLiveRtspChannel(string mode)
    {
        var result = FastMediaSorterBroadcastImport.Apply([], CameraDescriptor(mode), Now);

        Assert.True(result.Added);
        Assert.Equal(SourceOrigin.Imported, result.Channel.SourceOrigin);
        Assert.Equal(MediaKind.Rtsp, result.Channel.MediaKind);
        Assert.Equal("rtsp://192.168.1.97:8554/", result.Channel.Url);
        Assert.Equal("Galaxy S25 FE", result.Channel.Title);
        Assert.True(result.Channel.IsLive);
        Assert.Equal(mode, result.Channel.FastMediaSorterBroadcast?.Mode);
        Assert.Equal("RTSP", result.Channel.FastMediaSorterBroadcast?.SelectedTransport);
        Assert.Equal(200, result.Channel.FastMediaSorterBroadcast?.TargetLatencyMs);
    }

    /// <summary>A phone that switched from audio to its camera replaces its channel instead of duplicating it.</summary>
    [Fact]
    public void Apply_SourceIdMatchReplacesAnAudioChannelWithTheVideoBroadcast()
    {
        var original = Channel("http://192.168.1.97:8768/live-audio.aac") with
        {
            Pinned = true,
            SortIndex = -3,
            FastMediaSorterBroadcast = new FastMediaSorterBroadcastInfo
            {
                SourceId = "phone-camera-1",
                Mode = FastMediaSorterBroadcastDescriptor.AudioOnlyMode,
                SelectedTransport = "HTTP"
            }
        };

        var result = FastMediaSorterBroadcastImport.Apply([original], CameraDescriptor("VIDEO_AUDIO"), Now);

        Assert.False(result.Added);
        Assert.Equal(original.Id, result.Channel.Id);
        Assert.True(result.Channel.Pinned);
        Assert.Equal(-3, result.Channel.SortIndex);
        Assert.Equal(MediaKind.Rtsp, result.Channel.MediaKind);
        Assert.Equal("rtsp://192.168.1.97:8554/", result.Channel.Url);
        Assert.Equal("VIDEO_AUDIO", result.Channel.FastMediaSorterBroadcast?.Mode);
    }

    [Fact]
    public async Task PersistedBroadcastMetadataRoundTripsThroughTheStateStore()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"StreamsPlayer.Tests.{Guid.NewGuid():N}");
        try
        {
            var channel = Channel("http://192.168.1.97:8768/live-audio.aac") with
            {
                IsLive = true,
                FastMediaSorterBroadcast = new FastMediaSorterBroadcastInfo
                {
                    SourceId = "phone-1",
                    Mode = FastMediaSorterBroadcastDescriptor.AudioOnlyMode,
                    SelectedTransport = "HTTP",
                    TargetLatencyMs = 1000,
                    Endpoints =
                    [
                        new("http://192.168.1.97:8768/live-audio.aac", "HTTP", "AUDIO_ONLY", null, "AAC", 44100, 128000, true, 1000)
                    ]
                }
            };
            var store = new StreamCatalogStore(directory);

            await store.SaveAsync(new CatalogState { Channels = [channel] });
            var loaded = Assert.Single((await store.LoadAsync()).Channels);

            Assert.Equal("phone-1", loaded.FastMediaSorterBroadcast?.SourceId);
            Assert.Equal(1000, loaded.FastMediaSorterBroadcast?.TargetLatencyMs);
            Assert.Single(loaded.FastMediaSorterBroadcast?.Endpoints ?? []);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task StateWrittenBeforeBroadcastMetadataStillLoads()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"StreamsPlayer.Tests.{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(
                Path.Combine(directory, "catalog-state.json"),
                """{"channels":[{"id":"1d7c2cf2-caf5-4eb1-8812-a2682dc13e36","url":"https://example.test/live","title":"Old","mediaKind":"Audio","sourceOrigin":"Manual","sortIndex":0,"addedAt":"2026-09-13T08:00:00+00:00"}]}""");
            var loaded = Assert.Single((await new StreamCatalogStore(directory).LoadAsync()).Channels);

            Assert.Null(loaded.FastMediaSorterBroadcast);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void KnownDeviceAddressesAreLiveButAnArbitraryManualUrlIsNotAnFmsBroadcast()
    {
        var phone = Channel("http://192.168.1.97:8768/live-audio.aac");
        var watch = Channel("http://192.168.1.166:33559/listen");
        var manualLive = Channel("https://example.test/live") with { IsLive = true };

        Assert.True(FastMediaSorterBroadcastImport.IsLive(phone));
        Assert.True(FastMediaSorterBroadcastImport.IsLive(watch));
        Assert.True(FastMediaSorterBroadcastImport.IsFastMediaSorterBroadcast(phone));
        Assert.True(FastMediaSorterBroadcastImport.IsFastMediaSorterBroadcast(watch));
        Assert.True(FastMediaSorterBroadcastImport.IsLive(manualLive));
        Assert.False(FastMediaSorterBroadcastImport.IsFastMediaSorterBroadcast(manualLive));
    }

    [Fact]
    public void CatalogRefreshCannotPruneAnImportedBroadcast()
    {
        var imported = FastMediaSorterBroadcastImport.Apply(
            [],
            Descriptor("http://192.168.1.166:33559/listen", "watch-1", "Watch"),
            Now).Channel;

        var merged = CatalogMerger.Merge([imported], [], Now.AddMinutes(1));

        Assert.Equal(imported, Assert.Single(merged.Channels));
    }

    private static FastMediaSorterBroadcast Descriptor(string url, string? sourceId, string title) =>
        new(
            url,
            Mode: FastMediaSorterBroadcastDescriptor.AudioOnlyMode,
            title,
            sourceId,
            IsLive: true,
            TargetLatencyMs: 1000,
            Endpoints:
            [
                new FastMediaSorterBroadcastEndpoint(url, "HTTP", "AUDIO_ONLY", null, "AAC", 44100, 128000, true, 1000)
            ]);

    /// <summary>The §2.5 descriptor the phone emits for a camera broadcast, read through the contract reader.</summary>
    private static FastMediaSorterBroadcast CameraDescriptor(string mode) =>
        FastMediaSorterBroadcastDescriptor.Read($$"""
            {"schemaVersion":1,"url":"rtsp://192.168.1.97:8554/","title":"Galaxy S25 FE","mode":"{{mode}}","sourceId":"phone-camera-1","isLive":true,"targetLatencyMs":200,"endpoints":[{"url":"rtsp://192.168.1.97:8554/","transport":"RTSP","mode":"{{mode}}","videoCodec":"h264","bitrate":2000000,"isLive":true,"targetLatencyMs":200}]}
            """).Broadcast!;

    /// <summary>
    /// SP-0203: a video broadcast whose only reachable address is the relay - a phone on mobile data
    /// - lands as a video row: the kind follows the descriptor's mode, not the URL heuristic (a
    /// `/stream` path without an extension classifies as audio on its own).
    /// </summary>
    [Fact]
    public void Apply_ARelayOnlyVideoDescriptorStaysAVideoRow()
    {
        var result = FastMediaSorterBroadcastImport.Apply([], FastMediaSorterBroadcastDescriptor.Read("""
            {"schemaVersion":1,"url":"https://exchange.example.net:44022/v2/b/ICEiIyQlJicoKSorLC0uLw/stream","title":"Kitchen camera","mode":"VIDEO_AUDIO","sourceId":"phone-camera-2","isLive":true,"targetLatencyMs":2000,"endpoints":[{"url":"https://exchange.example.net:44022/v2/b/ICEiIyQlJicoKSorLC0uLw/stream","transport":"RELAY","mode":"VIDEO_AUDIO","videoCodec":"H264","audioCodec":"AAC","isLive":true,"targetLatencyMs":2000,"certFingerprint":"SHA256:8f6TQvCbXjDMOyu4A9JzKcWlEHmR5pNsGgVaU2wYqhk"}]}
            """).Broadcast!, Now);

        Assert.True(result.Added);
        Assert.Equal(MediaKind.Video, result.Channel.MediaKind);
        Assert.Equal("RELAY", result.Channel.FastMediaSorterBroadcast?.SelectedTransport);
        Assert.Equal(2000, result.Channel.FastMediaSorterBroadcast?.TargetLatencyMs);
        var attempt = Assert.Single(result.Channel.FastMediaSorterBroadcast!.PlaybackAttemptEndpoints());
        Assert.Equal("SHA256:8f6TQvCbXjDMOyu4A9JzKcWlEHmR5pNsGgVaU2wYqhk", attempt.CertFingerprint);
    }

    private static StreamChannel Channel(string url) => new()
    {
        Id = Guid.NewGuid(),
        Url = url,
        Title = "Original",
        MediaKind = MediaKind.Audio,
        SourceOrigin = SourceOrigin.Manual,
        SortIndex = 0,
        AddedAt = Now
    };
}
