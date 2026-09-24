namespace StreamsPlayer.Core;

/// <summary>The four palettes of <c>WAVE-PARTICLES section 3.3</c>; <see cref="Dynamic"/> is the default.</summary>
public enum WaveParticlesPalette
{
    Dynamic,
    Green,
    Pink,
    Blue
}

/// <summary><c>WAVE-PARTICLES</c> rule 13: the reduced profile lowers counts and nothing else.</summary>
public enum WaveParticlesProfile
{
    Full,
    Reduced
}

/// <summary>
/// <c>WAVE-PARTICLES</c> rule 11: what the motion is for, which decides when a power policy freezes it.
/// </summary>
public enum WaveParticlesIntent
{
    /// <summary>The motion is part of the content, as behind an audio player.</summary>
    Ambient,

    /// <summary>Ornament: a backdrop behind other content.</summary>
    Decorative
}

/// <summary>
/// The three user controls of <c>WAVE-PARTICLES</c> rule 15. The bounds are enforced here, whatever a
/// caller hands in, so a surface that dims itself through <see cref="Intensity"/> cannot brighten.
/// </summary>
public sealed record WaveParticlesTuning
{
    public static WaveParticlesTuning Default { get; } = new();

    public WaveParticlesTuning(double intensity = 1, double speed = 1, double density = 1)
    {
        Intensity = Clamp(intensity, WaveParticlesConstants.IntensityMin, WaveParticlesConstants.IntensityMax, 1);
        Speed = Clamp(speed, WaveParticlesConstants.SpeedMin, WaveParticlesConstants.SpeedMax, 1);
        Density = Clamp(density, WaveParticlesConstants.DensityMin, WaveParticlesConstants.DensityMax, 1);
    }

    public double Intensity { get; }
    public double Speed { get; }
    public double Density { get; }

    // NaN is the one input Math.Clamp passes through; it falls back to the default rather than poisoning
    // every frame after it.
    private static double Clamp(double value, double min, double max, double fallback) =>
        double.IsNaN(value) ? fallback : Math.Clamp(value, min, max);
}

/// <summary>An HSL colour with an opacity, each in <c>[0, 1]</c> except the hue in degrees.</summary>
public readonly record struct HslColor(double Hue, double Saturation, double Lightness, double Alpha)
{
    /// <summary>Standard HSL to RGB, rounded to bytes.</summary>
    public (byte R, byte G, byte B) ToRgb()
    {
        var h = ((Hue % 360) + 360) % 360 / 360.0;
        var s = Math.Clamp(Saturation, 0, 1);
        var l = Math.Clamp(Lightness, 0, 1);
        if (s == 0)
        {
            var grey = ToByte(l);
            return (grey, grey, grey);
        }

        var q = l < 0.5 ? l * (1 + s) : l + s - (l * s);
        var p = (2 * l) - q;
        return (ToByte(Channel(p, q, h + (1.0 / 3))), ToByte(Channel(p, q, h)), ToByte(Channel(p, q, h - (1.0 / 3))));
    }

    public byte AlphaByte => ToByte(Math.Clamp(Alpha, 0, 1));

    private static double Channel(double p, double q, double t)
    {
        if (t < 0)
        {
            t += 1;
        }

        if (t > 1)
        {
            t -= 1;
        }

        if (t < 1.0 / 6)
        {
            return p + ((q - p) * 6 * t);
        }

        if (t < 0.5)
        {
            return q;
        }

        return t < 2.0 / 3 ? p + ((q - p) * ((2.0 / 3) - t) * 6) : p;
    }

    private static byte ToByte(double unit) => (byte)Math.Round(unit * 255);
}

/// <summary>One drifting particle, in buffer pixels and pixels per reference frame.</summary>
public struct WaveParticle
{
    public double X;
    public double Y;
    public double VelocityX;
    public double VelocityY;
    public double Radius;
    public double Hue;
}
