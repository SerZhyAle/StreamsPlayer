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

    private static string Ci =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "workflows", "ci.yml"));

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

    // SP-0247 (PACKAGE-VERSIONING P3, P5): the version comes from the function scripts/Test-ReleaseVersion.ps1 tests,
    // and is read back out of the zip and the setup EXE after each is built. The scripts hold their own fixture tests;
    // this holds the lines that call them.
    [Fact]
    public void Release_DerivesTheVersionThroughTheTestedFunctionAndReadsItBack()
    {
        Assert.Contains("ConvertFrom-ReleaseTag -Tag $tag", Release, StringComparison.Ordinal);
        Assert.Contains("ConvertFrom-ReleaseTag -Tag $tag", MsixBuild, StringComparison.Ordinal);
        Assert.Contains("ConvertTo-MsixVersion -Version $appVersion", MsixBuild, StringComparison.Ordinal);
        Assert.Contains("scripts/Test-ReleaseVersion.ps1", CheckGate, StringComparison.Ordinal);

        var zip = Release.IndexOf("Compress-Archive", StringComparison.Ordinal);
        var zipReadBack = Release.IndexOf("Assert-ReleaseArtifactVersion.ps1 -Version $env:RELEASE_VERSION -ExePath stage/StreamsPlayer/StreamsPlayer.exe -ZipPath", StringComparison.Ordinal);
        var setup = Release.IndexOf("& $iscc", StringComparison.Ordinal);
        var setupReadBack = Release.IndexOf("Assert-ReleaseArtifactVersion.ps1 -Version $env:RELEASE_VERSION -SetupPath", StringComparison.Ordinal);
        var upload = Release.IndexOf("- name: Create GitHub Release", StringComparison.Ordinal);
        Assert.True(zip >= 0 && zip < zipReadBack && zipReadBack < upload, "release.yml no longer reads the version back from the zip before the upload.");
        Assert.True(setup >= 0 && setup < setupReadBack && setupReadBack < upload, "release.yml no longer reads the version back from the setup EXE before the upload.");
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

    // SP-0256: push CI carries only the fast tier, so the checks it used to run judge the tag's own tree here,
    // and all of them before the upload.
    [Fact]
    public void Release_RunsTheChecksPushCiNoLongerCarriesOnTheTaggedTree()
    {
        var upload = Release.IndexOf("- name: Create GitHub Release", StringComparison.Ordinal);
        var build = Release.IndexOf("- name: Build", StringComparison.Ordinal);
        foreach (var check in new[]
                 {
                     "scripts/check-docs.ps1",
                     "scripts/test.ps1 -Configuration Release -NoBuild",
                     "scripts/Test-TestResultSummary.ps1",
                     "tools/store/Test-StoreListing.ps1",
                     "tools/site/build-site.ps1 -Check",
                 })
        {
            var at = Release.IndexOf(check, StringComparison.Ordinal);
            Assert.True(at >= 0 && at < upload, $"release.yml no longer runs {check} before the upload.");
        }

        Assert.True(
            build < Release.IndexOf("tools/site/build-site.ps1 -Check", StringComparison.Ordinal),
            "The site-sync check reads the Core assembly, so it must run after the build.");
    }

    // SP-0256: between releases only checks of 5 s or less run (canon TESTING_AND_QA section 2), so push CI
    // neither builds nor runs the suite.
    [Fact]
    public void Ci_RunsOnlyTheFastTier()
    {
        Assert.Contains("scripts/check-fast.ps1", Ci, StringComparison.Ordinal);
        foreach (var slow in new[] { "dotnet ", "scripts/test.ps1", "build-site.ps1", "Test-StoreListing.ps1" })
        {
            Assert.DoesNotContain(slow, Ci, StringComparison.Ordinal);
        }
    }

    // SP-0256 (owner decision, 2026-10-30): the site deploys after a successful release run or on an explicit
    // run, never on push.
    [Fact]
    public void Pages_DeploysAfterASuccessfulReleaseRunOrAnExplicitRunOnly()
    {
        var trigger = Pages[..Pages.IndexOf("permissions:", StringComparison.Ordinal)];
        Assert.DoesNotMatch(new Regex(@"^  push:", RegexOptions.Multiline), trigger);
        Assert.Contains("workflow_run:", trigger, StringComparison.Ordinal);
        Assert.Contains("workflows: [Release]", trigger, StringComparison.Ordinal);
        Assert.Contains("workflow_dispatch:", trigger, StringComparison.Ordinal);
        Assert.Contains("github.event.workflow_run.conclusion == 'success'", Pages, StringComparison.Ordinal);
    }

    // SP-0156 criterion 6, kept by SP-0256: the documentation gate and the site-sync check pass on the very commit
    // the deploy job publishes, before it runs.
    [Fact]
    public void Pages_DeploysOnlyTheCommitItsOwnGatesJudged()
    {
        var deploy = Regex.Match(Pages, @"^  deploy:", RegexOptions.Multiline);
        Assert.True(deploy.Success, "pages.yml lost its deploy job.");
        var gateBlock = Pages[..deploy.Index];
        var deployBlock = Pages[deploy.Index..];
        Assert.Matches(new Regex(@"^  gate:\s*$", RegexOptions.Multiline), gateBlock);
        Assert.Contains("scripts/check-docs.ps1", gateBlock, StringComparison.Ordinal);
        Assert.Contains("tools/site/build-site.ps1 -Check", gateBlock, StringComparison.Ordinal);
        Assert.Contains("ref: ${{ steps.commit.outputs.sha }}", gateBlock, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"^    needs: gate\s*$", RegexOptions.Multiline), deployBlock);
        Assert.Contains("ref: ${{ needs.gate.outputs.sha }}", deployBlock, StringComparison.Ordinal);
    }
}
