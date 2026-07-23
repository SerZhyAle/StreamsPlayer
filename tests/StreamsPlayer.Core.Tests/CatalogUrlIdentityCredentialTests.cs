using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class CatalogUrlIdentityCredentialTests
{
    [Theory]
    [InlineData("https://alice:s3cr3t@host.example/live.m3u8")]
    [InlineData("https://host.example/live?token=ABC123")]
    [InlineData("https://host.example/live?region=eu&auth=xyz")]
    public void HasCredentials_TrueForUserInfoOrCredentialQuery(string url) =>
        Assert.True(CatalogUrlIdentity.HasCredentials(url));

    [Theory]
    [InlineData("https://host.example/live.m3u8")]
    [InlineData("https://host.example/live?region=eu&quality=hd")]
    [InlineData("rtsp://cam.example/stream")]
    [InlineData("not a url")]
    [InlineData("")]
    public void HasCredentials_FalseWhenNoCredentialsPresent(string url) =>
        Assert.False(CatalogUrlIdentity.HasCredentials(url));
}
