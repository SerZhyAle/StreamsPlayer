using System.Text.Json;
using System.Text.RegularExpressions;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0230, CONTEXT-HELP rule 19: the gate that counts surfaces. The windows are enumerated from the application's
/// own sources, the map and the copy and the baseline are read from the repository's <c>help/</c> folder (linked in
/// as data, never run), and the three are held against one another. The generated pages are judged by
/// <c>scripts/check-help.ps1</c>, which reads them as files.
/// </summary>
public sealed class HelpGateTests
{
    private static readonly string[] BaselineKinds = ["button", "locale", "page", "shot"];

    private static string HelpDirectory => Path.Combine(AppContext.BaseDirectory, "help");

    // One instance: HelpSurface is a record whose page list compares by reference, so rows from two loads are never equal.
    private static HelpMap Map { get; } = HelpMap.Load();

    private static IReadOnlyList<string> BaselineEntries() =>
        [.. File.ReadAllLines(Path.Combine(HelpDirectory, "help-baseline.txt"))
            .Where(line => line.Trim().Length > 0 && !line.TrimStart().StartsWith('#'))];

    private static IReadOnlySet<string> Baselined(string kind) =>
        BaselineEntries().Where(line => line.StartsWith(kind + " ", StringComparison.Ordinal))
            .Select(line => line[(kind.Length + 1)..]).ToHashSet(StringComparer.Ordinal);

    private static IReadOnlyList<string> AuthoredLocales() =>
        [.. Directory.GetDirectories(Path.Combine(HelpDirectory, "content")).Select(path => Path.GetFileName(path)!).OrderBy(code => code, StringComparer.Ordinal)];

    private static bool HasPage(HelpSurface surface) => surface.Form is HelpForm.Page or HelpForm.Both;

    /// <summary>The surfaces whose page is written: a page form and not on the page baseline.</summary>
    private static IReadOnlyList<HelpSurface> WrittenPages()
    {
        var unwritten = Baselined("page");
        return [.. Map.Surfaces.Where(surface => HasPage(surface) && !unwritten.Contains(surface.Help!))];
    }

    private static JsonDocument Copy(string locale, string help) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(HelpDirectory, "content", locale, help + ".json")));

    /// <summary>Every entry of a page's copy, in page order, whether the page is flat or in groups.</summary>
    private static IReadOnlyList<JsonElement> Entries(JsonElement copy) =>
        copy.TryGetProperty("groups", out var groups)
            ? [.. groups.EnumerateArray().SelectMany(group => group.GetProperty("elements").EnumerateArray())]
            : [.. copy.GetProperty("elements").EnumerateArray()];

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Xaml(string className) =>
        File.ReadAllText(Path.Combine(AppSourceFile.Directory, className + ".xaml"));

    [Fact]
    public void The_map_has_one_row_for_every_window_class_of_the_application_and_no_other()
    {
        var classes = AppSourceFile.LoadAll("*.cs")
            .SelectMany(file => Regex.Matches(file.Masked, @"\bclass\s+(\w+)\s*:\s*Window\b").Select(match => match.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var mapped = Map.Surfaces.Select(surface => surface.ClassName ?? string.Empty).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(classes, mapped);
        Assert.Equal(mapped.Length, mapped.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void The_shipped_map_is_sound()
    {
        Assert.Empty(Map.Problems());
    }

    [Fact]
    public void Every_name_key_the_help_cites_exists_in_every_dictionary()
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var surface in Map.Surfaces)
        {
            keys.UnionWith(new[] { surface.NameKey }.Concat(surface.Pages.Select(page => page.NameKey)).OfType<string>());
        }

        foreach (var locale in AuthoredLocales())
        {
            foreach (var surface in WrittenPages())
            {
                using var copy = Copy(locale, surface.Help!);
                keys.UnionWith(Entries(copy.RootElement).Select(entry => Text(entry, "nameKey")).OfType<string>());
                if (copy.RootElement.TryGetProperty("groups", out var groups))
                {
                    keys.UnionWith(groups.EnumerateArray().Select(group => Text(group, "nameKey")).OfType<string>());
                }
            }
        }

        var missing = from dictionary in LocalizationDictionary.LoadAll()
                      from key in keys
                      where !dictionary.Values.ContainsKey(key)
                      select $"{dictionary.Code}: {key}";

        Assert.Empty(missing);
    }

    [Fact]
    public void The_settings_pages_of_the_map_follow_the_navigation_order_of_the_window()
    {
        var source = AppSourceFile.LoadAll("SettingsWindow.xaml.cs").Single();
        var declaration = Regex.Match(source.Text, @"PageNameKeys\s*=\s*\[(?<keys>[^\]]*)\]");
        Assert.True(declaration.Success, "SettingsWindow.xaml.cs no longer declares PageNameKeys as a collection expression.");
        var navigation = Regex.Matches(declaration.Groups["keys"].Value, "\"([^\"]+)\"").Select(match => match.Groups[1].Value).ToArray();

        var pages = Map.Find("settings")!.Pages.Select(page => page.NameKey).ToArray();

        Assert.Equal(navigation, pages);
    }

    [Fact]
    public void The_baseline_is_sorted_names_only_known_kinds_and_every_debt_it_names_is_real()
    {
        var entries = BaselineEntries();
        Assert.Equal(entries.Order(StringComparer.Ordinal), entries);
        Assert.All(entries, line => Assert.Contains(line.Split(' ')[0], BaselineKinds));

        var pageIds = Map.Surfaces.Where(HasPage).Select(surface => surface.Help!).ToHashSet(StringComparer.Ordinal);
        foreach (var id in Baselined("page"))
        {
            Assert.Contains(id, pageIds);
            Assert.False(
                File.Exists(Path.Combine(HelpDirectory, "content", Map.SourceLocale, id + ".json")),
                $"page {id} is on the baseline but its copy exists - delete the baseline line.");
        }

        var surfaceIds = Map.Surfaces.Select(surface => surface.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(Baselined("button"), id => Assert.Contains(id, surfaceIds));
    }

    [Fact]
    public void Every_written_page_has_its_copy_in_every_authored_locale_and_the_other_locales_are_the_baseline()
    {
        var authored = AuthoredLocales();
        Assert.Contains(Map.SourceLocale, authored);
        foreach (var surface in WrittenPages())
        {
            foreach (var locale in authored)
            {
                Assert.True(
                    File.Exists(Path.Combine(HelpDirectory, "content", locale, surface.Help + ".json")),
                    $"{locale} has no copy for the page '{surface.Help}'.");
            }
        }

        var shipped = InterfaceLanguages.All.Select(language => language.DictionaryCode).Order(StringComparer.Ordinal).ToArray();
        var owed = shipped.Except(authored).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(owed, Baselined("locale").Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_written_page_without_a_generated_picture_is_on_the_picture_baseline_and_nothing_else_is()
    {
        var written = WrittenPages().Select(surface => surface.Help!).Order(StringComparer.Ordinal);

        Assert.Equal(written, Baselined("shot").Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_authored_locales_say_the_same_thing_in_the_same_structure()
    {
        var authored = AuthoredLocales();
        foreach (var surface in WrittenPages())
        {
            string Shape(string locale)
            {
                using var copy = Copy(locale, surface.Help!);
                var root = copy.RootElement;
                var groups = root.TryGetProperty("groups", out var list)
                    ? string.Join(",", list.EnumerateArray().Select(group => $"{Text(group, "id")}={Text(group, "nameKey")}"))
                    : "-";
                var entries = string.Join(";", Entries(root).Select(entry =>
                    $"{Text(entry, "id")}|{Text(entry, "nameKey")}|{Text(entry, "control")}|" +
                    string.Concat(new[] { "why", "next", "undo", "shortcut", "tip" }.Select(fact => entry.TryGetProperty(fact, out _) ? fact[0] : '.'))));
                var steps = root.TryGetProperty("order", out var order) ? order.GetArrayLength() : -1;
                return $"groups[{groups}] order[{steps}] {entries}";
            }

            var source = Shape(Map.SourceLocale);
            foreach (var locale in authored.Where(code => code != Map.SourceLocale))
            {
                Assert.True(source == Shape(locale), $"'{surface.Help}' differs between {Map.SourceLocale} and {locale} in ids, name keys, controls or fact lines.");
            }
        }
    }

    [Fact]
    public void An_order_has_at_most_seven_steps_and_every_entry_has_a_one_liner_and_a_full_text()
    {
        foreach (var locale in AuthoredLocales())
        {
            foreach (var surface in WrittenPages())
            {
                using var copy = Copy(locale, surface.Help!);
                if (copy.RootElement.TryGetProperty("order", out var order))
                {
                    Assert.True(order.GetArrayLength() <= 7, $"{locale}/{surface.Help} lists more than seven steps.");
                }

                foreach (var entry in Entries(copy.RootElement))
                {
                    Assert.False(string.IsNullOrWhiteSpace(Text(entry, "one")), $"{locale}/{surface.Help} entry '{Text(entry, "id")}' has no one-liner.");
                    Assert.True(entry.TryGetProperty("full", out var full) && full.GetArrayLength() > 0, $"{locale}/{surface.Help} entry '{Text(entry, "id")}' has no full text.");
                }
            }
        }
    }

    [Fact]
    public void An_entry_that_names_a_control_is_anchored_on_it_and_every_anchor_names_an_entry()
    {
        var anchored = new List<string>();
        foreach (var surface in WrittenPages().Where(surface => surface.ClassName is not null))
        {
            using var copy = Copy(Map.SourceLocale, surface.Help!);
            var xaml = Xaml(surface.ClassName!);
            var entryIds = Entries(copy.RootElement).Select(entry => Text(entry, "id")!).ToHashSet(StringComparer.Ordinal);

            foreach (var entry in Entries(copy.RootElement).Where(entry => Text(entry, "control") is not null))
            {
                var control = Text(entry, "control")!;
                // The element name may carry a namespace prefix: a product control such as local:BooleanOptionRow is
                // an editor too (SP-0231).
                var tag = Regex.Match(xaml, @"<[A-Za-z][\w.:]*\s[^<>]*?x:Name=""" + Regex.Escape(control) + @"""[^<]*?>", RegexOptions.Singleline);
                Assert.True(tag.Success, $"{surface.ClassName}.xaml has no element named '{control}' (entry '{Text(entry, "id")}').");
                Assert.True(
                    tag.Value.Contains($"HelpAnchor.Id=\"e-{Text(entry, "id")}\"", StringComparison.Ordinal),
                    $"{surface.ClassName}.xaml: '{control}' lacks local:HelpAnchor.Id=\"e-{Text(entry, "id")}\".");
            }

            foreach (Match anchor in Regex.Matches(xaml, @"HelpAnchor\.Id=""([^""]*)"""))
            {
                var value = anchor.Groups[1].Value;
                Assert.StartsWith("e-", value, StringComparison.Ordinal);
                Assert.Contains(value[2..], entryIds);
                anchored.Add($"{surface.ClassName}:{value}");
            }
        }

        // An anchor in a window whose page is not written would name an entry nobody has written.
        var unwritten = Map.Surfaces.Where(surface => surface.ClassName is not null && !WrittenPages().Contains(surface))
            .Where(surface => Regex.IsMatch(Xaml(surface.ClassName!), @"HelpAnchor\.Id=")).Select(surface => surface.ClassName);
        Assert.Empty(unwritten);
    }

    [Fact]
    public void A_window_carries_the_help_button_exactly_when_it_has_help_and_is_not_on_the_button_baseline()
    {
        var owed = Baselined("button");
        foreach (var surface in Map.Surfaces.Where(surface => surface.ClassName is not null))
        {
            var buttons = Regex.Matches(Xaml(surface.ClassName!), @"HelpGlyphButton").Count;
            if (surface.Form == HelpForm.None)
            {
                Assert.True(buttons == 0, $"{surface.ClassName} has no help and must not carry the help button.");
                Assert.DoesNotContain(surface.Id, owed);
                continue;
            }

            if (owed.Contains(surface.Id))
            {
                Assert.True(buttons == 0, $"{surface.ClassName} carries the help button, so '{surface.Id}' must leave the button baseline.");
            }
            else
            {
                Assert.True(buttons == 1, $"{surface.ClassName} must carry exactly one help button; it carries {buttons}.");
                var xaml = Xaml(surface.ClassName!);
                Assert.Contains("{DynamicResource HelpTip}", xaml);
                Assert.Contains("{DynamicResource HelpName}", xaml);
            }
        }
    }

    [Fact]
    public void F1_is_the_help_commands_gesture_registered_once_for_windows_and_never_system_wide()
    {
        var sources = AppSourceFile.LoadAll("*.cs");

        Assert.DoesNotContain(sources, file => file.Masked.Contains("RegisterHotKey", StringComparison.Ordinal));

        var service = sources.Single(file => file.Name == "HelpService.cs");
        Assert.Single(Regex.Matches(service.Masked, @"RegisterClassCommandBinding\s*\("));
        Assert.Contains("typeof(Window)", service.Masked);
        Assert.Contains("ApplicationCommands.Help", service.Masked);
        Assert.Single(sources.Single(file => file.Name == "App.xaml.cs").Invocations("HelpService.Register"));

        var style = Regex.Match(File.ReadAllText(Path.Combine(AppSourceFile.Directory, "App.xaml")),
            @"<Style x:Key=""HelpGlyphButton"".*?</Style>", RegexOptions.Singleline);
        Assert.True(style.Success);
        Assert.Contains("ApplicationCommands.Help", style.Value);
    }
}
