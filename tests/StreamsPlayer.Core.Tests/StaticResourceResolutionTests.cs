using System.Text.RegularExpressions;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0222, found by running the broadcast list against a real profile: three windows asked for the style
/// <c>SettingsLinkButton</c> by <c>StaticResource</c> while only the settings window defined it, so each of them
/// threw a <c>XamlParseException</c> the moment it was constructed - the list and both password prompts never
/// opened, and no build, no test and no review had said so. A <c>StaticResource</c> is resolved when the window
/// loads, against that window's own resources and then the application's; this reads that rule statically for
/// every window, so the next style that lives in the wrong place fails here instead of in front of a person.
/// </summary>
public sealed class StaticResourceResolutionTests
{
    private static readonly Regex Reference = new(@"\{StaticResource\s+(?<key>[A-Za-z_][\w.]*)\s*\}", RegexOptions.CultureInvariant);
    private static readonly Regex DictionaryRoot = new(@"\A\s*(<\?xml[^>]*\?>\s*)?(<!--.*?-->\s*)*<ResourceDictionary\b",
        RegexOptions.CultureInvariant | RegexOptions.Singleline);
    private static readonly Regex Definition =new(@"x:Key\s*=\s*""(?<key>[^""]+)""", RegexOptions.CultureInvariant);

    [Fact]
    public void EveryStaticResourceAWindowAsksForIsDefinedInItsOwnResourcesOrTheApplications()
    {
        var files = AppSourceFile.LoadAll("*.xaml");
        // The application's own resources, and every dictionary it merges (the icon glyphs, the strings): a file
        // whose root is a ResourceDictionary is not a window and is visible to all of them.
        var application = files
            .Where(file => file.Name == "App.xaml" || DictionaryRoot.IsMatch(file.Text))
            .SelectMany(file => Definition.Matches(file.Text).Select(match => match.Groups["key"].Value))
            .ToHashSet(StringComparer.Ordinal);
        var unresolved = new List<string>();
        foreach (var file in files)
        {
            if (file.Name == "App.xaml" || DictionaryRoot.IsMatch(file.Text))
            {
                continue;
            }

            var own = Definition.Matches(file.Text).Select(match => match.Groups["key"].Value).ToHashSet(StringComparer.Ordinal);
            foreach (var key in Reference.Matches(file.Text).Select(match => match.Groups["key"].Value).Distinct(StringComparer.Ordinal))
            {
                if (!own.Contains(key) && !application.Contains(key))
                {
                    unresolved.Add($"{file.Name}: {key}");
                }
            }
        }

        Assert.True(unresolved.Count == 0, "StaticResource keys no window or application dictionary defines: " + string.Join(", ", unresolved));
    }
}
