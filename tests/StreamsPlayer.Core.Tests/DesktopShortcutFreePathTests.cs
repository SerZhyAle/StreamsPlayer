using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>SP-0127: a second channel with the same title never takes over the first one's shortcut.</summary>
public sealed class DesktopShortcutFreePathTests
{
    private const string Desktop = @"C:\Users\user\Desktop";

    [Fact]
    public void FreePathFor_UsesThePlainNameWhileItIsFree() =>
        Assert.Equal(
            DesktopShortcutName.PathFor(Desktop, "BBC News"),
            DesktopShortcutName.FreePathFor(Desktop, "BBC News", _ => false));

    [Fact]
    public void FreePathFor_SuffixesANameAnotherChannelHolds()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            DesktopShortcutName.PathFor(Desktop, "BBC News"),
            Path.Combine(Desktop, "BBC News (2)" + DesktopShortcutName.Suffix)
        };

        Assert.Equal(
            Path.Combine(Desktop, "BBC News (3)" + DesktopShortcutName.Suffix),
            DesktopShortcutName.FreePathFor(Desktop, "BBC News", taken.Contains));
    }

    [Fact]
    public void FreePathFor_KeepsASuffixedLongTitleInsideThePathLimit()
    {
        var plain = DesktopShortcutName.PathFor(Desktop, new string('x', 300));

        var path = DesktopShortcutName.FreePathFor(Desktop, new string('x', 300), candidate => candidate == plain);

        Assert.NotNull(path);
        Assert.Equal(259, path.Length);
        Assert.EndsWith(" (2)" + DesktopShortcutName.Suffix, path, StringComparison.Ordinal);
    }

    [Fact]
    public void FreePathFor_GivesUpRatherThanOverwritingWhenEveryNameIsTaken() =>
        Assert.Null(DesktopShortcutName.FreePathFor(Desktop, "BBC News", _ => true));
}
