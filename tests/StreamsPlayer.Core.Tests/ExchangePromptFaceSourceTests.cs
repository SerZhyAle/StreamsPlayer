using System.Text.RegularExpressions;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0221 requirement 3, the seam the acceptance asks for: the face of the sign-in prompt shows no
/// address and no port, and its details show both. The face is composed in WPF code this project does
/// not load, so it is pinned over the App's own sources read as text on the <see cref="AppSourceFile"/>
/// terms (SP-0057): the four face lines are composed only of localized words and the string's own
/// display name, the endpoint may decide only which known/trust wording is shown, and the address and
/// port boxes live inside the collapsed details expander alone.
/// </summary>
public sealed class ExchangePromptFaceSourceTests
{
    private static AppSourceFile Source(string fileName) => Assert.Single(AppSourceFile.LoadAll(fileName));

    private static string XamlText(string fileName)
    {
        var path = Path.Combine(AppSourceFile.Directory, fileName);
        Assert.True(File.Exists(path), $"{fileName} is not among the copied App sources.");
        return File.ReadAllText(path);
    }

    [Theory]
    [InlineData("ExchangePreviewText")]
    [InlineData("ExchangeNameText")]
    [InlineData("ExchangeTrustText")]
    [InlineData("ExchangeInvitationText")]
    public void TheFaceLinesAreWordsAndTheDisplayName_NeverTheEndpoint(string line)
    {
        var file = Source("SettingsWindow.Exchange.cs");
        var (faceStart, faceEnd) = RangeOf(file, "private void ShowExchangePrompt()");
        var body = file.Masked[faceStart..faceEnd];

        var assignment = body.IndexOf($"{line}.Text =", StringComparison.Ordinal);
        Assert.True(assignment >= 0, $"The face line {line} is gone from ShowExchangePrompt.");
        var statement = body[assignment..(body.IndexOf(';', assignment) + 1)];

        Assert.DoesNotContain("Host", statement, StringComparison.Ordinal);
        Assert.DoesNotContain("Port", statement, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFaceCompositionNeverTouchesTheAddressOrThePortBoxes()
    {
        var file = Source("SettingsWindow.Exchange.cs");
        var (faceStart, faceEnd) = RangeOf(file, "private void ShowExchangePrompt()");
        var body = file.Masked[faceStart..faceEnd];

        Assert.DoesNotContain("ExchangeAddressBox", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ExchangePortBox", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAddressAndThePortLiveOnlyInsideTheDetailsExpander()
    {
        var xaml = XamlText("SettingsWindow.xaml");

        var details = RangeOfElement(xaml, "ExchangeDetailsExpander");
        var face = RangeOfElement(xaml, "ExchangePreview");
        var address = xaml.IndexOf("x:Name=\"ExchangeAddressBox\"", StringComparison.Ordinal);
        var port = xaml.IndexOf("x:Name=\"ExchangePortBox\"", StringComparison.Ordinal);
        Assert.True(address > 0 && port > 0, "The address or the port box is gone from the settings window.");

        Assert.True(details.Start < address && address < details.End, "The address box is not inside the details expander.");
        Assert.True(details.Start < port && port < details.End, "The port box is not inside the details expander.");
        Assert.True(face.End <= details.Start, "The face panel is not before the details expander.");
        var faceXaml = xaml[face.Start..face.End];
        Assert.DoesNotContain("ExchangeAddressBox", faceXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ExchangePortBox", faceXaml, StringComparison.Ordinal);
        // Collapsed by default: the details stay one control away until the person opens them.
        Assert.Matches(@"x:Name=""ExchangeDetailsExpander""[^>]*IsExpanded=""False""", xaml);
    }

    private static (int Start, int End) RangeOf(AppSourceFile file, string declaration)
    {
        var match = Regex.Match(file.Masked, Regex.Escape(declaration) + @"\s*\{");
        Assert.True(match.Success, $"{file.Name}: no declaration matches {declaration}");
        var depth = 0;
        for (var position = match.Index + match.Value.Length - 1; position < file.Masked.Length; position++)
        {
            depth += file.Masked[position] switch { '{' => 1, '}' => -1, _ => 0 };
            if (depth == 0)
            {
                return (match.Index, position + 1);
            }
        }

        throw new InvalidOperationException($"{file.Name}: unbalanced braces after {declaration}");
    }

    /// <summary>The span of the element named <paramref name="name"/>, from its opening tag to its end tag.</summary>
    private static (int Start, int End) RangeOfElement(string xaml, string name)
    {
        var open = xaml.IndexOf($"x:Name=\"{name}\"", StringComparison.Ordinal);
        Assert.True(open > 0, $"{name} is not in the XAML.");
        var tagStart = xaml.LastIndexOf('<', open);
        var depth = 0;
        var position = tagStart;
        while (position < xaml.Length)
        {
            var element = xaml.IndexOf('<', position);
            if (element < 0)
            {
                break;
            }

            var tagEnd = xaml.IndexOf('>', element);
            if (xaml.AsSpan(element).StartsWith("</"))
            {
                depth--;
                position = tagEnd + 1;
                if (depth == 0)
                {
                    return (tagStart, position);
                }
            }
            else if (xaml[element + 1] is not ('!' or '?') && !xaml[element..tagEnd].EndsWith("/"))
            {
                depth++;
                position = tagEnd + 1;
            }
            else
            {
                position = tagEnd + 1;
            }
        }

        throw new InvalidOperationException($"{name}: no closing tag found.");
    }
}
