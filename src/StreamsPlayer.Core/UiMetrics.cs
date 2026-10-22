namespace StreamsPlayer.Core;

/// <summary>
/// SP-0231: every measurement the covered components share, in logical units (WPF device-independent
/// pixels), for one chosen <see cref="UiProfile"/>.
/// </summary>
/// <remarks>
/// <c>UI-SIZING</c> section 2 rule 2 forbids mixing a large button with an unscaled checkbox or a fixed-size
/// rail, so the buttons, the boolean option, the group header and the settings rail all read this one record
/// and none of them carries a size literal of its own. The values are the ordinary-size tokens of
/// <c>BUTTONS</c> section 3, <c>BOOLEAN-OPTION</c> section 2, <c>COLLAPSIBLE-GROUP</c> section 3 and
/// <c>SETTINGS-NAVIGATION</c> section 2 rule 5, multiplied by the interface scale - they are baselines, not
/// ceilings.
/// </remarks>
public sealed record UiMetrics(
    UiInputProfile Input,
    double Scale,
    double TargetHeight,
    double ButtonPaddingX,
    double GlyphSize,
    double GlyphGap,
    double CornerRadius,
    double BorderWidth,
    double ButtonGap,
    double SemanticGroupGap,
    double CheckBoxBox,
    double OptionCellGap,
    double OptionHeadingGap,
    double OptionInlineGap,
    double MarkerSize,
    double MarkerGap,
    double GroupBodyInset,
    double SiblingGroupGap,
    double NavRowHeight,
    double NavGlyphSize,
    double NavIndent,
    double FontSize,
    double ProductFontSize,
    double TitleFontSize,
    double HeadingFontSize);

/// <summary>Turns a <see cref="UiProfile"/> into <see cref="UiMetrics"/>; the only place the tokens are written down.</summary>
public static class UiMetricsResolver
{
    /// <summary>
    /// The smallest pointer target this product supports (compact mode): 24 by 24 units. <c>UI-SIZING</c>
    /// section 2 rule 4 lets a product go below the 32-unit baseline only by declaring its floor and
    /// measuring it; this is the declaration, and <c>UiMetricsTests</c> is the measurement.
    /// </summary>
    public const double SmallestPointerTarget = 24;

    /// <summary>The smallest touch target this product supports (compact mode): 36 by 36 units.</summary>
    public const double SmallestTouchTarget = 36;

    /// <summary>
    /// The most the operating system's text enlargement may grow a companion glyph (<c>UI-SIZING</c> section 2
    /// rule 3). Text grows with the full multiplier; a glyph follows half of it, to at most this factor.
    /// </summary>
    public const double MaximumGlyphTextGrowth = 1.5;

    private const double GlyphShareOfTextGrowth = 0.5;

    public static UiMetrics Resolve(UiProfile profile, double textScale = 1.0) =>
        Resolve(profile.Input, profile.Scale, textScale);

    /// <param name="input">The profile chosen when the surface opened.</param>
    /// <param name="scale">The interface scale; held to the supported range, so no value produces a zero-size target.</param>
    /// <param name="textScale">The text-only enlargement of the operating system, independent of <paramref name="scale"/>.</param>
    public static UiMetrics Resolve(UiInputProfile input, double scale, double textScale = 1.0)
    {
        scale = SizeScales.Clamp(scale);
        var touch = input == UiInputProfile.Touch;
        var glyphGrowth = GlyphGrowth(textScale);

        double Scaled(double ordinary) => Math.Round(ordinary * scale, MidpointRounding.AwayFromZero);

        var target = Scaled(touch ? 48 : 32);
        var glyph = Math.Round((touch ? 20 : 16) * scale * glyphGrowth, MidpointRounding.AwayFromZero);

        return new UiMetrics(
            input,
            scale,
            TargetHeight: target,
            ButtonPaddingX: Scaled(touch ? 16 : 12),
            GlyphSize: glyph,
            GlyphGap: Scaled(8),
            CornerRadius: Scaled(4),
            // A hairline stays a hairline: scaling 1 down would draw a border that blurs across two pixels.
            BorderWidth: Math.Max(1, Scaled(1)),
            ButtonGap: Scaled(8),
            SemanticGroupGap: Scaled(16),
            CheckBoxBox: Scaled(touch ? 24 : 20),
            OptionCellGap: Scaled(8),
            OptionHeadingGap: Scaled(4),
            OptionInlineGap: Scaled(8),
            MarkerSize: Scaled(16),
            MarkerGap: Scaled(8),
            GroupBodyInset: Scaled(12),
            SiblingGroupGap: Scaled(8),
            NavRowHeight: target,
            NavGlyphSize: glyph,
            NavIndent: Scaled(12),
            // The type roles scale with the interface size and nothing else: the operating system's text enlargement
            // reaches them through layout, not through this table (UI-SIZING section 2 rule 3). The body is 12, the
            // floor BOOLEAN-OPTION section 2 rule 5 sets for an explanation, so a hint and the body share a size.
            FontSize: 12 * scale,
            ProductFontSize: 13 * scale,
            TitleFontSize: 18 * scale,
            HeadingFontSize: 24 * scale);
    }

    /// <summary>The smallest target dimension the profile may reach, the declared floor of its compact mode.</summary>
    public static double SmallestTarget(UiInputProfile input) =>
        input == UiInputProfile.Touch ? SmallestTouchTarget : SmallestPointerTarget;

    private static double GlyphGrowth(double textScale) =>
        double.IsFinite(textScale) && textScale > 1.0
            ? Math.Min(MaximumGlyphTextGrowth, 1.0 + (textScale - 1.0) * GlyphShareOfTextGrowth)
            : 1.0;
}
