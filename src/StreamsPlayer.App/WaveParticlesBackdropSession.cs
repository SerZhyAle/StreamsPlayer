using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// One listening session's backdrop (SP-0110): the <c>WAVE-PARTICLES</c> motion model plus the buffer the
/// trail accumulates in (rule 4). It belongs to the playing station, not to a surface, which is what lets
/// the same animation move from the catalog card into the compact panel and back.
/// </summary>
/// <remarks>
/// <para>The buffer is two render targets used in turn: each frame draws the previous one, the wash, the
/// lines and the particles into the other. Nothing is blurred and nothing is cleared, so a particle leaves
/// the fading tail the contract describes.</para>
/// <para>Lengths are buffer pixels, and the buffer is sized in device-independent pixels, which rule 14
/// allows: on a scaled display it is stretched to fill, which costs sharpness and nothing about speed.</para>
/// </remarks>
public sealed class WaveParticlesBackdropSession
{
    private const double Dpi = 96;

    private readonly DrawingVisual _visual = new();
    private ImageSource? _inherited;
    private WaveParticlesSession? _model;
    private RenderTargetBitmap? _front;
    private RenderTargetBitmap? _back;
    private double[] _points = [];
    private Pen[] _linePens = [];
    private Brush[] _particleBrushes = [];
    private (double Gain, bool Light) _paletteKey = (-1, false);
    private Color? _lastWash;
    private bool _isRunning;

    /// <param name="channelId">The station this session belongs to; a resume of it keeps the session.</param>
    /// <param name="previous">
    /// The session this one replaces, whose last frame becomes this one's starting picture, so a station
    /// change washes the old animation out under the new one instead of cutting to an empty surface.
    /// </param>
    public WaveParticlesBackdropSession(Guid channelId, WaveParticlesBackdropSession? previous)
    {
        ChannelId = channelId;
        _inherited = previous?._front ?? previous?._inherited;
    }

    public Guid ChannelId { get; }

    /// <summary>True while the station plays; a stopped station keeps its session frozen on the last frame.</summary>
    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (_isRunning == value)
            {
                return;
            }

            _isRunning = value;
            RunningChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? RunningChanged;

    /// <summary>The last presented frame, or null before the first one.</summary>
    public ImageSource? Frame => _front;

    public int FramesDrawn { get; private set; }

    /// <summary>
    /// Makes the buffer the given size. The first call seeds it with the wash colour (and the previous
    /// session's picture, stretched); a later size change carries the current picture into the new size
    /// and re-seeds only the particles (rule 12) - never an empty field, never a second ramp.
    /// </summary>
    public void EnsureBuffer(int width, int height, Color wash)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (_model is null)
        {
            _model = new WaveParticlesSession(width, height);
        }

        if (_front is not null && _front.PixelWidth == width && _front.PixelHeight == height)
        {
            return;
        }

        var carried = _front ?? _inherited;
        _inherited = null;
        _front = new RenderTargetBitmap(width, height, Dpi, Dpi, PixelFormats.Pbgra32);
        _back = new RenderTargetBitmap(width, height, Dpi, Dpi, PixelFormats.Pbgra32);
        var bounds = new Rect(0, 0, width, height);
        using (var dc = _visual.RenderOpen())
        {
            dc.DrawRectangle(Solid(wash, 1), null, bounds);
            if (carried is not null)
            {
                dc.DrawImage(carried, bounds);
            }
        }

        _front.Render(_visual);
        _model.Resize(width, height);
        _points = new double[_model.LinePointCount * 2];

        // SP-0162: _lastWash names the colour the picture was drawn against, and a carried picture was not
        // drawn against this one. Recording `wash` here made EnsureStill believe a frozen frame from a dark
        // card already suited the light compact panel it had just been resized for, so the old surface's
        // picture stayed under the new one (WAVE-PARTICLES rule 7, SP-0110).
        if (carried is null)
        {
            _lastWash = wash;
        }
    }

    /// <summary>Advances by real elapsed time and draws one frame.</summary>
    public void Step(double elapsedSeconds, int width, int height, Color wash, bool lightSurface)
    {
        EnsureBuffer(width, height, wash);
        Draw(_model!.Advance(elapsedSeconds), wash, lightSurface);
    }

    /// <summary>
    /// Rule 9: when motion is not allowed the surface shows a settled still frame, never an empty one. A
    /// session that has drawn is simply held; one that never has - or whose surface changed colour under
    /// it - gets the contract's still frame: a fully washed buffer and 36 passes of one reference frame.
    /// </summary>
    public void EnsureStill(int width, int height, Color wash, bool lightSurface)
    {
        EnsureBuffer(width, height, wash);
        if (FramesDrawn > 0 && _lastWash == wash)
        {
            return;
        }

        using (var dc = _visual.RenderOpen())
        {
            dc.DrawRectangle(Solid(wash, 1), null, new Rect(0, 0, _front!.PixelWidth, _front.PixelHeight));
        }

        _front!.Render(_visual);
        for (var pass = 0; pass < WaveParticlesConstants.RampFrames; pass++)
        {
            Draw(_model!.AdvanceFrames(1), wash, lightSurface);
        }
    }

    private void Draw(double washAlpha, Color wash, bool lightSurface)
    {
        var model = _model!;
        var bounds = new Rect(0, 0, _front!.PixelWidth, _front.PixelHeight);
        RefreshPalette(model, lightSurface);
        using (var dc = _visual.RenderOpen())
        {
            dc.DrawImage(_front, bounds);
            dc.DrawRectangle(Solid(wash, washAlpha), null, bounds);
            for (var lane = 0; lane < model.LineCount; lane++)
            {
                var count = model.FillLine(lane, _points);
                if (count < 2)
                {
                    continue;
                }

                var geometry = new StreamGeometry();
                using (var context = geometry.Open())
                {
                    context.BeginFigure(new Point(_points[0], _points[1]), false, false);
                    for (var i = 1; i < count; i++)
                    {
                        context.LineTo(new Point(_points[2 * i], _points[(2 * i) + 1]), true, true);
                    }
                }

                geometry.Freeze();
                dc.DrawGeometry(null, _linePens[lane], geometry);
            }

            var particles = model.Particles;
            for (var i = 0; i < particles.Length; i++)
            {
                dc.DrawEllipse(_particleBrushes[i], null, new Point(particles[i].X, particles[i].Y), particles[i].Radius, particles[i].Radius);
            }
        }

        _back!.Clear();
        _back.Render(_visual);
        (_front, _back) = (_back, _front);
        _lastWash = wash;
        FramesDrawn++;
    }

    // Colours change only with the ramp's gain and the surface's lightness, so once a session has settled
    // its pens and brushes are built once rather than on every frame.
    private void RefreshPalette(WaveParticlesSession model, bool lightSurface)
    {
        var key = (Math.Round(model.Gain, 3), lightSurface);
        if (key == _paletteKey && _linePens.Length == model.LineCount && _particleBrushes.Length == model.Particles.Length)
        {
            return;
        }

        _paletteKey = key;
        _linePens = new Pen[model.LineCount];
        for (var lane = 0; lane < _linePens.Length; lane++)
        {
            var pen = new Pen(Solid(model.LineColor(lane, lightSurface)), model.StrokeWidth)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            pen.Freeze();
            _linePens[lane] = pen;
        }

        _particleBrushes = new Brush[model.Particles.Length];
        for (var i = 0; i < _particleBrushes.Length; i++)
        {
            _particleBrushes[i] = Solid(model.ParticleColor(i, lightSurface));
        }
    }

    private static SolidColorBrush Solid(HslColor colour)
    {
        var (r, g, b) = colour.ToRgb();
        return Solid(Color.FromRgb(r, g, b), colour.Alpha);
    }

    private static SolidColorBrush Solid(Color colour, double alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), colour.R, colour.G, colour.B));
        brush.Freeze();
        return brush;
    }
}
