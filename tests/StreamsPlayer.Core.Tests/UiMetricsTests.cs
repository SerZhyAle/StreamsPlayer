using System.Text.Json;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0231, <c>UI-SIZING</c> sections 2 and 4: the measurement half of the declared target floors. The
/// resolver is the single source of the component tokens, so these facts are the claims the contract asks a
/// product to publish and prove - ordinary baselines, the smallest supported targets, no zero-size result for
/// an unknown scale, and text enlargement kept apart from interface scale.
/// </summary>
public sealed class UiMetricsTests
{
    [Fact]
    public void TheOrdinaryProfilesCarryTheContractBaselines()
    {
        var pointer = UiMetricsResolver.Resolve(UiProfile.Default);
        var touch = UiMetricsResolver.Resolve(new UiProfile(UiInputProfile.Touch, UiInterfaceSize.Standard));

        // BUTTONS section 3 table; BOOLEAN-OPTION section 2 rule 4; COLLAPSIBLE-GROUP section 3 rule 6;
        // SETTINGS-NAVIGATION section 2 rule 5.
        Assert.Equal(32, pointer.TargetHeight);
        Assert.Equal(48, touch.TargetHeight);
        Assert.Equal(12, pointer.ButtonPaddingX);
        Assert.Equal(16, touch.ButtonPaddingX);
        Assert.Equal(16, pointer.GlyphSize);
        Assert.Equal(20, touch.GlyphSize);
        Assert.Equal(8, pointer.GlyphGap);
        Assert.Equal(4, pointer.CornerRadius);
        Assert.Equal(1, pointer.BorderWidth);
        Assert.Equal(8, pointer.ButtonGap);
        Assert.Equal(16, pointer.SemanticGroupGap);
        Assert.Equal(20, pointer.CheckBoxBox);
        Assert.Equal(24, touch.CheckBoxBox);
        Assert.Equal(8, pointer.OptionCellGap);
        Assert.Equal(4, pointer.OptionHeadingGap);
        Assert.Equal(8, pointer.OptionInlineGap);
        Assert.Equal(16, pointer.MarkerSize);
        Assert.Equal(8, pointer.MarkerGap);
        Assert.Equal(8, pointer.SiblingGroupGap);
        Assert.Equal(32, pointer.NavRowHeight);
        Assert.Equal(48, touch.NavRowHeight);
        Assert.Equal(16, pointer.NavGlyphSize);
        Assert.Equal(20, touch.NavGlyphSize);

        // The type roles: the body is 12 (the floor of BOOLEAN-OPTION section 2 rule 5), and the input profile never
        // changes a type size - a finger needs bigger targets, not bigger words.
        Assert.Equal(12, pointer.FontSize);
        Assert.Equal(13, pointer.ProductFontSize);
        Assert.Equal(18, pointer.TitleFontSize);
        Assert.Equal(24, pointer.HeadingFontSize);
        Assert.Equal(pointer.FontSize, touch.FontSize);
    }

    [Fact]
    public void TypeRolesFollowTheInterfaceSizeAndOnlyIt()
    {
        var compact = UiMetricsResolver.Resolve(new UiProfile(UiInputProfile.Pointer, UiInterfaceSize.Compact));
        var large = UiMetricsResolver.Resolve(new UiProfile(UiInputProfile.Pointer, UiInterfaceSize.Large));

        Assert.Equal(9, compact.FontSize);
        Assert.Equal(15, large.FontSize);
        Assert.Equal(22.5, large.TitleFontSize);

        // OS text enlargement is a separate input and does not enter the type table; layout carries it.
        Assert.Equal(large.FontSize, UiMetricsResolver.Resolve(UiInputProfile.Pointer, 1.25, textScale: 2.0).FontSize);
    }

    [Fact]
    public void TheStandardSizeIsTheIdentityScale()
    {
        // The default changes no layout: a user who never opens the selector must see today's interface.
        Assert.Equal(1.0, UiProfile.Default.Scale);
        Assert.Equal(1.0, new UiProfile(UiInputProfile.Touch, UiInterfaceSize.Standard).Scale);
    }

    [Theory]
    [InlineData(UiInputProfile.Pointer, UiMetricsResolver.SmallestPointerTarget)]
    [InlineData(UiInputProfile.Touch, UiMetricsResolver.SmallestTouchTarget)]
    public void CompactModeReachesExactlyTheDeclaredSmallestTarget(UiInputProfile input, double declaredFloor)
    {
        var compact = UiMetricsResolver.Resolve(new UiProfile(input, UiInterfaceSize.Compact));

        Assert.Equal(declaredFloor, compact.TargetHeight);
        Assert.Equal(declaredFloor, compact.NavRowHeight);
        Assert.Equal(declaredFloor, UiMetricsResolver.SmallestTarget(input));
    }

    [Theory]
    [InlineData(UiInputProfile.Pointer)]
    [InlineData(UiInputProfile.Touch)]
    public void EverySizeStepGrowsEveryTargetMonotonicallyAndNeverBelowTheFloor(UiInputProfile input)
    {
        var steps = Enum.GetValues<UiInterfaceSize>()
            .Select(size => UiMetricsResolver.Resolve(new UiProfile(input, size)))
            .ToArray();

        for (var index = 1; index < steps.Length; index++)
        {
            Assert.True(steps[index].TargetHeight > steps[index - 1].TargetHeight);
            Assert.True(steps[index].GlyphSize >= steps[index - 1].GlyphSize);
            Assert.True(steps[index].CheckBoxBox >= steps[index - 1].CheckBoxBox);
            Assert.True(steps[index].MarkerSize >= steps[index - 1].MarkerSize);
        }

        foreach (var step in steps)
        {
            Assert.True(step.TargetHeight >= UiMetricsResolver.SmallestTarget(input));
            // The visible box must stay inside the target that carries it, at every size.
            Assert.True(step.CheckBoxBox <= step.TargetHeight);
            Assert.True(step.GlyphSize <= step.TargetHeight);
        }
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(1e-9)]
    [InlineData(1e9)]
    public void AnUnknownScaleNeverProducesAZeroSizeOrUnboundedTarget(double scale)
    {
        var metrics = UiMetricsResolver.Resolve(UiInputProfile.Pointer, scale);

        Assert.True(metrics.TargetHeight >= UiMetricsResolver.SmallestPointerTarget);
        Assert.True(metrics.TargetHeight <= 32 * SizeScales.Maximum);
        Assert.True(metrics.BorderWidth >= 1);
        Assert.True(metrics.CheckBoxBox > 0);
        Assert.True(metrics.GlyphSize > 0);
    }

    [Fact]
    public void TheLargestSupportedScaleRendersVeryLargeVectorsInsideTheirTargets()
    {
        // UI-SIZING section 4: the showcase exercises very large vector sizes too.
        var largest = UiMetricsResolver.Resolve(UiInputProfile.Touch, SizeScales.Maximum);

        Assert.Equal(96, largest.TargetHeight);
        Assert.Equal(40, largest.GlyphSize);
        Assert.Equal(48, largest.CheckBoxBox);
    }

    [Fact]
    public void TextEnlargementIsIndependentOfInterfaceScaleAndGrowsGlyphsOnlyModerately()
    {
        var ordinary = UiMetricsResolver.Resolve(UiInputProfile.Pointer, 1.0, textScale: 1.0);
        var enlarged = UiMetricsResolver.Resolve(UiInputProfile.Pointer, 1.0, textScale: 2.0);
        var huge = UiMetricsResolver.Resolve(UiInputProfile.Pointer, 1.0, textScale: 10.0);

        // Targets and gaps are the interface scale's alone; text enlargement reaches them through layout.
        Assert.Equal(ordinary.TargetHeight, enlarged.TargetHeight);
        Assert.Equal(ordinary.ButtonGap, enlarged.ButtonGap);
        Assert.Equal(ordinary.CheckBoxBox, enlarged.CheckBoxBox);

        // A glyph follows half of the text growth, and is bounded: 16 * 1.5 at the very most.
        Assert.Equal(24, enlarged.GlyphSize);
        Assert.Equal(24, huge.GlyphSize);
        Assert.True(UiMetricsResolver.Resolve(UiInputProfile.Pointer, 1.0, textScale: 0.5).GlyphSize == 16);
        Assert.True(UiMetricsResolver.Resolve(UiInputProfile.Pointer, 1.0, textScale: double.NaN).GlyphSize == 16);
    }

    [Fact]
    public void ChangingTheProfileChangesOnlyTheNamedInput()
    {
        var pointer = UiMetricsResolver.Resolve(UiInputProfile.Pointer, 1.25);
        var touch = UiMetricsResolver.Resolve(UiInputProfile.Touch, 1.25);

        Assert.Equal(pointer.Scale, touch.Scale);
        Assert.Equal(pointer.ButtonGap, touch.ButtonGap);
        Assert.Equal(pointer.CornerRadius, touch.CornerRadius);
        Assert.True(touch.TargetHeight > pointer.TargetHeight);
    }

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sp231-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public async Task TheProfileRoundTripsAndAnAbsentFileIsTheOrdinaryInterface()
    {
        var directory = TempDirectory();
        try
        {
            var store = new UiProfileStore(directory);
            Assert.Equal(UiProfile.Default, store.LoadSync());

            var chosen = new UiProfile(UiInputProfile.Touch, UiInterfaceSize.VeryLarge);
            Assert.True(await store.SaveAsync(chosen));

            Assert.Equal(chosen, new UiProfileStore(directory).LoadSync());
            Assert.Contains("\"input\":\"Touch\"", File.ReadAllText(store.FilePath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("{\"input\":\"Hologram\",\"size\":\"Large\"}", UiInputProfile.Pointer, UiInterfaceSize.Large)]
    [InlineData("{\"input\":\"touch\",\"size\":\"gigantic\"}", UiInputProfile.Touch, UiInterfaceSize.Standard)]
    [InlineData("{\"input\":7,\"size\":99}", UiInputProfile.Pointer, UiInterfaceSize.Standard)]
    [InlineData("{\"size\":\"Compact\"}", UiInputProfile.Pointer, UiInterfaceSize.Compact)]
    [InlineData("{}", UiInputProfile.Pointer, UiInterfaceSize.Standard)]
    public void AnUnknownNameCostsOnlyItsOwnField(string json, UiInputProfile input, UiInterfaceSize size)
    {
        var directory = TempDirectory();
        try
        {
            var store = new UiProfileStore(directory);
            File.WriteAllText(store.FilePath, json);

            // A number where a name belongs is not a name; the numeric string "7" must not parse as an enum value.
            Assert.Equal(new UiProfile(input, size), store.LoadSync());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AFileThatCouldNotBeReadIsNeverWrittenOver()
    {
        var directory = TempDirectory();
        try
        {
            var store = new UiProfileStore(directory);
            File.WriteAllText(store.FilePath, "{ this is not json");

            Assert.Equal(UiProfile.Default, store.LoadSync());
            Assert.False(await store.SaveAsync(new UiProfile(UiInputProfile.Touch, UiInterfaceSize.Large)));
            Assert.Equal("{ this is not json", File.ReadAllText(store.FilePath));

            // Once the damaged file is replaced by hand, the next read succeeds and saving resumes.
            File.WriteAllText(store.FilePath, "{\"input\":\"Pointer\",\"size\":\"Compact\"}");
            Assert.Equal(new UiProfile(UiInputProfile.Pointer, UiInterfaceSize.Compact), store.LoadSync());
            Assert.True(await store.SaveAsync(new UiProfile(UiInputProfile.Touch, UiInterfaceSize.Large)));
            using var saved = JsonDocument.Parse(File.ReadAllText(store.FilePath));
            Assert.Equal("Large", saved.RootElement.GetProperty("size").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
