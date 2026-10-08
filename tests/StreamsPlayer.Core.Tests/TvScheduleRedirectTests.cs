using System.Net;
using System.Net.Http;
using System.Text;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0184 S6-3: the user agrees to download a schedule from one host, so a redirect is followed only while it
/// stays on that host - the request to any other host is never sent.
/// </summary>
public sealed class TvScheduleRedirectTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private const string Guide = """
        <tv>
          <channel id="a"><display-name>A</display-name></channel>
          <programme start="20260925140000 +0000" stop="20260925150000 +0000" channel="a"><title>T</title></programme>
        </tv>
        """;

    [Theory]
    [InlineData("https://guide.test/a.xml", "https://guide.test/b.xml", true)]
    [InlineData("https://guide.test/a.xml", "https://GUIDE.test/b.xml", true)]
    [InlineData("http://guide.test/a.xml", "https://guide.test/a.xml", true)]
    [InlineData("http://guide.test/a.xml", "http://guide.test/b.xml", true)]
    [InlineData("https://guide.test/a.xml", "http://guide.test/a.xml", false)]
    [InlineData("https://guide.test/a.xml", "https://other.test/a.xml", false)]
    [InlineData("https://guide.test/a.xml", "https://cdn.guide.test/a.xml", false)]
    public void RedirectIsAllowedOnlyOnTheConsentedHost(string source, string target, bool allowed) =>
        Assert.Equal(allowed, TvScheduleService.IsRedirectAllowed(new Uri(source), new Uri(target)));

    [Fact]
    public async Task SameHostRedirect_IsFollowed()
    {
        var handler = new RedirectingHandler(request => request.RequestUri!.AbsolutePath == "/a.xml"
            ? Redirect("/b.xml")
            : Ok());
        var document = await Download(handler, "https://guide.test/a.xml");

        Assert.Equal(["/a.xml", "/b.xml"], handler.Requested.Select(uri => uri.AbsolutePath));
        Assert.Single(document.Channels);
    }

    [Fact]
    public async Task CrossHostRedirect_IsRefusedWithoutRequestingTheOtherHost()
    {
        var handler = new RedirectingHandler(_ => Redirect("https://other.test/guide.xml"));

        await Assert.ThrowsAsync<HttpRequestException>(() => Download(handler, "https://guide.test/a.xml"));

        Assert.Equal(["guide.test"], handler.Requested.Select(uri => uri.Host));
    }

    [Fact]
    public async Task EndlessRedirects_AreStopped()
    {
        var handler = new RedirectingHandler(_ => Redirect("/again.xml"));

        await Assert.ThrowsAsync<HttpRequestException>(() => Download(handler, "https://guide.test/a.xml"));

        Assert.Equal(TvScheduleService.MaximumRedirects + 1, handler.Requested.Count);
    }

    private static Task<TvScheduleDocument> Download(RedirectingHandler handler, string source)
    {
        using var client = new HttpClient(handler);
        return new TvScheduleService(client).DownloadAsync(new Uri(source), Now, null, CancellationToken.None);
    }

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(Encoding.UTF8.GetBytes(Guide)),
    };

    private sealed class RedirectingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }
}
