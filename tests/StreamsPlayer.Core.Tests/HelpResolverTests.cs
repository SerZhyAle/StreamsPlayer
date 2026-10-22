using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>SP-0230: the resolver and the page locator of CONTEXT-HELP, on a small map of their own.</summary>
public sealed class HelpResolverTests
{
    private const string SampleMap = """
        {
          "schemaVersion": 1, "product": "Sample", "index": "index", "sourceLocale": "en", "unknownMember": 7,
          "surfaces": [
            { "id": "main", "name": "Main", "form": "page", "help": "main", "class": "MainWindow" },
            { "id": "settings", "name": "Settings", "form": "page", "help": "settings", "anchor": "general", "class": "SettingsWindow" },
            { "id": "rename", "name": "Rename", "form": "inherit", "parent": "main", "reason": "one field", "class": "RenameWindow" },
            { "id": "secret", "name": "Secret", "form": "inherit", "parent": "settings", "anchor": "library", "reason": "a section", "class": "SecretWindow" },
            { "id": "deep", "name": "Deep", "form": "inherit", "parent": "rename", "reason": "two hops", "class": "DeepWindow" },
            { "id": "confirm", "name": "Confirm", "form": "none", "reason": "its text is the explanation", "class": "ConfirmWindow" }
          ]
        }
        """;

    private static readonly HelpMap Map = HelpMap.Parse(SampleMap);

    [Fact]
    public void A_page_surface_resolves_to_its_own_help_id()
    {
        var target = HelpResolver.Resolve(Map, "main");

        Assert.Equal(new HelpTarget("main", "main", null), target);
    }

    [Fact]
    public void An_element_anchor_wins_over_the_rows_own_anchor()
    {
        Assert.Equal("general", HelpResolver.Resolve(Map, "settings")!.Anchor);
        Assert.Equal("e-search", HelpResolver.Resolve(Map, "settings", "e-search")!.Anchor);
    }

    [Fact]
    public void An_inheriting_surface_opens_its_parents_page_at_its_own_anchor()
    {
        var target = HelpResolver.Resolve(Map, "secret");

        Assert.Equal("settings", target!.HelpId);
        Assert.Equal("library", target.Anchor);
    }

    [Fact]
    public void An_inherit_chain_is_followed_to_the_end()
    {
        Assert.Equal("main", HelpResolver.Resolve(Map, "deep")!.HelpId);
    }

    [Fact]
    public void A_none_surface_and_an_unknown_surface_have_no_help()
    {
        Assert.Null(HelpResolver.Resolve(Map, "confirm"));
        Assert.Null(HelpResolver.Resolve(Map, "not-a-surface"));
    }

    [Fact]
    public void An_inherit_cycle_gives_up_instead_of_looping()
    {
        var cycle = HelpMap.Parse("""
            { "schemaVersion": 1, "surfaces": [
              { "id": "a", "name": "A", "form": "inherit", "parent": "b", "reason": "x" },
              { "id": "b", "name": "B", "form": "inherit", "parent": "a", "reason": "x" } ] }
            """);

        Assert.Null(HelpResolver.Resolve(cycle, "a"));
        Assert.Contains(cycle.Problems(), problem => problem.Contains("ends in no help"));
    }

    [Fact]
    public void The_sample_map_is_sound()
    {
        Assert.Empty(Map.Problems());
    }

    [Fact]
    public void The_reader_names_a_row_it_cannot_place_and_a_form_it_does_not_know()
    {
        var broken = HelpMap.Parse("""
            { "schemaVersion": 2, "surfaces": [
              { "id": "a", "name": "A", "form": "page" },
              { "id": "a", "name": "A again", "form": "page", "help": "a" },
              { "id": "b", "name": "B", "form": "inherit", "parent": "nowhere" },
              { "id": "c", "name": "C", "form": "none" },
              { "id": "d", "name": "D", "form": "carousel", "reason": "x" } ] }
            """);

        var problems = string.Join("\n", broken.Problems());

        Assert.Contains("schemaVersion is 2", problems);
        Assert.Contains("'a' has the form Page and no help id", problems);
        Assert.Contains("'a' appears twice", problems);
        Assert.Contains("'b' is inherit with no reason", problems);
        Assert.Contains("'b' inherits from 'nowhere'", problems);
        Assert.Contains("'c' is none with no reason", problems);
        Assert.Contains("'d' has the form 'carousel'", problems);
    }

    [Fact]
    public void The_locator_prefers_the_users_locale_then_the_source_locale()
    {
        var target = HelpResolver.Resolve(Map, "settings", "e-search");
        var files = new HashSet<string> { "ru/settings.html", "en/settings.html", "en/index.html" };

        Assert.Equal("ru/settings.html#e-search", HelpPageLocator.Locate(Map, target, "ru", files.Contains));
        Assert.Equal("en/settings.html#e-search", HelpPageLocator.Locate(Map, target, "de", files.Contains));
        Assert.Equal("en/settings.html#e-search", HelpPageLocator.Locate(Map, target, "EN", files.Contains));
    }

    [Fact]
    public void The_locator_falls_back_to_the_index_and_drops_the_anchor_there()
    {
        var target = HelpResolver.Resolve(Map, "settings", "e-search");
        var files = new HashSet<string> { "ru/index.html", "en/index.html" };

        Assert.Equal("ru/index.html", HelpPageLocator.Locate(Map, target, "ru", files.Contains));
        Assert.Equal("en/index.html", HelpPageLocator.Locate(Map, target, "ar", files.Contains));
    }

    [Fact]
    public void The_locator_answers_the_index_for_a_surface_without_a_target_and_null_for_an_empty_folder()
    {
        var files = new HashSet<string> { "en/index.html" };

        Assert.Equal("en/index.html", HelpPageLocator.Locate(Map, null, "en", files.Contains));
        Assert.Null(HelpPageLocator.Locate(Map, null, "en", _ => false));
    }

    [Fact]
    public void The_file_address_keeps_the_anchor_and_the_query_and_encodes_each_segment()
    {
        Assert.Equal(
            "file:///C:/Program%20Files/Streams%20Player/help/ru/main.html?theme=dark#e-search",
            HelpPageLocator.FileAddress(@"C:\Program Files\Streams Player\help", "ru/main.html#e-search", "theme=dark"));
        Assert.Equal(
            "file:///D:/%D0%A2%D1%80/help/en/index.html",
            HelpPageLocator.FileAddress("D:/Тр/help/", "en/index.html", null));
    }

    [Fact]
    public void The_file_address_of_a_network_share_names_the_server_unencoded()
    {
        Assert.Equal(
            "file://nas/apps/Streams%20Player/help/en/settings.html#library",
            HelpPageLocator.FileAddress(@"\\nas\apps\Streams Player\help", "en/settings.html#library", ""));
    }

    [Fact]
    public void The_shipped_map_loads_and_is_sound()
    {
        var shipped = HelpMap.Load();

        Assert.Equal("STREAMS Player", shipped.Product);
        Assert.Empty(shipped.Problems());
        Assert.NotNull(HelpResolver.Resolve(shipped, "main"));
        Assert.NotNull(HelpResolver.Resolve(shipped, "import-url"));
        Assert.Null(HelpResolver.Resolve(shipped, "broadcast-password"));
    }
}
