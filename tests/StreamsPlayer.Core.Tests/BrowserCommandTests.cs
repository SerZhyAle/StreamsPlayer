using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>SP-0230: the registered browser command, read the way Chrome, Edge and Firefox write it.</summary>
public sealed class BrowserCommandTests
{
    private const string Page = "file:///C:/Program%20Files/StreamsPlayer/help/en/main.html?theme=dark#e-search";

    [Fact]
    public void Chrome_and_Edge_take_the_address_as_one_quoted_single_argument()
    {
        var command = BrowserCommand.Build("\"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe\" --single-argument %1", Page);

        Assert.Equal(("C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe", $"--single-argument \"{Page}\""), command);
    }

    [Fact]
    public void A_placeholder_the_template_already_quoted_keeps_the_templates_quotes()
    {
        var command = BrowserCommand.Build("\"C:\\Program Files\\Mozilla Firefox\\firefox.exe\" -osint -url \"%1\"", Page);

        Assert.Equal(("C:\\Program Files\\Mozilla Firefox\\firefox.exe", $"-osint -url \"{Page}\""), command);
    }

    [Fact]
    public void An_unquoted_program_and_the_percent_L_form_are_read()
    {
        var command = BrowserCommand.Build("C:\\Browsers\\fast.exe %L", Page);

        Assert.Equal(("C:\\Browsers\\fast.exe", $"\"{Page}\""), command);
    }

    [Fact]
    public void A_template_without_a_placeholder_gets_the_address_appended()
    {
        Assert.Equal(("C:\\b\\x.exe", $"--app \"{Page}\""), BrowserCommand.Build("\"C:\\b\\x.exe\" --app", Page));
        Assert.Equal(("C:\\b\\x.exe", $"\"{Page}\""), BrowserCommand.Build("\"C:\\b\\x.exe\"", Page));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"C:\\unterminated.exe --x %1")]
    [InlineData("\"C:\\no-extension\" %1")]
    [InlineData("C:\\script.cmd %1")]
    public void A_template_that_names_no_executable_is_refused(string? template)
    {
        Assert.Null(BrowserCommand.Build(template, Page));
    }

    [Fact]
    public void An_address_that_could_break_out_of_its_quotes_is_refused()
    {
        Assert.Null(BrowserCommand.Build("\"C:\\b\\x.exe\" %1", "file:///x.html\" --evil \""));
        Assert.Null(BrowserCommand.Build("\"C:\\b\\x.exe\" %1", ""));
    }
}
