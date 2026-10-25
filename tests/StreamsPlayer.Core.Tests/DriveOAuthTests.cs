using System.Net;
using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class DriveOAuthTests
{
    [Fact]
    public void AuthorizationIsSingleScopePkceInTheSystemBrowser()
    {
        using var http = new HttpClient();
        var oauth = new DriveOAuth(http, "public-client", "desktop-secret");
        var url = oauth.AuthorizationUrl("http://127.0.0.1:12345/", "random-state", "verifier");
        Assert.Contains(Uri.EscapeDataString(DriveOAuth.Scope), url);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains("access_type=offline", url);
        Assert.DoesNotContain("desktop-secret", url);
        Assert.DoesNotContain("openid", url);
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", DriveOAuth.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    }

    [Fact]
    public async Task WiderGrantIsRevokedAndNeverReturned()
    {
        using var handler = new OAuthHandler(DriveOAuth.Scope + " openid");
        using var http = new HttpClient(handler);
        var oauth = new DriveOAuth(http, "client", "secret");
        await Assert.ThrowsAsync<InvalidDataException>(() => oauth.ExchangeAsync("code", "http://127.0.0.1:12345/", "verifier", default));
        Assert.True(handler.Revoked);
    }

    [Theory]
    [InlineData("access_token=private-token")]
    [InlineData("RefreshToken=private-token")]
    [InlineData("client_id=private-token")]
    [InlineData("code_verifier=private-token")]
    public void DiagnosticsRedactGoogleValues(string text) => Assert.DoesNotContain("private-token", DiagnosticValueRedactor.Redact(text));

    private sealed class OAuthHandler(string scope) : HttpMessageHandler
    {
        public bool Revoked { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Assert.DoesNotContain("secret", request.RequestUri!.AbsoluteUri);
            if (request.RequestUri.AbsolutePath == "/revoke")
            {
                Assert.Contains("token=refresh", body);
                Revoked = true;
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            Assert.Contains("client_secret=secret", body);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            { access_token = "access", refresh_token = "refresh", expires_in = 3600, scope })) };
        }
    }
}
