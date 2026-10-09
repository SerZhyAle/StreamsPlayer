namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0058 / SP-0201 requirement 5 / SP-0180 wave C: the About window's "Copy all" hands the raw channel
/// address to the clipboard, so it must ask for the same confirmations as the share text.
/// </summary>
public sealed class ChannelHandOutWarningsTests
{
    [Theory]
    [InlineData("https://alice:s3cr3t@host.example/live.m3u8")]
    [InlineData("https://host.example/live?token=ABC123")]
    public void AnAddressWithACredentialAsksTheCredentialWarning(string url) =>
        Assert.Equal([ChannelHandOutWarnings.CredentialKey], ChannelHandOutWarnings.KeysFor(url));

    [Theory]
    [InlineData("fmsx://exchange.example.net:44022/b/ICEiIyQlJicoKSorLC0uLw/http")]
    [InlineData("https://relay.example.net/v2/b/ICEiIyQlJicoKSorLC0uLw/stream")]
    public void AListenCapabilityAddressAsksTheCapabilityWarning(string url) =>
        Assert.Equal([ChannelHandOutWarnings.CapabilityKey], ChannelHandOutWarnings.KeysFor(url));

    [Fact]
    public void AnAddressThatIsBothAsksBothInOrder() =>
        Assert.Equal(
            [ChannelHandOutWarnings.CredentialKey, ChannelHandOutWarnings.CapabilityKey],
            ChannelHandOutWarnings.KeysFor("https://alice:s3cr3t@relay.example.net/v2/b/ICEiIyQlJicoKSorLC0uLw/stream"));

    [Theory]
    [InlineData("https://host.example/live.m3u8")]
    [InlineData("http://192.168.1.20:8080/stream")]
    [InlineData("rtsp://cam.example/stream")]
    [InlineData("")]
    public void AnOrdinaryAddressAsksNothing(string url) =>
        Assert.Empty(ChannelHandOutWarnings.KeysFor(url));

    [Fact]
    public void TheKeysAreTheOnesTheShareDialogsAlreadyShip()
    {
        // Reusing the shipped keys is the whole point: no new string is added to thirteen dictionaries.
        foreach (var dictionary in LocalizationDictionary.LoadAll())
        {
            Assert.True(dictionary.Values.ContainsKey(ChannelHandOutWarnings.CredentialKey), dictionary.Code);
            Assert.True(dictionary.Values.ContainsKey(ChannelHandOutWarnings.CapabilityKey), dictionary.Code);
        }
    }
}
