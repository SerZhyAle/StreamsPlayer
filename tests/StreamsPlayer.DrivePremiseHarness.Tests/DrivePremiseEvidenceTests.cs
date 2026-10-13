using StreamsPlayer.DrivePremiseHarness;

namespace StreamsPlayer.DrivePremiseHarness.Tests;

/// <summary>
/// The experiment's evidence discipline: the verdict lines the report will quote are the house check shape, and
/// the marker a <c>write</c> run leaves is what a later <c>list</c> run holds the listing against.
/// </summary>
public sealed class DrivePremiseEvidenceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "sp-0202-evidence-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Verdict_IsTheHouseCheckShape()
    {
        Assert.Equal("expected: listed | actual: listed", DrivePremiseEvidence.Verdict("listed", "listed"));
        Assert.Equal("expected: listed | actual: not-listed", DrivePremiseEvidence.Verdict("listed", "not-listed"));
        Assert.Equal("expected: listed | actual: error: the listing failed", DrivePremiseEvidence.Verdict("listed", "error: the listing failed"));
    }

    [Fact]
    public void ProbeMarker_RoundTripsAndListsAgain()
    {
        var marker = new ProbeMarker("-DfFxPt42N4q_uTZ-zB66w", "fmsx-broadcast--DfFxPt42N4q_uTZ-zB66w.json", "driveFileId", "desktop-A", "2026-10-14T12:00:00Z");
        DrivePremiseEvidence.SaveProbeMarker(directory, marker);

        var markers = DrivePremiseEvidence.LoadProbeMarkers(directory);
        var loaded = Assert.Single(markers);
        Assert.Equal(marker, loaded);
    }

    [Fact]
    public void ProbeMarker_IsAbsentBeforeAnyWrite()
    {
        Assert.Empty(DrivePremiseEvidence.LoadProbeMarkers(directory));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
