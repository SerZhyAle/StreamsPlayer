namespace StreamsPlayer.Core;

/// <summary>
/// SP-0231, <c>UI-SIZING</c> section 2 rule 1: which targets the interface draws. Chosen once when a
/// surface opens, or by an explicit user choice, and never changed by mouse movement or a touch.
/// </summary>
public enum UiInputProfile
{
    /// <summary>Mouse, pen and keyboard: 32-unit ordinary targets. The default of a Windows desktop.</summary>
    Pointer,

    /// <summary>Finger: 48-unit ordinary targets.</summary>
    Touch
}

/// <summary>
/// SP-0231, <c>UI-SIZING</c> section 2 rule 3: the interface size the user chose. It scales text, glyphs,
/// padding, gaps and targets together; the text-only enlargement of the operating system is a separate
/// input to <see cref="UiMetricsResolver"/>, never folded into this one.
/// </summary>
public enum UiInterfaceSize
{
    Compact,
    Standard,
    Large,
    VeryLarge
}

/// <summary>The two choices that define the interface's measurements, as the user made them.</summary>
public sealed record UiProfile(
    UiInputProfile Input = UiInputProfile.Pointer,
    UiInterfaceSize Size = UiInterfaceSize.Standard)
{
    /// <summary>What a fresh install and an unreadable file both mean: the ordinary pointer interface.</summary>
    public static UiProfile Default { get; } = new();

    /// <summary>The scale factor of this size. Standard is exactly 1, so the default changes no layout.</summary>
    public double Scale => SizeScales.For(Size);
}

/// <summary>The one table of interface-size factors.</summary>
public static class SizeScales
{
    /// <summary>
    /// The smallest supported factor. It is also the compact mode of <c>UI-SIZING</c> section 2 rule 4, so
    /// the declared smallest targets (<see cref="UiMetricsResolver.SmallestPointerTarget"/> and
    /// <see cref="UiMetricsResolver.SmallestTouchTarget"/>) are exactly the ordinary targets times this value.
    /// </summary>
    public const double Minimum = 0.75;

    /// <summary>
    /// Above the largest step the selector offers: a hand-edited or newer file may name a larger factor, and an
    /// unbounded one would grow every target without limit.
    /// </summary>
    public const double Maximum = 2.0;

    public static double For(UiInterfaceSize size) => size switch
    {
        UiInterfaceSize.Compact => Minimum,
        UiInterfaceSize.Large => 1.25,
        UiInterfaceSize.VeryLarge => 1.5,
        _ => 1.0
    };

    /// <summary>A factor held to the supported range; NaN and infinities read as the ordinary size.</summary>
    public static double Clamp(double scale) =>
        double.IsFinite(scale) ? Math.Clamp(scale, Minimum, Maximum) : 1.0;
}
