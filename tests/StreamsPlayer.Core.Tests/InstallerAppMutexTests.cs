using System.Text.RegularExpressions;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

// SP-0136: the installer asks the user to close a running copy by checking the SP-0118 lock. The name in
// installer/StreamsPlayer.iss is a second spelling of SingleInstanceIdentity.ProductMutexName, and Windows
// compares mutex names case-sensitively - a drift would silently disable the check.
public sealed class InstallerAppMutexTests
{
    private static string Script =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "installer", "StreamsPlayer.iss"));

    [Fact]
    public void InstallerLockName_EqualsTheApplicationsFrozenLockName()
    {
        var match = Regex.Match(Script, @"^#define AppMutexName ""(?<name>[^""]+)""\s*$", RegexOptions.Multiline);

        Assert.True(match.Success, "installer/StreamsPlayer.iss no longer defines AppMutexName.");
        Assert.Equal(SingleInstanceIdentity.ProductMutexName, match.Groups["name"].Value);
    }

    [Fact]
    public void InstallerChecksTheLock_InTheWizardAndInSilentRuns()
    {
        var script = Script;

        Assert.Matches(new Regex(@"^AppMutex=\{#AppMutexName\}\s*$", RegexOptions.Multiline), script);
        Assert.Contains("WizardSilent and CheckForMutexes('{#AppMutexName}')", script, StringComparison.Ordinal);
        Assert.Contains("UninstallSilent and CheckForMutexes('{#AppMutexName}')", script, StringComparison.Ordinal);
    }
}
