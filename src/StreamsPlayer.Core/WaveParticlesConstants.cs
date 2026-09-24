namespace StreamsPlayer.Core;

/// <summary>
/// Every number of the portfolio's "particles and lines" backdrop, as <c>WAVE-PARTICLES section 3</c>
/// fixes it (contract version 0.10). One home: the session reads these and nothing else, and a value
/// that ever has to differ is an exception row in the contract registry, not an edit here (rule 18).
/// </summary>
public static class WaveParticlesConstants
{
    // WAVE-PARTICLES section 3.1 - clock.
    public const double ReferenceFrameSeconds = 1.0 / 60.0;
    public const double TimeStep = 0.002;
    public const int RampFrames = 36;

    // WAVE-PARTICLES section 3.2 - session rolls. Integer ranges are inclusive.
    public const int FullLineCountMin = 5;
    public const int FullLineCountMax = 12;
    public const int ReducedLineCountMin = 3;
    public const int ReducedLineCountMax = 6;
    public const double SampleStepBase = 20;
    public const double SampleStepJitterMin = 0.8;
    public const double SampleStepJitterMax = 1.2;
    public const double StrokeWidthMin = 3;
    public const double StrokeWidthMax = 6;
    public const double AmplitudeFractionMin = 0.28;
    public const double AmplitudeFractionMax = 0.48;
    public const int FullParticleCountMin = 15;
    public const int FullParticleCountMax = 55;
    public const int ReducedParticleCountMin = 6;
    public const int ReducedParticleCountMax = 20;
    public const double SpeedMultiplierMin = 0.5;
    public const double SpeedMultiplierMax = 1.5;

    // WAVE-PARTICLES section 3.2 - per particle.
    public const double ParticleRadiusMin = 1;
    public const double ParticleRadiusMax = 6;
    public const double ParticleSpeedBase = 0.12;
    public const double ParticleSpeedSpread = 0.42;
    public const double CounterDriftProbability = 0.18;
    public const double CounterDriftFactor = -0.35;
    public const double ParticleJitter = 0.28;

    // WAVE-PARTICLES section 3.3 - palettes. Named palettes draw their particle base as
    // centre + (U - 0.5) * NamedPaletteParticleBaseSpread and spread their particles by NamedPaletteSpread.
    public const double DynamicLineHueStepMin = 8;
    public const double DynamicLineHueStepMax = 20;
    public const double DynamicParticleSpread = 108;
    public const double NamedLineHueStepMin = 2;
    public const double NamedLineHueStepMax = 6;
    public const double NamedPaletteParticleBaseSpread = 30;
    public const double NamedPaletteSpread = 30;
    public const double GreenLineHueMin = 95;
    public const double GreenLineHueMax = 130;
    public const double GreenParticleHueCentre = 115;
    public const double PinkLineHueMin = 305;
    public const double PinkLineHueMax = 335;
    public const double PinkParticleHueCentre = 320;
    public const double BlueLineHueMin = 200;
    public const double BlueLineHueMax = 230;
    public const double BlueParticleHueCentre = 215;

    // WAVE-PARTICLES section 3.4 - frame.
    public const byte DarkWashLevel = 10;
    public const byte LightWashLevel = 245;
    public const double WashAlpha = 38.0 / 255.0;
    public const double LineSaturation = 0.80;
    public const double DarkLineLightness = 0.65;
    public const double LightLineLightness = 0.35;
    public const double LineOpacityBase = 0.28;
    public const double LineOpacityGain = 0.16;
    public const double ParticleSaturation = 0.90;
    public const double DarkParticleLightness = 0.70;
    public const double LightParticleLightness = 0.30;
    public const double ParticleOpacityBase = 0.38;
    public const double ParticleOpacityGain = 0.32;
    public const double OpacityScale = 0.70;
    public const double CentreDriftRate = 0.45;
    public const double CentreDriftFraction = 0.02;
    public const double LaneSpacingFraction = 0.038;
    public const double PathSpanSteps = 6;
    public const double SpatialFrequency = 0.0105;
    public const double LanePhaseStep = 0.8;
    public const double EnvelopeBase = 0.40;
    public const double EnvelopeGain = 0.60;
    public const double EnvelopeRate = 0.4;
    public const double EnvelopeLanePhase = 0.2;

    // WAVE-PARTICLES section 4 - the ramp's gain runs from RampGainStart to 1.
    public const double RampGainStart = 0.35;

    // WAVE-PARTICLES section 3.5 - tuning bounds; every default is 1.
    public const double IntensityMin = 0;
    public const double IntensityMax = 1;
    public const double SpeedMin = 0.25;
    public const double SpeedMax = 2;
    public const double DensityMin = 0;
    public const double DensityMax = 1;
}
