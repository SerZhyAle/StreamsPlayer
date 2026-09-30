using System.Text.RegularExpressions;

namespace StreamsPlayer.Core.Tests;

// SP-0156: a release ships only a gated payload, and the gates live in text a workflow run could silently
// lose. The workflows are read as test data on the installer-script terms - a workflow cannot be
// unit-tested, so CI holds its gates in place by holding the lines that are the gates.
public sealed class ReleaseWorkflowGateTests
{
    private static string Release =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "workflows", "release.yml"));

    private static string Pages =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "workflows", "pages.yml"));

    [Fact]
    public void Release_AsksForActionsRead()
    {
        Assert.Contains("actions: read", Release, StringComparison.Ordinal);
    }

    [Fact]
    public void Release_RefusesToBuildWithoutAGreenCiRunForTheTaggedCommit()
    {
        var step = Regex.Match(Release, @"- name: Require green CI for the tagged commit(?s).*?(?=- name:)");
        Assert.True(step.Success, "release.yml no longer gates the release on a green CI run for the tagged commit.");
        Assert.Contains("-w ci.yml -c $sha", step.Value, StringComparison.Ordinal);
        Assert.Contains("conclusion -ne 'success'", step.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Release_RefusesToBuildWithoutTheSmokeVerdictForTheTaggedVersion()
    {
        var step = Regex.Match(Release, @"- name: Require the playback smoke verdict for this version(?s).*?(?=- name:)");
        Assert.True(step.Success, "release.yml no longer gates the release on the committed smoke verdict.");
        Assert.Contains("release-verdicts", step.Value, StringComparison.Ordinal);
        Assert.Contains("result -ne 'PASS'", step.Value, StringComparison.Ordinal);
        Assert.Contains("version -ne $env:RELEASE_VERSION", step.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Release_AppliesThePinnedNativesCheckToThePayload()
    {
        Assert.Matches(
            new Regex(@"Assert-PinnedNatives\.ps1 -Folder stage/StreamsPlayer -ProjectPath src/StreamsPlayer\.App/StreamsPlayer\.App\.csproj"),
            Release);
    }

    [Fact]
    public void Release_NeverReplacesAPublishedAssetWithoutProvingItIdentical()
    {
        var guard = Regex.Match(Release, @"- name: Guard assets already published for this tag(?s).*?(?=- name: Create GitHub Release)");
        Assert.True(guard.Success, "release.yml no longer guards the assets an earlier run already published.");
        Assert.Contains("Get-FileHash", guard.Value, StringComparison.Ordinal);
        Assert.Contains("never replaced silently", guard.Value, StringComparison.Ordinal);

        var upload = Regex.Match(Release, @"- name: Create GitHub Release(?s).*?fail_on_unmatched_files");
        Assert.True(upload.Success, "release.yml no longer uploads through the guarded release step.");
        Assert.Matches(new Regex(@"if: steps\.guard\.outputs\.proceed != 'false'"), upload.Value);
    }

    [Fact]
    public void Pages_DeploysOnlyAfterTheCiGatePassed()
    {
        Assert.Matches(new Regex(@"^  ci-gate:\s*$", RegexOptions.Multiline), Pages);
        var deploy = Regex.Match(Pages, @"^  deploy:", RegexOptions.Multiline);
        Assert.True(deploy.Success, "pages.yml lost its deploy job.");
        var deployBlock = Pages.Substring(deploy.Index);
        Assert.Matches(new Regex(@"^    needs: ci-gate\s*$", RegexOptions.Multiline), deployBlock);
        Assert.Contains("ci.yml", Pages, StringComparison.Ordinal);
    }
}
