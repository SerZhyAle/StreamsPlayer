using System.Text.RegularExpressions;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0225 requirement 2, held over the App's own sources: the connection check runs when a person asks and
/// never otherwise. The engine is called from the check window alone, and the window is opened from a click
/// handler alone - no timer, no start-up path, no catch block reaches it.
/// </summary>
public sealed class ExchangeDiagnosisSourceTests
{
    [Fact]
    public void OnlyTheCheckWindowRunsTheEngine()
    {
        var callers = AppSourceFile.LoadAll("*.cs")
            .Where(file => file.Invocations("ExchangeDiagnosisEngine.RunAsync").Count > 0)
            .Select(file => file.Name).ToArray();

        Assert.Equal(["ExchangeDiagnosisWindow.xaml.cs"], callers);
    }

    [Fact]
    public void TheCheckWindowIsOpenedOnlyFromAClickHandlerOfADiagnoseButton()
    {
        var method = new Regex(@"(?:private|internal|public)\s+[\w<>?,\s]+?\s+(\w+)\s*\(", RegexOptions.CultureInvariant);
        var openers = new List<string>();
        foreach (var file in AppSourceFile.LoadAll("*.cs"))
        {
            foreach (var construction in Regex.Matches(file.Masked, @"new\s+ExchangeDiagnosisWindow\s*\("))
            {
                var at = ((Match)construction).Index;
                var enclosing = method.Matches(file.Masked[..at]).LastOrDefault()?.Groups[1].Value;
                openers.Add($"{file.Name}:{enclosing}");
                Assert.True(enclosing is not null && enclosing.EndsWith("Diagnose_Click", StringComparison.Ordinal),
                    $"{file.Name}:{file.LineAt(at)} opens the connection check from '{enclosing}', which is not a Diagnose click handler.");
            }
        }

        // The three doors: Settings, the list window, a failed broadcast's window.
        Assert.Equal(3, openers.Count);
    }

    [Fact]
    public void NeitherTheEngineNorTheProbesAreReachedFromTheServiceOrAnyTimer()
    {
        foreach (var file in AppSourceFile.LoadAll("*.cs").Where(file => file.Name != "ExchangeDiagnosisWindow.xaml.cs"
                     && file.Name != "ExchangeDiagnosisProbes.cs"))
        {
            Assert.DoesNotContain("new ExchangeDiagnosisProbes(", file.Masked, StringComparison.Ordinal);
        }
    }
}
