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
            @"^\[InstallDelete\]\s*\r?\nType: filesandordirs; Name: \x22\{app\}\x22; Check: FileExistsInApp\('StreamsPlayer\.exe'\) or FileExistsInApp\('unins000\.exe'\)\s*$",
            RegexOptions.Multiline);

        Assert.True(wipe.Success, "installer/StreamsPlayer.iss no longer wipes {app} before installing - an upgrade would keep files the new payload dropped.");
    }

    [Fact]
    public void Upgrade_WipeIsGuardedByPreviousInstallationCheck()
    {
        // SP-0183: Check that the FileExistsInApp function exists to guard the wipe
        var checkFunction = Regex.Match(Script, @"function\s+FileExistsInApp\s*\(\s*FileName:\s*String\s*\)\s*:\s*Boolean");

        Assert.True(checkFunction.Success, "installer/StreamsPlayer.iss no longer guards the wipe with FileExistsInApp - Setup may delete a folder it did not install.");
    }

    // A Pascal brace comment ends at the first closing brace, so one that names an Inno constant such as the app
    // folder ends early and the rest of the sentence is read as code: ISCC stops with "'BEGIN' expected". The
    // 26.1002.2330 release job failed on exactly that, after every local gate had passed - none of them compiles
    // the installer. Line comments carry constants safely, so a brace comment holding a brace is the thing to refuse.
    [Fact]
    public void CodeSection_HasNoBraceCommentThatContainsAnotherBrace()
    {
        var code = Script[Script.IndexOf("[Code]", StringComparison.Ordinal)..];
        var offenders = Regex.Matches(code, @"^[ \t]*\{[^}\r\n]*\{", RegexOptions.Multiline)
            .Select(match => match.Value.Trim())
            .ToList();

        Assert.True(offenders.Count == 0, "A brace comment in [Code] contains a brace and would end early: " + string.Join(" | ", offenders));
    }
}
