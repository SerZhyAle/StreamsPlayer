using System.Text.RegularExpressions;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0231, phase P1: the static half of the proof for the shared UI components. The application's markup and code are
/// read as data (the <see cref="AppSourceFile"/> pattern - Tests reference Core only), so each fact below is a claim about
/// the shipped sources and fails the moment a later edit breaks it. They are rung-1 evidence: they say the structure
/// that makes the contract's behaviour possible is present, and nothing about how the running window behaves - the
/// run-and-observe record does that.
/// </summary>
public sealed class UiFoundationGateTests
{
    private static readonly Regex TokenReference = new(
        @"\{(?<kind>Dynamic|Static)Resource\s+(?<key>Ui[A-Z]\w*)\s*\}", RegexOptions.CultureInvariant);

    private static readonly Regex DeclaredKey = new(@"x:Key\s*=\s*""(?<key>Ui[A-Z]\w*)""", RegexOptions.CultureInvariant);

    private static readonly Regex PublishedKey = new(@"""(?<key>Ui[A-Z]\w*)""", RegexOptions.CultureInvariant);

    // Resources that start with "Ui" and are not size tokens: the interface language's flow direction, defined by the
    // localization dictionaries and read everywhere.
    private static readonly HashSet<string> NotSizeTokens = new(StringComparer.Ordinal) { "UiFlowDirection" };

    private static string App(string name) =>
        AppSourceFile.LoadAll(name).Single().Text;

    [Fact]
    public void EveryUiTokenAComponentNamesIsDeclaredInAppXamlAndPublishedByTheService()
    {
        var declared = DeclaredKey.Matches(App("App.xaml")).Select(match => match.Groups["key"].Value).ToHashSet(StringComparer.Ordinal);
        var service = App("UiProfileService.cs");
        var published = ServiceKeys(service);

        var used = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        var staticReferences = new List<string>();
        foreach (var file in AppSourceFile.LoadAll("*.xaml"))
        {
            foreach (Match match in TokenReference.Matches(file.Text))
            {
                var key = match.Groups["key"].Value;
                if (NotSizeTokens.Contains(key))
                {
                    continue;
                }

                if (!used.TryGetValue(key, out var files))
                {
                    used[key] = files = [];
                }

                files.Add(file.Name);
                if (match.Groups["kind"].Value == "Static")
                {
                    staticReferences.Add($"{file.Name}: {key}");
                }
            }
        }

        // A token changes when the user picks another size, so a StaticResource would freeze it at first load.
        Assert.True(staticReferences.Count == 0, "Ui* tokens must be DynamicResource: " + string.Join(", ", staticReferences));
        Assert.Empty(used.Keys.Where(key => !declared.Contains(key)));
        Assert.Empty(declared.Except(published, StringComparer.Ordinal));
        Assert.Empty(published.Except(declared, StringComparer.Ordinal));
        Assert.NotEmpty(used);
    }

    private static HashSet<string> ServiceKeys(string service)
    {
        var start = service.IndexOf("PublishedKeys", StringComparison.Ordinal);
        Assert.True(start >= 0, "UiProfileService declares no PublishedKeys.");
        var end = service.IndexOf("];", start, StringComparison.Ordinal);
        var list = service[start..end];
        return PublishedKey.Matches(list).Select(match => match.Groups["key"].Value).ToHashSet(StringComparer.Ordinal);
    }

    [Fact]
    public void EveryResourceThePublishedKeysNameIsAlsoWrittenByPublish()
    {
        var service = App("UiProfileService.cs");
        var listed = ServiceKeys(service);
        var publish = service[service.IndexOf("internal static void Publish", StringComparison.Ordinal)..];
        var written = Regex.Matches(publish, @"resources\[""(?<key>Ui[A-Z]\w*)""\]\s*=").Select(match => match.Groups["key"].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Empty(listed.Except(written, StringComparer.Ordinal));
        Assert.Empty(written.Except(listed, StringComparer.Ordinal));
    }

    private static string Style(string xaml, string key)
    {
        var start = xaml.IndexOf($"<Style x:Key=\"{key}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"No style {key}.");
        var end = xaml.IndexOf("</Style>", start, StringComparison.Ordinal);
        return xaml[start..end];
    }

    [Fact]
    public void TheGroupHeaderReportsStateWithAMirroringVectorTriangleAndNotTheActionChevron()
    {
        var group = Style(App("App.xaml"), "SettingsGroup");

        // COLLAPSIBLE-GROUP 3 rule 2: a filled triangle, drawn as a vector, that reports the current state.
        Assert.Contains("Chrome.group-state", group);
        Assert.DoesNotContain("Icon.nav.expand", group);
        Assert.DoesNotContain("Icon.nav.collapse", group);
        Assert.Contains("RotateTransform", group);

        // It must mirror with the layout (right-pointing collapsed becomes left-pointing in RTL), so the action
        // glyphs' LeftToRight pin must not be on it.
        var marker = group[group.IndexOf("<Viewbox", StringComparison.Ordinal)..group.IndexOf("</Viewbox>", StringComparison.Ordinal)];
        Assert.DoesNotContain("FlowDirection=\"LeftToRight\"", marker);

        // Sizes come from the shared tokens, and the caption wraps instead of clipping.
        Assert.Contains("MinHeight=\"{DynamicResource UiTargetHeight}\"", group);
        Assert.Contains("{DynamicResource UiMarkerSize}", group);
        Assert.Contains("{DynamicResource UiMarkerMargin}", group);
        Assert.Contains("{DynamicResource UiGroupBodyMargin}", group);
        Assert.Contains("{DynamicResource UiSiblingGroupMargin}", group);
    }

    [Fact]
    public void TheWholeHeaderIsTheOnlyActivationTargetAndTheBodyInstallsNoHandler()
    {
        var group = Style(App("App.xaml"), "SettingsGroup");

        // The toggle is the header's ToggleButton, whose IsChecked is bound to IsExpanded; the body is a plain
        // presenter that is collapsed with the group and carries no mouse handler.
        Assert.Contains("IsChecked=\"{Binding IsExpanded, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}\"", group);

        // Headers expose their topic (BUTTONS 2 rule 5, COLLAPSIBLE-GROUP 4 rule 5): the toggle carries the caption as its name.
        Assert.Contains("AutomationProperties.Name=\"{Binding Header.Text, RelativeSource={RelativeSource TemplatedParent}}\"", group);
        var body = group[group.IndexOf("x:Name=\"ExpandSite\"", StringComparison.Ordinal)..];
        var bodyElement = body[..body.IndexOf("/>", StringComparison.Ordinal)];
        Assert.DoesNotContain("MouseLeftButton", bodyElement);
        Assert.DoesNotContain("PreviewMouse", bodyElement);
        Assert.Contains("Visibility=\"Collapsed\"", bodyElement);
    }

    [Fact]
    public void EveryGroupHeaderCaptionWrapsInsteadOfClipping()
    {
        var missing = new List<string>();
        foreach (var file in AppSourceFile.LoadAll("SettingsWindow.xaml"))
        {
            foreach (Match header in Regex.Matches(file.Text, @"<Expander\.Header>\s*(?<element><TextBlock\b[^>]*?/>)"))
            {
                if (!header.Groups["element"].Value.Contains("TextWrapping=\"Wrap\"", StringComparison.Ordinal))
                {
                    missing.Add(header.Groups["element"].Value);
                }
            }
        }

        Assert.True(missing.Count == 0, "Group header captions without TextWrapping: " + string.Join(" | ", missing));
    }

    [Fact]
    public void TheBooleanOptionRowTogglesFromTheBoxTheCaptionAndTheGlyphOnly()
    {
        var code = App("BooleanOptionRow.cs");

        // The only elements that receive a heading press are the glyph and the caption.
        var attachments = Regex.Matches(code, @"foreach \(var name in new\[\] \{(?<names>[^}]*)\}");
        Assert.Single(attachments);
        var names = Regex.Matches(attachments[0].Groups["names"].Value, "\"(?<n>PART_\\w+)\"").Select(m => m.Groups["n"].Value).ToArray();
        Assert.Equal(["PART_Glyph", "PART_Caption"], names);

        // One report per gesture: the box reports through its own Click, every heading activation through Toggle().
        Assert.Equal(2, Regex.Matches(code, @"RaiseToggled\(\)").Count(match => !IsDeclaration(code, match.Index)));

        // A press that began elsewhere, or a drag that a scroll viewer took over, never completes as a release here.
        Assert.Contains("CaptureMouse()", code);
        Assert.Contains("IsPointerInside", code);
    }

    private static bool IsDeclaration(string code, int index) =>
        code[Math.Max(0, index - 12)..index].Contains("void ", StringComparison.Ordinal);

    [Fact]
    public void TheBooleanOptionTemplateKeepsExplanationAndHelpOutsideTheCheckBox()
    {
        var xaml = App("App.xaml");
        var template = xaml[xaml.IndexOf("<Style TargetType=\"local:BooleanOptionRow\">", StringComparison.Ordinal)..];
        template = template[..template.IndexOf("</Style>", StringComparison.Ordinal)];

        // The box is a self-closing CheckBox with no content: nothing the user can press lives inside it but the box.
        var check = Regex.Match(template, @"<CheckBox\b[^>]*/>");
        Assert.True(check.Success, "PART_Check must be a self-closing CheckBox.");
        Assert.Contains("x:Name=\"PART_Check\"", check.Value);
        Assert.Contains("AutomationProperties.Name=\"{TemplateBinding Caption}\"", check.Value);
        Assert.Contains("AutomationProperties.HelpText=\"{TemplateBinding Explanation}\"", check.Value);

        // The explanation and the help are siblings of the activation region, never part of it.
        Assert.Contains("x:Name=\"PART_Explanation\"", template);
        Assert.Contains("x:Name=\"PART_Help\"", template);
        var box = xaml[xaml.IndexOf("x:Key=\"BooleanOptionBoxTemplate\"", StringComparison.Ordinal)..];
        box = box[..box.IndexOf("</ControlTemplate>", StringComparison.Ordinal)];
        Assert.DoesNotContain("ContentPresenter", box);
        Assert.DoesNotContain("TextBlock", box);

        // The box and hit target come from the shared tokens, and the colours from roles (no baked white or black).
        Assert.Contains("{DynamicResource UiCheckBoxBox}", box);
        Assert.Contains("{DynamicResource UiTargetHeight}", box);
        Assert.DoesNotContain("White", box);
        Assert.DoesNotContain("Black", box);
    }

    [Fact]
    public void TheSettingsWindowUsesTheOptionRowWhereItHasAdopted()
    {
        var settings = App("SettingsWindow.xaml");
        var row = Regex.Match(settings, @"<local:BooleanOptionRow\b[^>]*?/>", RegexOptions.Singleline);
        Assert.True(row.Success);

        // Toggled, not Click: the row reports a user's activation only, so populating the page needs no guard flag.
        Assert.Contains("Toggled=", row.Value);
        Assert.DoesNotContain("Click=", row.Value);
        Assert.DoesNotContain("AnimatedBackdropCheckBox", settings);
    }
}
