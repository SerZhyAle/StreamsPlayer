using System.Net;
using StreamsPlayer.DrivePremiseHarness;

namespace StreamsPlayer.DrivePremiseHarness.Tests;

public sealed class DrivePremiseOAuthTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("desktop-secret")]
    public async Task TokenExchange_SendsPkceAndOptionalDesktopSecret(string? secret)
    {
        using var handler = new TokenHandler(HttpStatusCode.OK, """{"access_token":"memory-only-token"}""");
        using var client = new HttpClient(handler);

        var token = await DrivePremiseOAuth.ExchangeTokenAsync(client, "desktop-client", "http://localhost:54321/",
            "pkce-verifier", "one-use-code", CancellationToken.None, secret);

        Assert.Equal("memory-only-token", token);
        Assert.Equal("https://oauth2.googleapis.com/token", handler.RequestUri);
        Assert.Equal("application/x-www-form-urlencoded", handler.MediaType);
        var fields = handler.RequestBody!.Split('&').Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));
        Assert.Equal("desktop-client", fields["client_id"]);
        Assert.Equal("http://localhost:54321/", fields["redirect_uri"]);
        Assert.Equal("pkce-verifier", fields["code_verifier"]);
        Assert.Equal("one-use-code", fields["code"]);
        Assert.Equal("authorization_code", fields["grant_type"]);
        if (secret is null)
            Assert.False(fields.ContainsKey("client_secret"));
        else
            Assert.Equal(secret, fields["client_secret"]);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.OK)]
    public async Task TokenExchange_DoesNotReportCredentialBearingResponseBodies(HttpStatusCode status)
    {
        using var handler = new TokenHandler(status, """{"error_description":"private-response-marker","refresh_token":"private-token"}""");
        using var client = new HttpClient(handler);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DrivePremiseOAuth.ExchangeTokenAsync(client, "client", "http://localhost:54321/", "verifier", "code", CancellationToken.None));

        Assert.DoesNotContain("private-response-marker", exception.ToString());
        Assert.DoesNotContain("private-token", exception.ToString());
    }

    [Fact]
    public void EvidenceIdentifier_IsSha256RatherThanTheIdentifier()
    {
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", DrivePremiseEvidence.HashIdentifier("abc"));
    }

    private sealed class TokenHandler(HttpStatusCode status, string responseBody) : HttpMessageHandler
    {
        public string? RequestUri { get; private set; }
        public string? MediaType { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            RequestUri = request.RequestUri!.AbsoluteUri;
            MediaType = request.Content!.Headers.ContentType!.MediaType;
            RequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(responseBody) };
        }
    }
}
