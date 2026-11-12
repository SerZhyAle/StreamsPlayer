namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0225 requirement 2: the connection check of a failed broadcast looks at the server's authority and nothing
/// else, for the audio and the video window alike. The path and the id behind the host are the capability to listen.
/// </summary>
public sealed class ExchangeDiagnosisTargetTests
{
    private static FastMediaSorterBroadcastInfo Imported(string json)
    {
        var descriptor = FastMediaSorterBroadcastDescriptor.Read(json).Broadcast!;
        return FastMediaSorterBroadcastImport.Apply([], descriptor, DateTimeOffset.UtcNow).Channel.FastMediaSorterBroadcast!;
    }

    [Fact]
    public void AVideoBroadcastNamesItsHostAndPort_AndNeverThePathBehindThem()
    {
        var info = Imported("""
            {"schemaVersion":1,"title":"Cam","url":"http://192.168.1.97:8080/live/secret-broadcast-id.ts","mode":"VIDEO_AUDIO","isLive":true,
             "endpoints":[{"url":"http://192.168.1.97:8080/live/secret-broadcast-id.ts","transport":"HTTP","mode":"VIDEO_AUDIO"}]}
            """);

        var target = info.DiagnosisTarget();

        Assert.Equal(("192.168.1.97", 8080), target);
        Assert.DoesNotContain("secret-broadcast-id", target!.Value.Host, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEndpointWithNoWrittenPortTakesTheSchemesDefault()
    {
        var info = Imported("""
            {"schemaVersion":1,"title":"Radio","url":"https://server.example/live-audio.aac","mode":"AUDIO_ONLY","sourceId":"target-skip",
             "endpoints":[{"url":"https://server.example/live-audio.aac","transport":"HTTP","mode":"AUDIO_ONLY"}]}
            """);

        Assert.Equal(("server.example", 443), info.DiagnosisTarget());
    }

    [Fact]
    public void ABroadcastWithNoEndpointToCheckHasNoTarget_SoNoCheckIsOffered()
    {
        var info = new FastMediaSorterBroadcastInfo { Mode = "AUDIO_ONLY", Endpoints = [] };

        Assert.Null(info.DiagnosisTarget());
    }
}
