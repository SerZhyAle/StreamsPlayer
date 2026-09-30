using System.Text.RegularExpressions;

namespace StreamsPlayer.Core.Tests;

// SP-0156: an upgrade must leave the install folder holding exactly the new payload. The wipe lives in
// the installer script as an [InstallDelete] over {app}; if the section is lost, an upgraded install
// quietly recombines every version's files - including a media plugin a later engine removed - and the
// installer stops carrying the same payload as the archive beside it. User data is not at risk: it
// lives in %LOCALAPPDATA%\StreamsPlayer, which this section never touches.
public sealed class InstallerCleanUpgradeTests
{
    private static string Script =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "installer", "StreamsPlayer.iss"));

    [Fact]
    public void Upgrade_WipesTheInstallFolderBeforeInstallingTheNewPayload()
    {
        var wipe = Regex.Match(
            Script,
            @"^\[InstallDelete\]\s*\r?\nType: filesandordirs; Name: ""\{app\}""\s*$",
            RegexOptions.Multiline);

        Assert.True(wipe.Success, "installer/StreamsPlayer.iss no longer wipes {app} before installing - an upgrade would keep files the new payload dropped.");
    }
}
