namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0124, enforced over the App's own sources (read as text - see <see cref="AppSourceFile"/>): a channel
/// address is turned into a <see cref="Uri"/> only by <see cref="LaunchableAddress"/>.
/// </summary>
/// <remarks>
/// Each ad-hoc parse was a place the scheme rule could be forgotten: a <c>new Uri(channel.Url)</c> threw out
/// of an async handler on an address that does not parse, and a bare <c>Uri.TryCreate</c> let a
/// <c>file://</c> or network-share address reach an engine. So the App parses no absolute address itself.
/// The one construction left is a relative one - a resource path, which is never an address a stream
/// comes from.
/// </remarks>
public sealed class LaunchAddressSourceTests
{
    [Fact]
    public void NoAppFileParsesAnAddressOutsideTheLaunchHelper()
    {
        var findings = Findings(AppSourceFile.LoadAll("*.cs")).ToArray();

        Assert.True(
            findings.Length == 0,
            "Parse a channel address through LaunchableAddress, which refuses everything but http, https " +
            "and rtsp (STREAM-BANK launchable schemes):" + Environment.NewLine +
            string.Join(Environment.NewLine, findings.Select(finding => "  " + finding)));
    }

    [Fact]
    public void TheGateSeesTheConstructionItAllows()
    {
        // A gate that finds nothing to check passes on anything.
        var localization = AppSourceFile.LoadAll("LocalizationService.cs").Single();
        Assert.Single(Constructions(localization));
        Assert.Empty(Findings([localization]));
    }

    [Theory]
    [InlineData("class M { void P(StreamChannel channel) => _audio.Play(new Uri(channel.Url), 50); }")]
    [InlineData("class M { string T(string url) => new System.Uri(url).Host; }")]
    [InlineData("class M { Uri A(string url) => new Uri(url, UriKind.Absolute); }")]
    [InlineData("class M { bool H(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp; }")]
    [InlineData("class M { bool H(string url) => System.Uri.TryCreate(url.Trim(), UriKind.RelativeOrAbsolute, out _); }")]
    public void TheGateFailsAnAdHocParse(string source)
    {
        Assert.NotEmpty(Findings([AppSourceFile.Parse("MainWindow.Probe.cs", source)]));
    }

    [Theory]
    [InlineData("class M { void P(StreamChannel channel) { if (LaunchableAddress.TryParse(channel.Url, out var a)) _audio.Play(a, 50); } }")]
    [InlineData("class M { object D(string code) => new Uri($\"/Dictionaries/{code}.xaml\", UriKind.Relative); }")]
    [InlineData("class M { string T(string url) => LaunchableAddress.HostOf(url); /* not new Uri(url) */ }")]
    public void TheGatePassesTheHelperAndARelativeResource(string source)
    {
        Assert.Empty(Findings([AppSourceFile.Parse("MainWindow.Probe.cs", source)]));
    }

    private static IEnumerable<string> Findings(IEnumerable<AppSourceFile> sources)
    {
        foreach (var source in sources)
        {
            foreach (var call in source.Invocations("Uri.TryCreate"))
            {
                yield return $"{source.Name}:{source.LineAt(call.Offset)} Uri.TryCreate";
            }

            foreach (var construction in Constructions(source).Where(call => !IsRelative(source, call)))
            {
                yield return $"{source.Name}:{source.LineAt(construction.Offset)} new Uri";
            }
        }
    }

    /// <summary>Every <c>new Uri(..)</c> / <c>new System.Uri(..)</c> - a call to "Uri" whose name follows <c>new</c>.</summary>
    private static IEnumerable<Invocation> Constructions(AppSourceFile source) =>
        source.Invocations("Uri").Where(call =>
        {
            var before = source.Masked[..call.Offset].TrimEnd();
            if (before.EndsWith('.'))
            {
                before = before[..^1].TrimEnd();
                if (!before.EndsWith("System", StringComparison.Ordinal))
                {
                    return false; // Uri.X(..) is a member call, handled on its own
                }

                before = before[..^"System".Length].TrimEnd();
            }

            return before.EndsWith("new", StringComparison.Ordinal) &&
                   (before.Length == 3 || !char.IsLetterOrDigit(before[^4]) && before[^4] != '_');
        });

    private static bool IsRelative(AppSourceFile source, Invocation call) =>
        call.Arguments.Count == 2 &&
        source.Masked.Substring(call.Arguments[1].Start, call.Arguments[1].Length).Trim() == "UriKind.Relative";
}
