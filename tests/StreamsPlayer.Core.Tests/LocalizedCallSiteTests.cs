namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0057: the gate. <see cref="LocalizationParityTests"/> keeps the thirteen dictionaries agreeing with
/// each other; this keeps them agreeing with the code that formats them.
/// </summary>
/// <remarks>
/// The hole it closes was found the expensive way. SP-0056 gave <c>ChannelPreviewsWorking</c> a second
/// placeholder, which the parity gate then correctly forced into all thirteen languages - and nothing
/// anywhere compared that against the one call site passing one argument. A <see cref="FormatException"/>
/// from an <c>async void</c> handler is not a wrong status line; it reaches
/// <c>DispatcherUnhandledException</c>, which logs without handling, and the window vanishes.
/// </remarks>
public sealed class LocalizedCallSiteTests
{
    private static IReadOnlyDictionary<string, string> English =>
        LocalizationDictionary.LoadAll().Single(dictionary => dictionary.Code == "en").Values;

    private static GateReport Report => LocalizedCallSiteGate.Inspect(AppSourceFile.LoadAll("*.cs"), English);

    [Fact]
    public void EveryCallSiteSuppliesExactlyThePlaceholdersItsStringNeeds()
    {
        // Both directions in one comparison, because they are the same inequality read twice. Too few is a
        // crash; too many is silent at runtime and is the signature of a placeholder deleted from the
        // string and left behind in the code, which nothing else would ever report.
        var report = Report;

        Assert.True(report.Problems.Count == 0, string.Join(Environment.NewLine, report.Problems));
    }

    [Fact]
    public void NoPlaceholderStringIsBoundFromMarkup()
    {
        // A DynamicResource binding cannot carry an argument, so a key with a placeholder would print its
        // own braces. Zero violations today: this ships as a guard, not as a cleanup.
        var problems = LocalizedCallSiteGate.InspectMarkup(AppSourceFile.LoadAll("*.xaml"), English);

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void TheGateActuallyInspectedTheCallSites()
    {
        // Without this the gate would pass loudest exactly when it had stopped working: a masker that
        // silently stopped matching finds no call sites and reports no problems. The floors are set well
        // below the counts measured when the gate was written, so ordinary growth does not touch them and
        // a collapse still fails.
        var sources = AppSourceFile.LoadAll("*.cs");
        var report = LocalizedCallSiteGate.Inspect(sources, English);

        Assert.True(sources.Count >= 50, $"Only {sources.Count} application sources were read.");
        Assert.True(report.CallSites >= 150, $"Only {report.CallSites} literal-key call sites were found.");
        Assert.True(report.Keys.Count >= 120, $"Only {report.Keys.Count} distinct keys were gated.");

        // Named call sites, one per shape the reader has to handle: a direct two-argument status line, a
        // key reached through a wrapper that supplies the arguments itself, and a conditional key.
        Assert.Contains("ChannelCount", report.Keys);
        Assert.Contains("ChannelPreviewsWorking", report.Keys);
        Assert.Contains("ResumeNothingToPlay", report.Keys);
    }
}
