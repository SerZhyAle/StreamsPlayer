using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0008: the desktop shortcut path. The catalog carries titles with slashes and colons, and titles
/// long enough that the shell refuses the path - which used to end the application instead of the action.
/// </summary>
public sealed class DesktopShortcutNameTests
{
    private const string Desktop = @"C:\Users\user\Desktop";

    [Fact]
    public void PathFor_PutsTheTitledShortcutInTheGivenDirectory() =>
        Assert.Equal(
            Path.Combine(Desktop, "BBC News - StreamsPlayer.lnk"),
            DesktopShortcutName.PathFor(Desktop, "BBC News"));

    [Theory]
    [InlineData("News/Sport", "News_Sport")]
    [InlineData("Radio: live", "Radio_ live")]
    [InlineData("Tabs\tand\nnewlines", "Tabs_and_newlines")]
    public void PathFor_ReplacesEveryCharacterAFileNameCannotHold(string title, string expected) =>
        Assert.Equal(
            Path.Combine(Desktop, expected + DesktopShortcutName.Suffix),
            DesktopShortcutName.PathFor(Desktop, title));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void PathFor_FallsBackWhenNothingUsableIsLeft(string? title) =>
        Assert.Equal(
            Path.Combine(Desktop, "Stream" + DesktopShortcutName.Suffix),
            DesktopShortcutName.PathFor(Desktop, title));

    [Theory]
    [InlineData("Channel.")]
    [InlineData("Channel ")]
    [InlineData("Channel .. ")]
    public void PathFor_NeverEndsTheTitleInASpaceOrDot(string title) =>
        Assert.Equal(
            Path.Combine(Desktop, "Channel" + DesktopShortcutName.Suffix),
            DesktopShortcutName.PathFor(Desktop, title));

    /// <summary>The crash of 2026-08-10: a 300-character catalog title against an ordinary desktop.</summary>
    [Fact]
    public void PathFor_KeepsALongTitleInsideTheShellsPathLimit()
    {
        var path = DesktopShortcutName.PathFor(Desktop, new string('x', 300));

        Assert.Equal(259, path.Length);
        Assert.EndsWith(DesktopShortcutName.Suffix, path, StringComparison.Ordinal);
    }

    /// <summary>A deeper desktop - OneDrive under a long organisation name - leaves the title less room.</summary>
    [Fact]
    public void PathFor_SpendsTheBudgetTheDirectoryLeaves()
    {
        var deep = Path.Combine(Desktop, new string('d', 60));

        var path = DesktopShortcutName.PathFor(deep, new string('x', 300));

        Assert.Equal(259, path.Length);
        Assert.StartsWith(deep, path, StringComparison.Ordinal);
    }

    /// <summary>
    /// A trailing separator on the directory is the same directory, so it must not cost the title a
    /// character or produce a doubled separator.
    /// </summary>
    [Fact]
    public void PathFor_TreatsATrailingSeparatorAsPartOfTheDirectory() =>
        Assert.Equal(
            DesktopShortcutName.PathFor(Desktop, new string('x', 300)),
            DesktopShortcutName.PathFor(Desktop + Path.DirectorySeparatorChar, new string('x', 300)));
}
