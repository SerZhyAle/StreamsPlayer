using System.Net;
using StreamsPlayer.DrivePremiseHarness;

namespace StreamsPlayer.DrivePremiseHarness.Tests;

public sealed class DrivePremiseDriveTests
{
    [Fact]
    public async Task ProbeUploadUsesARelatedMultipartContentType()
    {
        using var handler = new UploadHandler();
        using var client = new HttpClient(handler);
        var drive = new DrivePremiseDrive(client);
        var file = await drive.UploadAsync("memory-only-access", "fmsx-broadcast-probe.json", "{\"probe\":true}", default);
        Assert.Equal("probe-file", file.Id);
    }

    private sealed class UploadHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("multipart/related", request.Content!.Headers.ContentType!.MediaType);
            Assert.Equal("sp0202premise", Assert.Single(request.Content.Headers.ContentType.Parameters).Value);
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            Assert.Contains("\"parents\":[\"appDataFolder\"]", body);
            Assert.Contains("{\"probe\":true}", body);
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("""{"id":"probe-file","name":"fmsx-broadcast-probe.json"}""") };
        }
    }
}
