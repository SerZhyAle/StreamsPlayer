using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0040 criterion 4. The prepared message cannot be asserted through the shell, and the failure mode
/// is silent - a client drops everything after an unescaped separator and the user mails an empty note -
/// so the link itself is the thing under test.
/// </summary>
public sealed class DiagnosticMailLinkTests
{
    [Fact]
    public void Build_PutsRecipientSubjectAndBodyInTheLink()
    {
        var link = DiagnosticMailLink.Build("author@example.invalid", "Logs - version 26.0730.0012", "Hello");

        Assert.StartsWith("mailto:author@example.invalid?subject=", link, StringComparison.Ordinal);
        Assert.Contains("Logs%20-%20version%2026.0730.0012", link, StringComparison.Ordinal);
        Assert.EndsWith("&body=Hello", link, StringComparison.Ordinal);
    }

    // The body is localized prose with real line breaks; a raw one ends the parameter at that point.
    [Fact]
    public void Build_EscapesLineBreaksAndSeparatorsInTheBody()
    {
        var link = DiagnosticMailLink.Build("a@b.invalid", "s", "Hello,\r\n\r\nattach StreamsPlayer-logs.zip & press Send #now");

        Assert.DoesNotContain("\r", link, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", link, StringComparison.Ordinal);
        Assert.Contains("%0D%0A", link, StringComparison.Ordinal);
        Assert.Contains("%26", link, StringComparison.Ordinal);   // the ampersand, not a second parameter
        Assert.Contains("%23", link, StringComparison.Ordinal);   // the hash, not a fragment
        Assert.Equal(2, link.Split('&').Length);                  // exactly one parameter separator survives
    }

    [Fact]
    public void Build_EscapesNonLatinSubjects()
    {
        var link = DiagnosticMailLink.Build("a@b.invalid", "Журналы STREAMS Player - версия 26.0730.0012", "текст");

        Assert.DoesNotContain("Журналы", link, StringComparison.Ordinal);
        Assert.Contains("STREAMS%20Player", link, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_CapsAnOverlongBody()
    {
        var body = new string('x', DiagnosticMailLink.MaxBodyCharacters + 500);

        var link = DiagnosticMailLink.Build("a@b.invalid", "s", body);

        Assert.EndsWith($"&body={new string('x', DiagnosticMailLink.MaxBodyCharacters)}", link, StringComparison.Ordinal);
    }
}
