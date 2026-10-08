using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0203: the ordered attempt list - the producer's listed order is the order a leg tries and a
/// reconnect restarts at; a transport this build does not implement drops silently; a descriptor
/// with no usable list falls back to the legacy top-level address; and a `RELAY` endpoint with its
/// `certFingerprint` (and a `TUNNEL` one with its `inner`) is read and kept.
/// </summary>
public sealed class FastMediaSorterBroadcastAttemptsTests
{
    private static FastMediaSorterBroadcastEndpoint Endpoint(string url, string transport, string? mode = null,
        long? targetLatencyMs = null, string? certFingerprint = null, string? inner = null) =>
        new(url, transport, mode, null, null, null, null, true, targetLatencyMs, certFingerprint, inner);

    private static FastMediaSorterBroadcast Descriptor(string mode, params FastMediaSorterBroadcastEndpoint[] endpoints) =>
        new("rtsp://192.168.1.97:8554/live", mode, "Kitchen", "src-1", true, 1000, endpoints);

    [Fact]
    public void TheAttemptListKeepsTheProducerListedOrder()
    {
        var descriptor = Descriptor(
            "VIDEO_AUDIO",
            Endpoint("rtsp://192.168.1.97:8554/live", "RTSP", "VIDEO_AUDIO"),
            Endpoint("https://relay.example.net:44022/v2/b/b1/stream", "RELAY", "VIDEO_AUDIO", 2000),
            Endpoint("http://192.168.1.97:8768/live.ts", "HTTP", "VIDEO_AUDIO", 1000));
        Assert.Equal(["RTSP", "RELAY", "HTTP"],
            descriptor.PlaybackAttemptEndpoints().Select(endpoint => endpoint.Transport));
    }

    [Fact]
    public void AnUnimplementedTransportIsSkippedSilently()
    {
        var descriptor = Descriptor(
            "AUDIO_ONLY",
            Endpoint("p2p://192.168.1.97:9000", "P2P", "AUDIO_ONLY"),
            Endpoint("http://192.168.1.97:8768/live-audio.aac", "HTTP", "AUDIO_ONLY"),
            Endpoint("webrtc://192.168.1.97/live", "WEBRTC", "AUDIO_ONLY"));
        Assert.Equal(["HTTP"], descriptor.PlaybackAttemptEndpoints().Select(endpoint => endpoint.Transport));
    }

    [Fact]
    public void ADescriptorWithNoUsableListFallsBackToTheTopLevelAddress()
    {
        var descriptor = Descriptor("AUDIO_ONLY");
        var attempts = descriptor.PlaybackAttemptEndpoints();
        var attempt = Assert.Single(attempts);
        Assert.Equal("rtsp://192.168.1.97:8554/live", attempt.Url);
        Assert.Equal("RTSP", attempt.Transport);
    }

    [Fact]
    public void AReconnectReadsTheSamePlanFromTheTop()
    {
        // The plan is stateless: the second call - what a reconnect leg makes - is the first, so the
        // restart at index 0 is the restart at the top.
        var descriptor = Descriptor(
            "AUDIO_ONLY",
            Endpoint("http://192.168.1.97:8768/live-audio.aac", "HTTP", "AUDIO_ONLY"),
            Endpoint("https://relay.example.net:44022/v2/b/b1/stream", "RELAY", "AUDIO_ONLY", 2000, "SHA256:8f6TQvCbXjDMOyu4A9JzKcWlEHmR5pNsGgVaU2wYqhk"));
        Assert.Equal(descriptor.PlaybackAttemptEndpoints(), descriptor.PlaybackAttemptEndpoints());
    }

    [Fact]
    public void TheRelayEndpointIsReadWithItsPinAndLatency()
    {
        const string pin = "SHA256:8f6TQvCbXjDMOyu4A9JzKcWlEHmR5pNsGgVaU2wYqhk";
        var descriptor = Descriptor(
            "AUDIO_ONLY",
            Endpoint("https://relay.example.net:44022/v2/b/b1/stream", "RELAY", "AUDIO_ONLY", 2000, pin));
        var attempt = Assert.Single(descriptor.PlaybackAttemptEndpoints());
        Assert.Equal("RELAY", attempt.Transport);
        Assert.Equal(pin, attempt.CertFingerprint);
        Assert.Equal(2000, attempt.TargetLatencyMs);
        Assert.Equal("https://relay.example.net:44022/v2/b/b1/stream", attempt.Url);
    }

    [Fact]
    public void TheParserKeepsCertFingerprintAndInnerThroughTheRead()
    {
        const string json = """
            {"schemaVersion":1,"url":"http://192.168.1.97:8768/live-audio.aac","title":"Kitchen","mode":"AUDIO_ONLY",
             "sourceId":"AAECAwQFBgcICQoLDA0ODw","isLive":true,"targetLatencyMs":1000,"endpoints":[
             {"url":"http://192.168.1.97:8768/live-audio.aac","transport":"HTTP","mode":"AUDIO_ONLY","audioCodec":"AAC","sampleRate":44100,"isLive":true,"targetLatencyMs":1000},
             {"url":"https://exchange.example.net:44022/v2/b/ICEiIyQlJicoKSorLC0uLw/stream","transport":"RELAY","mode":"AUDIO_ONLY","audioCodec":"AAC","sampleRate":44100,"isLive":true,"targetLatencyMs":2000,"certFingerprint":"SHA256:8f6TQvCbXjDMOyu4A9JzKcWlEHmR5pNsGgVaU2wYqhk"},
             {"url":"fmsx://exchange.example.net:44022/b/ICEiIyQlJicoKSorLC0uLw/http","transport":"TUNNEL","inner":"http://192.168.1.97:8768/live-audio.aac","mode":"AUDIO_ONLY","audioCodec":"AAC","sampleRate":44100,"isLive":true,"targetLatencyMs":2000,"certFingerprint":"SHA256:8f6TQvCbXjDMOyu4A9JzKcWlEHmR5pNsGgVaU2wYqhk"}]}
            """;
        var read = FastMediaSorterBroadcastDescriptor.Read(json);
        Assert.Equal(FastMediaSorterBroadcastReadStatus.Ok, read.Status);
        var attempts = read.Broadcast!.PlaybackAttemptEndpoints();
        Assert.Equal(["HTTP", "RELAY", "TUNNEL"], attempts.Select(endpoint => endpoint.Transport));
        Assert.Equal("SHA256:8f6TQvCbXjDMOyu4A9JzKcWlEHmR5pNsGgVaU2wYqhk", attempts[1].CertFingerprint);
        Assert.Equal("http://192.168.1.97:8768/live-audio.aac", attempts[2].Inner);
    }

    [Fact]
    public void ALanHttpVideoEndpointJoinsTheVideoAttempts()
    {
        var descriptor = Descriptor(
            "VIDEO_AUDIO",
            Endpoint("http://192.168.1.97:8768/live.ts", "HTTP", "VIDEO_AUDIO", 1000),
            Endpoint("https://relay.example.net:44022/v2/b/b1/stream", "RELAY", "VIDEO_AUDIO", 2000));
        Assert.Equal(["HTTP", "RELAY"], descriptor.PlaybackAttemptEndpoints().Select(endpoint => endpoint.Transport));
    }

    [Fact]
    public void AnAudioEndpointIsNeverAnAttemptOfAVideoDescriptorAndTheReverse()
    {
        var audioForVideo = Descriptor(
            "VIDEO_ONLY",
            Endpoint("http://192.168.1.97:8768/live-audio.aac", "HTTP", "AUDIO_ONLY"),
            Endpoint("https://relay.example.net:44022/v2/b/b1/stream", "RELAY", "AUDIO_ONLY"));
        var videoOnly = Assert.Single(audioForVideo.PlaybackAttemptEndpoints());
        Assert.Equal("rtsp://192.168.1.97:8554/live", videoOnly.Url);

        var videoForAudio = Descriptor(
            "AUDIO_ONLY",
            Endpoint("rtsp://192.168.1.97:8554/live", "RTSP", "VIDEO_AUDIO"),
            Endpoint("http://192.168.1.97:8768/live.ts", "HTTP", "VIDEO_AUDIO"),
            Endpoint("https://relay.example.net:44022/v2/b/b1/stream", "RELAY", "VIDEO_AUDIO"));
        var audioOnly = Assert.Single(videoForAudio.PlaybackAttemptEndpoints());
        Assert.Equal("rtsp://192.168.1.97:8554/live", audioOnly.Url);
    }

    [Fact]
    public void AnEndpointWithoutAModeInheritsTheDescriptorMode()
    {
        var descriptor = Descriptor(
            "VIDEO_AUDIO",
            Endpoint("https://relay.example.net:44022/v2/b/b1/stream", "RELAY", null, 2000));
        var attempt = Assert.Single(descriptor.PlaybackAttemptEndpoints());
        Assert.Equal("RELAY", attempt.Transport);
    }

    [Fact]
    public void AnHttpAudioEndpointIsNotAVideoAttemptEvenForAVideoDescriptor()
    {
        // The MPEG-TS video shape and the ADTS audio shape share the HTTP transport; the endpoint's
        // mode is what separates them (requirement 2 keeps the audio shape where it was).
        var descriptor = Descriptor(
            "VIDEO_ONLY",
            Endpoint("http://192.168.1.97:8768/live-audio.aac", "HTTP", "AUDIO_ONLY"));
        var attempt = Assert.Single(descriptor.PlaybackAttemptEndpoints());
        Assert.Equal("rtsp://192.168.1.97:8554/live", attempt.Url);
    }
}
