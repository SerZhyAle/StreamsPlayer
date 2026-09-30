using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// <c>WAVE-PARTICLES</c> rung 1 (section 3 read value by value) and the section 4 pacing, amended in 0.10,
/// that makes the picture independent of the refresh rate.
/// </summary>
public sealed class WaveParticlesSessionTests
{
    [Fact]
    public void Constants_MatchContractSection3()
    {
        Assert.Equal(1.0 / 60, WaveParticlesConstants.ReferenceFrameSeconds, 12);
        Assert.Equal(0.002, WaveParticlesConstants.TimeStep);
        Assert.Equal(36, WaveParticlesConstants.RampFrames);
        Assert.Equal((5, 12), (WaveParticlesConstants.FullLineCountMin, WaveParticlesConstants.FullLineCountMax));
        Assert.Equal((3, 6), (WaveParticlesConstants.ReducedLineCountMin, WaveParticlesConstants.ReducedLineCountMax));
        Assert.Equal((15, 55), (WaveParticlesConstants.FullParticleCountMin, WaveParticlesConstants.FullParticleCountMax));
        Assert.Equal((6, 20), (WaveParticlesConstants.ReducedParticleCountMin, WaveParticlesConstants.ReducedParticleCountMax));
        Assert.Equal((20.0, 0.8, 1.2), (WaveParticlesConstants.SampleStepBase, WaveParticlesConstants.SampleStepJitterMin, WaveParticlesConstants.SampleStepJitterMax));
        Assert.Equal((3.0, 6.0), (WaveParticlesConstants.StrokeWidthMin, WaveParticlesConstants.StrokeWidthMax));
        Assert.Equal((0.28, 0.48), (WaveParticlesConstants.AmplitudeFractionMin, WaveParticlesConstants.AmplitudeFractionMax));
        Assert.Equal((0.5, 1.5), (WaveParticlesConstants.SpeedMultiplierMin, WaveParticlesConstants.SpeedMultiplierMax));
        Assert.Equal((1.0, 6.0), (WaveParticlesConstants.ParticleRadiusMin, WaveParticlesConstants.ParticleRadiusMax));
        Assert.Equal((0.12, 0.42), (WaveParticlesConstants.ParticleSpeedBase, WaveParticlesConstants.ParticleSpeedSpread));
        Assert.Equal((0.18, -0.35, 0.28), (WaveParticlesConstants.CounterDriftProbability, WaveParticlesConstants.CounterDriftFactor, WaveParticlesConstants.ParticleJitter));
        Assert.Equal((8.0, 20.0, 108.0), (WaveParticlesConstants.DynamicLineHueStepMin, WaveParticlesConstants.DynamicLineHueStepMax, WaveParticlesConstants.DynamicParticleSpread));
        Assert.Equal((2.0, 6.0, 30.0, 30.0), (WaveParticlesConstants.NamedLineHueStepMin, WaveParticlesConstants.NamedLineHueStepMax, WaveParticlesConstants.NamedPaletteParticleBaseSpread, WaveParticlesConstants.NamedPaletteSpread));
        Assert.Equal((95.0, 130.0, 115.0), (WaveParticlesConstants.GreenLineHueMin, WaveParticlesConstants.GreenLineHueMax, WaveParticlesConstants.GreenParticleHueCentre));
        Assert.Equal((305.0, 335.0, 320.0), (WaveParticlesConstants.PinkLineHueMin, WaveParticlesConstants.PinkLineHueMax, WaveParticlesConstants.PinkParticleHueCentre));
        Assert.Equal((200.0, 230.0, 215.0), (WaveParticlesConstants.BlueLineHueMin, WaveParticlesConstants.BlueLineHueMax, WaveParticlesConstants.BlueParticleHueCentre));
        Assert.Equal(((byte)10, (byte)245), (WaveParticlesConstants.DarkWashLevel, WaveParticlesConstants.LightWashLevel));
        Assert.Equal(38.0 / 255, WaveParticlesConstants.WashAlpha, 12);
        Assert.Equal((0.80, 0.65, 0.35), (WaveParticlesConstants.LineSaturation, WaveParticlesConstants.DarkLineLightness, WaveParticlesConstants.LightLineLightness));
        Assert.Equal((0.90, 0.70, 0.30), (WaveParticlesConstants.ParticleSaturation, WaveParticlesConstants.DarkParticleLightness, WaveParticlesConstants.LightParticleLightness));
        Assert.Equal((0.28, 0.16, 0.38, 0.32, 0.70), (WaveParticlesConstants.LineOpacityBase, WaveParticlesConstants.LineOpacityGain, WaveParticlesConstants.ParticleOpacityBase, WaveParticlesConstants.ParticleOpacityGain, WaveParticlesConstants.OpacityScale));
        Assert.Equal((0.45, 0.02, 0.038, 6.0), (WaveParticlesConstants.CentreDriftRate, WaveParticlesConstants.CentreDriftFraction, WaveParticlesConstants.LaneSpacingFraction, WaveParticlesConstants.PathSpanSteps));
        Assert.Equal((0.0105, 0.8), (WaveParticlesConstants.SpatialFrequency, WaveParticlesConstants.LanePhaseStep));
        Assert.Equal((0.40, 0.60, 0.4, 0.2), (WaveParticlesConstants.EnvelopeBase, WaveParticlesConstants.EnvelopeGain, WaveParticlesConstants.EnvelopeRate, WaveParticlesConstants.EnvelopeLanePhase));
        Assert.Equal(0.35, WaveParticlesConstants.RampGainStart);
        Assert.Equal((0.0, 1.0, 0.25, 2.0, 0.0, 1.0), (WaveParticlesConstants.IntensityMin, WaveParticlesConstants.IntensityMax, WaveParticlesConstants.SpeedMin, WaveParticlesConstants.SpeedMax, WaveParticlesConstants.DensityMin, WaveParticlesConstants.DensityMax));
    }

    [Theory]
    [InlineData(WaveParticlesProfile.Full, 5, 12, 15, 55)]
    [InlineData(WaveParticlesProfile.Reduced, 3, 6, 6, 20)]
    public void Rolls_StayInsideTheProfileRanges(WaveParticlesProfile profile, int lineMin, int lineMax, int particleMin, int particleMax)
    {
        for (var seed = 0; seed < 300; seed++)
        {
            var session = new WaveParticlesSession(600, 80, new Random(seed), profile);
            Assert.InRange(session.LineCount, lineMin, lineMax);
            Assert.InRange(session.RolledParticleCount, particleMin, particleMax);
            Assert.Equal(session.RolledParticleCount, session.Particles.Length);
            Assert.InRange(session.DirectionDegrees, 0, 360 - 1e-9);
            Assert.InRange(session.SampleStep, 16, 24);
            Assert.InRange(session.StrokeWidth, 3, 6);
            Assert.InRange(session.AmplitudeFraction, 0.28, 0.48);
            Assert.InRange(session.SpeedMultiplier, 0.5, 1.5);
            foreach (var particle in session.Particles)
            {
                Assert.InRange(particle.X, 0, 600);
                Assert.InRange(particle.Y, 0, 80);
                Assert.InRange(particle.Radius, 1, 6);
                Assert.InRange(particle.Hue, 0, 360);
            }
        }
    }

    [Theory]
    [InlineData(WaveParticlesPalette.Green, 95, 130, 100, 130)]
    [InlineData(WaveParticlesPalette.Pink, 305, 335, 305, 335)]
    [InlineData(WaveParticlesPalette.Blue, 200, 230, 200, 230)]
    public void NamedPalettes_RollInsideTheirHueBands(WaveParticlesPalette palette, double lineMin, double lineMax, double baseMin, double baseMax)
    {
        for (var seed = 0; seed < 200; seed++)
        {
            var session = new WaveParticlesSession(400, 300, new Random(seed), palette: palette);
            Assert.InRange(session.LineBaseHue, lineMin, lineMax);
            Assert.InRange(session.LineHueStep, 2, 6);
            Assert.InRange(session.ParticleHueBase, baseMin, baseMax);
            Assert.Equal(30, session.ParticleSpread);
        }
    }

    [Fact]
    public void DynamicPalette_IsTheDefault()
    {
        var session = new WaveParticlesSession(400, 300, new Random(1));
        Assert.Equal(WaveParticlesPalette.Dynamic, session.Palette);
        Assert.Equal(108, session.ParticleSpread);
        Assert.InRange(session.LineHueStep, 8, 20);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(30)]
    public void OneSecond_IsTheSamePictureAtAnyRefreshRate(int framesPerSecond)
    {
        // A bounce is decided on a drawn frame, so its exact point depends on the frame rate by nature;
        // the comparison is of free flight, well away from every edge.
        const double Size = 4000;
        const double Margin = 100;
        var reference = new WaveParticlesSession(Size, Size, new Random(7));
        var session = new WaveParticlesSession(Size, Size, new Random(7));
        var inside = reference.Particles.ToArray()
            .Select((p, i) => (p, i))
            .Where(x => x.p.X is > Margin and < Size - Margin && x.p.Y is > Margin and < Size - Margin)
            .Select(x => x.i)
            .ToArray();
        Assert.NotEmpty(inside);
        for (var i = 0; i < 60; i++)
        {
            reference.AdvanceFrames(1);
        }

        for (var i = 0; i < framesPerSecond; i++)
        {
            session.Advance(1.0 / framesPerSecond);
        }

        Assert.Equal(reference.Time, session.Time, 9);
        Assert.Equal(0.12, session.Time, 9);
        Assert.Equal(reference.RampFramesElapsed, session.RampFramesElapsed, 9);
        foreach (var i in inside)
        {
            Assert.Equal(reference.Particles[i].X, session.Particles[i].X, 6);
            Assert.Equal(reference.Particles[i].Y, session.Particles[i].Y, 6);
        }
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(2)]
    [InlineData(2.4)]
    public void Wash_LeavesWhatKWashesAtWashAlphaWouldLeave(double k)
    {
        Assert.Equal(WaveParticlesConstants.WashAlpha, WaveParticlesSession.WashAlphaFor(1), 12);
        var remaining = 1 - WaveParticlesSession.WashAlphaFor(k);
        Assert.Equal(Math.Pow(1 - WaveParticlesConstants.WashAlpha, k), remaining, 12);
        Assert.Equal(0, WaveParticlesSession.WashAlphaFor(0));
    }

    [Fact]
    public void Ramp_RaisesGainFromStartToOneOverThirtySixReferenceFrames()
    {
        var session = new WaveParticlesSession(600, 80, new Random(3));
        Assert.Equal(0.35, session.Gain, 12);
        Assert.False(session.IsSettled);
        session.Advance(0.3);
        Assert.InRange(session.Gain, 0.36, 0.99);
        session.Advance(0.3);
        Assert.True(session.IsSettled);
        Assert.Equal(1, session.Gain, 12);
        Assert.Equal(0.308, session.LineOpacity, 3);
        Assert.Equal(0.490, session.ParticleOpacity, 3);
        session.Advance(10);
        Assert.Equal(36, session.RampFramesElapsed);
    }

    [Fact]
    public void Resize_KeepsRollsAndClock_AndReseedsParticlesInsideTheNewBounds()
    {
        var session = new WaveParticlesSession(900, 68, new Random(11));
        session.Advance(1);
        var (direction, lines, time, ramp) = (session.DirectionDegrees, session.LineCount, session.Time, session.RampFramesElapsed);
        var velocities = session.Particles.ToArray().Select(p => (p.VelocityX, p.VelocityY, p.Radius, p.Hue)).ToArray();

        session.Resize(600, 80);

        Assert.Equal((direction, lines, time, ramp), (session.DirectionDegrees, session.LineCount, session.Time, session.RampFramesElapsed));
        Assert.Equal(velocities, session.Particles.ToArray().Select(p => (p.VelocityX, p.VelocityY, p.Radius, p.Hue)).ToArray());
        foreach (var particle in session.Particles)
        {
            Assert.InRange(particle.X, 0, 600);
            Assert.InRange(particle.Y, 0, 80);
        }
    }

    [Fact]
    public void FreshSessions_Differ()
    {
        var first = new WaveParticlesSession(600, 80, new Random(1));
        var second = new WaveParticlesSession(600, 80, new Random(2));
        Assert.NotEqual(first.DirectionDegrees, second.DirectionDegrees);
        Assert.NotEqual(first.LineBaseHue, second.LineBaseHue);
    }

    [Fact]
    public void Tuning_IsClampedWhateverItIsHanded_AndDefaultsReproduceTheUntunedSession()
    {
        var wild = new WaveParticlesTuning(intensity: 3, speed: 9, density: -1);
        Assert.Equal((1.0, 2.0, 0.0), (wild.Intensity, wild.Speed, wild.Density));
        var nan = new WaveParticlesTuning(double.NaN, double.NaN, double.NaN);
        Assert.Equal((1.0, 1.0, 1.0), (nan.Intensity, nan.Speed, nan.Density));
        Assert.Equal(0.25, new WaveParticlesTuning(speed: 0).Speed);

        var untuned = new WaveParticlesSession(600, 80, new Random(5));
        var tuned = new WaveParticlesSession(600, 80, new Random(5), tuning: new WaveParticlesTuning(1, 1, 1));
        untuned.Advance(0.5);
        tuned.Advance(0.5);
        Assert.Equal(untuned.Time, tuned.Time);
        Assert.Equal(untuned.Particles.Length, tuned.Particles.Length);
        Assert.Equal(untuned.Particles[0].X, tuned.Particles[0].X);

        var linesOnly = new WaveParticlesSession(600, 80, new Random(5), tuning: new WaveParticlesTuning(density: 0));
        Assert.Equal(0, linesOnly.Particles.Length);
        Assert.Equal(untuned.LineCount, linesOnly.LineCount);
    }

    [Fact]
    public void FillLine_FollowsTheSessionDirectionAcrossTheWholeBuffer()
    {
        var session = new WaveParticlesSession(600, 80, new Random(9));
        session.Advance(1);
        var points = new double[session.LinePointCount * 2];
        var written = session.FillLine(0, points);

        Assert.Equal(session.LinePointCount, written);
        var dx = points[2 * (written - 1)] - points[0];
        var dy = points[(2 * (written - 1)) + 1] - points[1];
        var along = (dx * session.DirectionX) + (dy * session.DirectionY);
        Assert.True(along >= Math.Sqrt((600 * 600) + (80 * 80)), $"path spans {along}");
    }

    [Fact]
    public void LightSurface_MirrorsLightnessNotHue()
    {
        var session = new WaveParticlesSession(600, 80, new Random(4));
        var dark = session.LineColor(2, lightSurface: false);
        var light = session.LineColor(2, lightSurface: true);
        Assert.Equal(dark.Hue, light.Hue);
        Assert.Equal((0.65, 0.35), (dark.Lightness, light.Lightness));
        Assert.Equal((0.70, 0.30), (session.ParticleColor(0, false).Lightness, session.ParticleColor(0, true).Lightness));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(40)]
    public void Particles_StayInsideAfterAFrameHitch(int seed)
    {
        // Section 4 as amended in 0.12 (SP-0132 R9): a hitch of k reference frames overshoots the edge by up
        // to k steps; the step reflects it back, and the following single steps never leave the canvas.
        var session = new WaveParticlesSession(600, 80, new Random(seed));
        foreach (var k in new[] { 900.0, 1, 1, 1, 250, 1, 1, 1 })
        {
            session.AdvanceFrames(k);
            foreach (var particle in session.Particles)
            {
                Assert.InRange(particle.X, 0, session.Width);
                Assert.InRange(particle.Y, 0, session.Height);
            }
        }
    }

    [Theory]
    [InlineData(0, 1, 0.5, 255, 0, 0)]
    [InlineData(120, 1, 0.5, 0, 255, 0)]
    [InlineData(240, 1, 0.5, 0, 0, 255)]
    [InlineData(0, 0, 0.5, 128, 128, 128)]
    [InlineData(-120, 1, 0.5, 0, 0, 255)]
    public void Hsl_ConvertsToRgb(double hue, double saturation, double lightness, byte r, byte g, byte b)
    {
        Assert.Equal((r, g, b), new HslColor(hue, saturation, lightness, 1).ToRgb());
    }
}
