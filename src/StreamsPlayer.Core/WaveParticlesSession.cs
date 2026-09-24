namespace StreamsPlayer.Core;

/// <summary>
/// One session of the "particles and lines" backdrop: the values <c>WAVE-PARTICLES section 3.2</c> rolls
/// once, the clock, the particles, and the geometry of one frame as section 4 draws it. Pure arithmetic -
/// the caller owns the buffer, the wash and the drawing.
/// </summary>
/// <remarks>
/// A session is rolled once and then only advanced (rule 3). A resume is simply not creating a new one;
/// a resize keeps every roll and re-seeds only the particle positions (rule 12). The clock is advanced by
/// elapsed time, never by frames drawn (rule 2), and so are the ramp and the wash (section 4 as amended
/// in 0.10), so the picture is the same at 60, 120 or 144 Hz.
/// </remarks>
public sealed class WaveParticlesSession
{
    private readonly Random _random;
    private readonly WaveParticle[] _particles;
    private double _rampFrames;

    public WaveParticlesSession(
        double width,
        double height,
        Random? random = null,
        WaveParticlesProfile profile = WaveParticlesProfile.Full,
        WaveParticlesPalette palette = WaveParticlesPalette.Dynamic,
        WaveParticlesTuning? tuning = null)
    {
        _random = random ?? new Random();
        Profile = profile;
        Palette = palette;
        Tuning = tuning ?? WaveParticlesTuning.Default;
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);

        var reduced = profile == WaveParticlesProfile.Reduced;
        DirectionDegrees = Uniform(0, 360);
        var radians = DirectionDegrees * Math.PI / 180;
        DirectionX = Math.Cos(radians);
        DirectionY = Math.Sin(radians);
        LineCount = reduced
            ? Integer(WaveParticlesConstants.ReducedLineCountMin, WaveParticlesConstants.ReducedLineCountMax)
            : Integer(WaveParticlesConstants.FullLineCountMin, WaveParticlesConstants.FullLineCountMax);
        SampleStep = WaveParticlesConstants.SampleStepBase
            * Uniform(WaveParticlesConstants.SampleStepJitterMin, WaveParticlesConstants.SampleStepJitterMax);
        StrokeWidth = Uniform(WaveParticlesConstants.StrokeWidthMin, WaveParticlesConstants.StrokeWidthMax);
        AmplitudeFraction = Uniform(WaveParticlesConstants.AmplitudeFractionMin, WaveParticlesConstants.AmplitudeFractionMax);
        RolledParticleCount = reduced
            ? Integer(WaveParticlesConstants.ReducedParticleCountMin, WaveParticlesConstants.ReducedParticleCountMax)
            : Integer(WaveParticlesConstants.FullParticleCountMin, WaveParticlesConstants.FullParticleCountMax);
        SpeedMultiplier = Uniform(WaveParticlesConstants.SpeedMultiplierMin, WaveParticlesConstants.SpeedMultiplierMax);
        RollPalette();

        // Rule 15: density scales the roll, rounded, and never above it.
        var count = Math.Min(RolledParticleCount, (int)Math.Round(RolledParticleCount * Tuning.Density));
        _particles = new WaveParticle[count];
        for (var i = 0; i < _particles.Length; i++)
        {
            _particles[i] = RollParticle();
        }
    }

    public WaveParticlesProfile Profile { get; }
    public WaveParticlesPalette Palette { get; }
    public WaveParticlesTuning Tuning { get; }
    public double Width { get; private set; }
    public double Height { get; private set; }

    public double DirectionDegrees { get; }
    public double DirectionX { get; }
    public double DirectionY { get; }
    public double NormalX => -DirectionY;
    public double NormalY => DirectionX;
    public int LineCount { get; }
    public double SampleStep { get; }
    public double StrokeWidth { get; }
    public double AmplitudeFraction { get; }
    public int RolledParticleCount { get; }
    public double SpeedMultiplier { get; }
    public double LineBaseHue { get; private set; }
    public double LineHueStep { get; private set; }
    public double ParticleHueBase { get; private set; }
    public double ParticleSpread { get; private set; }

    public ReadOnlySpan<WaveParticle> Particles => _particles;

    /// <summary>The phase <c>t</c> of section 4.</summary>
    public double Time { get; private set; }

    /// <summary>The ramp counter <c>f</c>, in reference frames, capped at <see cref="WaveParticlesConstants.RampFrames"/>.</summary>
    public double RampFramesElapsed => _rampFrames;

    public bool IsSettled => _rampFrames >= WaveParticlesConstants.RampFrames;

    /// <summary>The gain <c>g</c> of section 4: 0.35 at a fresh start, 1 once the ramp is over.</summary>
    public double Gain
    {
        get
        {
            var p = _rampFrames / WaveParticlesConstants.RampFrames;
            return WaveParticlesConstants.RampGainStart + ((1 - WaveParticlesConstants.RampGainStart) * p * (2 - p));
        }
    }

    public double LineOpacity =>
        (WaveParticlesConstants.LineOpacityBase + (WaveParticlesConstants.LineOpacityGain * Gain)) * WaveParticlesConstants.OpacityScale;

    public double ParticleOpacity =>
        (WaveParticlesConstants.ParticleOpacityBase + (WaveParticlesConstants.ParticleOpacityGain * Gain)) * WaveParticlesConstants.OpacityScale;

    /// <summary>The number of points <see cref="FillLine"/> writes at the current size.</summary>
    public int LinePointCount => (int)Math.Floor((PathSpan / SampleStep) + 1e-9) + 1;

    private double PathSpan => Math.Sqrt((Width * Width) + (Height * Height)) + (WaveParticlesConstants.PathSpanSteps * SampleStep);

    /// <summary>
    /// The wash alpha for a step of <paramref name="referenceFrames"/>: what that many washes at
    /// <c>WASH_ALPHA</c> would leave (section 4, amended in 0.10). Exactly <c>WASH_ALPHA</c> at 1.
    /// </summary>
    public static double WashAlphaFor(double referenceFrames) =>
        referenceFrames <= 0 ? 0 : 1 - Math.Pow(1 - WaveParticlesConstants.WashAlpha, referenceFrames);

    /// <summary>Advances by real elapsed time and returns the wash alpha the caller applies before drawing.</summary>
    public double Advance(double elapsedSeconds) =>
        AdvanceFrames(Math.Max(0, elapsedSeconds) / WaveParticlesConstants.ReferenceFrameSeconds);

    /// <summary>Advances by <paramref name="k"/> reference frames: the clock, the ramp and every particle.</summary>
    public double AdvanceFrames(double k)
    {
        if (k <= 0 || double.IsNaN(k))
        {
            return 0;
        }

        Time += WaveParticlesConstants.TimeStep * Tuning.Speed * k;
        _rampFrames = Math.Min(_rampFrames + k, WaveParticlesConstants.RampFrames);
        for (var i = 0; i < _particles.Length; i++)
        {
            ref var particle = ref _particles[i];
            particle.X += particle.VelocityX * k;
            particle.Y += particle.VelocityY * k;
            if (particle.X < 0 || particle.X > Width)
            {
                particle.VelocityX = -particle.VelocityX;
            }

            if (particle.Y < 0 || particle.Y > Height)
            {
                particle.VelocityY = -particle.VelocityY;
            }
        }

        return WashAlphaFor(k);
    }

    /// <summary>
    /// Writes lane <paramref name="lane"/>'s path as x,y pairs into <paramref name="xy"/> and returns the
    /// number of points written (at most <see cref="LinePointCount"/>).
    /// </summary>
    public int FillLine(int lane, Span<double> xy)
    {
        var shortSide = Math.Min(Width, Height);
        var drift = Math.Sin(WaveParticlesConstants.CentreDriftRate * Time) * WaveParticlesConstants.CentreDriftFraction * shortSide;
        var centreX = (Width / 2) + (DirectionX * drift);
        var centreY = (Height / 2) + (DirectionY * drift);
        var laneSpacing = WaveParticlesConstants.LaneSpacingFraction * shortSide;
        var band = (lane - ((LineCount - 1) / 2.0)) * laneSpacing;
        var envelope = WaveParticlesConstants.EnvelopeBase
            + (WaveParticlesConstants.EnvelopeGain * Math.Abs(Math.Sin((WaveParticlesConstants.EnvelopeRate * Time) + (WaveParticlesConstants.EnvelopeLanePhase * lane))));
        var amplitude = Height * AmplitudeFraction * Gain * envelope;
        var count = Math.Min(LinePointCount, xy.Length / 2);
        var start = -PathSpan / 2;
        for (var i = 0; i < count; i++)
        {
            var s = start + (i * SampleStep);
            var y = Math.Sin((WaveParticlesConstants.SpatialFrequency * s) + Time + (WaveParticlesConstants.LanePhaseStep * lane)) * amplitude;
            var offset = band + y;
            xy[2 * i] = centreX + (DirectionX * s) + (NormalX * offset);
            xy[(2 * i) + 1] = centreY + (DirectionY * s) + (NormalY * offset);
        }

        return count;
    }

    /// <summary>Line <paramref name="lane"/>'s colour; rule 7 mirrors lightness, never hue, on a light surface.</summary>
    public HslColor LineColor(int lane, bool lightSurface) => new(
        Mod360(LineBaseHue + (lane * LineHueStep)),
        WaveParticlesConstants.LineSaturation,
        lightSurface ? WaveParticlesConstants.LightLineLightness : WaveParticlesConstants.DarkLineLightness,
        LineOpacity);

    public HslColor ParticleColor(int index, bool lightSurface) => new(
        _particles[index].Hue,
        WaveParticlesConstants.ParticleSaturation,
        lightSurface ? WaveParticlesConstants.LightParticleLightness : WaveParticlesConstants.DarkParticleLightness,
        ParticleOpacity);

    /// <summary>
    /// Rule 12: a new buffer size keeps every roll, the clock and the ramp, and re-seeds only where the
    /// particles are, inside the new bounds.
    /// </summary>
    public void Resize(double width, double height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == Width && height == Height)
        {
            return;
        }

        Width = width;
        Height = height;
        for (var i = 0; i < _particles.Length; i++)
        {
            _particles[i].X = Uniform(0, Width);
            _particles[i].Y = Uniform(0, Height);
        }
    }

    private void RollPalette()
    {
        switch (Palette)
        {
            case WaveParticlesPalette.Green:
                RollNamed(WaveParticlesConstants.GreenLineHueMin, WaveParticlesConstants.GreenLineHueMax, WaveParticlesConstants.GreenParticleHueCentre);
                break;
            case WaveParticlesPalette.Pink:
                RollNamed(WaveParticlesConstants.PinkLineHueMin, WaveParticlesConstants.PinkLineHueMax, WaveParticlesConstants.PinkParticleHueCentre);
                break;
            case WaveParticlesPalette.Blue:
                RollNamed(WaveParticlesConstants.BlueLineHueMin, WaveParticlesConstants.BlueLineHueMax, WaveParticlesConstants.BlueParticleHueCentre);
                break;
            default:
                LineBaseHue = Uniform(0, 360);
                LineHueStep = Uniform(WaveParticlesConstants.DynamicLineHueStepMin, WaveParticlesConstants.DynamicLineHueStepMax);
                ParticleHueBase = Uniform(0, 360);
                ParticleSpread = WaveParticlesConstants.DynamicParticleSpread;
                break;
        }
    }

    private void RollNamed(double lineHueMin, double lineHueMax, double particleCentre)
    {
        LineBaseHue = Uniform(lineHueMin, lineHueMax);
        LineHueStep = Uniform(WaveParticlesConstants.NamedLineHueStepMin, WaveParticlesConstants.NamedLineHueStepMax);
        ParticleHueBase = particleCentre + ((_random.NextDouble() - 0.5) * WaveParticlesConstants.NamedPaletteParticleBaseSpread);
        ParticleSpread = WaveParticlesConstants.NamedPaletteSpread;
    }

    private WaveParticle RollParticle()
    {
        var directional = (WaveParticlesConstants.ParticleSpeedBase + Uniform(0, WaveParticlesConstants.ParticleSpeedSpread)) * SpeedMultiplier;
        if (_random.NextDouble() < WaveParticlesConstants.CounterDriftProbability)
        {
            directional *= WaveParticlesConstants.CounterDriftFactor;
        }

        return new WaveParticle
        {
            X = Uniform(0, Width),
            Y = Uniform(0, Height),
            Radius = Uniform(WaveParticlesConstants.ParticleRadiusMin, WaveParticlesConstants.ParticleRadiusMax),
            VelocityX = (DirectionX * directional) + Jitter(),
            VelocityY = (DirectionY * directional) + Jitter(),
            Hue = Mod360(ParticleHueBase + ((_random.NextDouble() - 0.5) * ParticleSpread))
        };
    }

    private double Jitter() => (_random.NextDouble() - 0.5) * WaveParticlesConstants.ParticleJitter * SpeedMultiplier;

    private double Uniform(double min, double max) => min + (_random.NextDouble() * (max - min));

    private int Integer(int min, int max) => _random.Next(min, max + 1);

    private static double Mod360(double degrees) => ((degrees % 360) + 360) % 360;
}
