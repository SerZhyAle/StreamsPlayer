using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class ChannelMonogramTests
{
    [Theory]
    [InlineData("Nature Radio Rain", "NR")]
    [InlineData("Ambient Sleeping Pill", "AS")]
    [InlineData("Cryosleep", "C")]
    [InlineData("24/7 Nature Radio", "24")]
    [InlineData("# 100 GREATEST HEAVY METAL", "10")]
    [InlineData("7 Rays Radio", "7R")]
    [InlineData("0 N - Chillout on Radio (AAC+)", "0N")]
    [InlineData("радио Люкс", "РЛ")]
    public void Text_ReadsTheTitleTheWayTheBankSpellsIt(string title, string expected) =>
        Assert.Equal(expected, ChannelMonogram.Text(title));

    // The bank is not curated: a title can be blank, whitespace, or punctuation only. Every one of those
    // must still produce a mark, because the whole point of SP-0087 is that no channel renders as a hole.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("---")]
    [InlineData("...")]
    public void Text_IsNeverEmpty(string? title) =>
        Assert.NotEmpty(ChannelMonogram.Text(title));

    [Fact]
    public void Text_IgnoresSurroundingWhitespace() =>
        Assert.Equal(ChannelMonogram.Text("Nature Radio"), ChannelMonogram.Text("  Nature Radio  "));

    // The assertion that matters: a hard-coded expected index. string.GetHashCode is randomized per
    // process, so an implementation that used it would pass a "twice in a row" check and still repaint
    // the catalog on every launch. Only a pinned value catches that.
    [Fact]
    public void PaletteIndex_IsPinnedRatherThanFrameworkHashed() =>
        Assert.Equal(5, ChannelMonogram.PaletteIndex("Nature Radio Rain", 12));

    [Theory]
    [InlineData("Nature Radio Rain")]
    [InlineData("24/7 Nature Radio")]
    [InlineData("")]
    [InlineData(null)]
    public void PaletteIndex_StaysInRange(string? title)
    {
        var index = ChannelMonogram.PaletteIndex(title, 12);
        Assert.InRange(index, 0, 11);
    }

    [Fact]
    public void PaletteIndex_SpreadsAcrossThePalette()
    {
        var buckets = Enumerable.Range(0, 100)
            .Select(number => ChannelMonogram.PaletteIndex($"Station number {number}", 12))
            .ToHashSet();

        // A hash that collapsed onto a couple of buckets would leave the scrolled list looking
        // two-coloured, which is the failure the spec's "соседние строки должны быть различимы" names.
        Assert.True(buckets.Count >= 6, $"only {buckets.Count} of 12 palette entries were reached");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void PaletteIndex_RefusesAnEmptyPalette(int paletteSize) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ChannelMonogram.PaletteIndex("Any", paletteSize));
}
