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

    // SP-0212 (L1, L2, L4): the gates that decide a release but live in scripts - the release-parity
    // gate's own wiring, the extracted smoke-verdict gate, and the Store package build that runs it.
    // Read as text on the same terms as the workflows above: none of them can be unit-tested directly.
    private static string CheckGate =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scripts", "check.ps1"));

    private static string SmokeGate =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scripts", "Assert-SmokeVerdict.ps1"));

    private static string MsixBuild =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "msix", "build-msix.ps1"));

    [Fact]
    public void CheckGate_RunsTheAuditCampaignFixtureTests()
    {
        Assert.Contains("tools/audit/Test-AuditVerdictGate.ps1", CheckGate, StringComparison.Ordinal);
        Assert.Contains("tools/audit/Test-AuditTools.ps1", CheckGate, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckGate_ChecksTheCatalogSnapshotStructurally()
    {
        Assert.Contains("build-catalog-snapshot.ps1') -Check -Offline", CheckGate, StringComparison.Ordinal);
        Assert.Contains("./tools/build-catalog-snapshot.ps1 -Check -Offline", Release, StringComparison.Ordinal);
    }

    [Fact]
    public void SmokeVerdictGate_RefusesAVerdictThatDoesNotBindTheTaggedCode()
    {
        Assert.Contains("gate -ne 'smoke-playback'", SmokeGate, StringComparison.Ordinal);
        Assert.Contains("result -ne 'PASS'", SmokeGate, StringComparison.Ordinal);
        Assert.Contains("skippedChecks.Count -gt 0", SmokeGate, StringComparison.Ordinal);
        Assert.Contains("version -ne $Version", SmokeGate, StringComparison.Ordinal);
        Assert.Contains("git merge-base --is-ancestor $smokeCommit HEAD", SmokeGate, StringComparison.Ordinal);
        Assert.Contains("-notlike 'release-verdicts/*'", SmokeGate, StringComparison.Ordinal);
    }

    [Fact]
    public void MsixBuild_RefusesAPackageWithoutTheSmokeVerdictForTheTaggedVersion()
    {
        Assert.Contains("Assert-SmokeVerdict.ps1", MsixBuild, StringComparison.Ordinal);
        Assert.True(
            MsixBuild.IndexOf("Assert-AuditVerdict.ps1", StringComparison.Ordinal)
                < MsixBuild.IndexOf("Assert-SmokeVerdict.ps1", StringComparison.Ordinal),
            "msix/build-msix.ps1 must judge the audit verdict before the smoke verdict, both before it packs.");
    }

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
        // SP-0212 (L4): the checks live in scripts/Assert-SmokeVerdict.ps1, which the Store package
        // build runs too; this suite holds that script's own checks in place below.
        Assert.Contains("./scripts/Assert-SmokeVerdict.ps1 -Version $env:RELEASE_VERSION", step.Value, StringComparison.Ordinal);
    }

    // SP-0180: a release is also an audited release; the verdict gate runs before anything is built.
    [Fact]
    public void Release_RefusesToBuildWithoutTheCodeAuditVerdictForTheTaggedVersion()
    {
        var step = Regex.Match(Release, @"- name: Require the code-audit verdict for this version(?s).*?(?=- name:)");
        Assert.True(step.Success, "release.yml no longer gates the release on the committed code-audit verdict.");
        Assert.Contains("Assert-AuditVerdict.ps1 -Version $env:RELEASE_VERSION", step.Value, StringComparison.Ordinal);
        Assert.True(
            step.Index < Release.IndexOf("actions/setup-dotnet", StringComparison.Ordinal),
            "The code-audit gate must run before the build starts.");
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
        Assert.Matches(new Regex(@"if: steps\.guard\.outputs\.proceed == 'true'"), upload.Value);
        Assert.DoesNotContain("gh api -X DELETE", guard.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Release_CompletePublicationSkipsTheBuildAndUpload()
    {
        var inspection = Release.IndexOf("- name: Inspect assets already published for this tag", StringComparison.Ordinal);
        var setup = Release.IndexOf("actions/setup-dotnet", StringComparison.Ordinal);
        Assert.True(inspection >= 0 && inspection < setup);
        var steps = Regex.Matches(Release[setup..], @"(?m)^      - (?:name:|uses:).*?(?=^      - |\z)", RegexOptions.Singleline);
        Assert.NotEmpty(steps);
        foreach (Match step in steps)
        {
            Assert.Contains(step.Value.Contains("- name: Create GitHub Release", StringComparison.Ordinal)
                ? "if: steps.guard.outputs.proceed == 'true'"
                : "if: steps.existing.outputs.complete != 'true'", step.Value, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Pages_EveryMainPushCanReplaceASupersededCancelledRun()
    {
        var trigger = Pages[..Pages.IndexOf("permissions:", StringComparison.Ordinal)];
        Assert.Contains("branches: [main]", trigger, StringComparison.Ordinal);
        Assert.DoesNotContain("paths:", trigger, StringComparison.Ordinal);
        Assert.Contains("-w ci.yml -c \"$sha\"", Pages, StringComparison.Ordinal);
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
