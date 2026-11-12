namespace StreamsPlayer.Core.Tests;

public sealed class BroadcastOpenDiagnosticsTests
{
    [Fact]
    public void ShapeKeepsOriginalOrderAndNeverFormatsUntrustedValues()
    {
        var broadcast = new FastMediaSorterBroadcastInfo
        {
            Mode = "VIDEO_AUDIO", Auth = new("password-v1", "secret-user"),
            Endpoints = [new("rtsp://secret-url", "RTSP", null, null, null, null, null, null, null),
                new("fmsx://secret-url", "TUNNEL", null, null, null, null, null, null, null, Seal: new(1, "secret-key", "secret-pin"))]
        };
        Assert.Equal(new[] { "auth=present", "scheme=password-v1", "endpoints=0:RTSP:sealed=false;1:TUNNEL:sealed=true" }, BroadcastOpenDiagnostics.Fields(broadcast));
        var directory = Path.Combine(Path.GetTempPath(), "SP0249-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var log = new StreamsPlayer.App.CurrentLog(directory))
                log.Event("BROADCAST DESCRIPTOR", BroadcastOpenDiagnostics.Fields(broadcast));
            var text = File.ReadAllText(Path.Combine(directory, DiagnosticLogFiles.CurrentLogName));
            Assert.Contains("BROADCAST DESCRIPTOR | auth=present | scheme=password-v1 | endpoints=0:RTSP:sealed=false;1:TUNNEL:sealed=true", text);
            Assert.DoesNotContain("secret", text);
        }
        finally { Directory.Delete(directory, recursive: true); }
        var unknown = broadcast with { Auth = new("secret-scheme", "secret-user"), Endpoints = [broadcast.Endpoints[0] with { Transport = "secret-transport" }] };
        Assert.DoesNotContain("secret", string.Join('|', BroadcastOpenDiagnostics.Fields(unknown)));
        Assert.Equal("auth=absent", BroadcastOpenDiagnostics.Fields(broadcast with { Auth = null })[0]);
    }
}
