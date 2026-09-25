namespace StreamsPlayer.Core.Tests;

/// <summary>Guards the App-side rule that a state mutation is evaluated only after it owns the commit gate.</summary>
public sealed class StateCommitSourceTests
{
    [Fact]
    public void MainWindowNeverPersistsAPrecomputedStateSnapshot()
    {
        var offenders = AppSourceFile.LoadAll("MainWindow*.cs")
            .Where(source => source.Text.Contains("PersistAsync(_state", StringComparison.Ordinal) ||
                             source.Text.Contains("await _store.SaveAsync", StringComparison.Ordinal))
            .Select(source => source.Name)
            .ToArray();

        Assert.True(offenders.Length == 0,
            $"State writes must go through a mutation delegate, not a stale snapshot: {string.Join(", ", offenders)}");
    }
}
