using System.Text.RegularExpressions;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0224 requirement 5: every exchange window links to the portal page that explains it, through the one place that
/// picks the page and the language. Pinned over the App's own sources read as text (the WPF project is not loaded here).
/// Whether a page answers over HTTP is checked against the live portal, not here: a unit test must not need a network.
/// </summary>
public sealed class ExchangePortalLinkTests
{
    private static AppSourceFile Source(string fileName) => Assert.Single(AppSourceFile.LoadAll(fileName));

    // Window -> the portal page its link opens. Account is the sign-in and password-for-the-account story, Broadcasts
    // the list and the broadcasting story, Home the guide's front page (the diagnosis spans both).
    [Theory]
    [InlineData("BroadcastsWindow", "Broadcasts")]
    [InlineData("BroadcastWindow", "Broadcasts")]
    [InlineData("BroadcastPasswordWindow", "Broadcasts")]
    [InlineData("ExchangePasswordWindow", "Account")]
    [InlineData("ExchangeDiagnosisWindow", "Home")]
    public void EveryExchangeWindowCarriesAMediaExchangeLinkToItsPortalPage(string window, string page)
    {
        var markup = Source(window + ".xaml");
        var link = Regex.Match(markup.Text,
            @"<Button\b[^>]*Content=""\{DynamicResource MediaExchange\}""[^>]*Click=""ExchangeHelp_Click""|<Button\b[^>]*Click=""ExchangeHelp_Click""[^>]*Content=""\{DynamicResource MediaExchange\}""",
            RegexOptions.Singleline);
        Assert.True(link.Success, $"{window}.xaml has no MediaExchange link wired to ExchangeHelp_Click.");

        var code = Source(window + ".xaml.cs");
        Assert.Matches(
            @"void ExchangeHelp_Click\(object sender, RoutedEventArgs e\)\s*=>\s*ExchangeHelp\.Open\(this, ExchangePortalPage\." + page + @"\)",
            code.Masked);
    }

    [Fact]
    public void TheSettingsExchangeAndBroadcastingPagesLinkToTheirPortalPages()
    {
        var markup = Source("SettingsWindow.xaml").Text;
        // Text, not Masked: the routing keys are string literals, which masking blanks.
        var code = Source("SettingsWindow.xaml.cs").Text;
        foreach (var tag in new[] { "MediaExchange", "ExchangeAccount", "ExchangeBroadcasts" })
        {
            Assert.Contains($"Tag=\"{tag}\"", markup, StringComparison.Ordinal);
            Assert.Matches(@"""" + tag + @"""\s*=>\s*ProductInfo\.ExchangePortalUrl\(currentLanguage", code);
        }
    }

    [Fact]
    public void ThePortalAddressIsPickedInOnePlaceAndFollowsTheInterfaceLanguage()
    {
        var info = Source("ProductInfo.cs").Text;
        // The three page stems and the two editions the portal has; every other interface language reads English.
        Assert.Contains("\"exchange-server\"", info, StringComparison.Ordinal);
        Assert.Contains("\"live-broadcast\"", info, StringComparison.Ordinal);
        Assert.Contains("AppLanguage.Russian => \"-ru\"", info, StringComparison.Ordinal);
        Assert.Contains("AppLanguage.Ukrainian => \"-uk\"", info, StringComparison.Ordinal);

        var help = Source("ExchangeHelp.cs").Masked;
        Assert.Contains("ProductInfo.ExchangePortalUrl(LocalizationService.CurrentLanguage, page)", help, StringComparison.Ordinal);
    }
}
