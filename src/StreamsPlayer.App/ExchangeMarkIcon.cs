using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0234, <c>EXCHANGE-MARK</c>: the coloured mark of the exchange server, drawn from the vendored PNG of the
/// exact size the slot and the screen scale ask for (rule 5, <see cref="ExchangeMarkSteps"/>) - never a bitmap
/// stretched up, never a likeness drawn here (rule 1). Under a Windows high-contrast theme it gives way to the
/// one-tone glyph in the theme's text colour (rule 3).
/// </summary>
/// <remarks>
/// It stands beside text that already names the server (the term of <c>DEVICE-EXCHANGE</c> item CW), so it is
/// decorative and absent from UI Automation (rule 14 gives an <em>image-only</em> mark the name; this product has
/// none). It is never mirrored: a right-to-left window mirrors its layout, not this picture (rule 2), which is why
/// the element pins its own flow direction. Both pictures are children rather than a template so that a row built
/// in code can create one with a single constructor call.
/// </remarks>
public sealed class ExchangeMarkIcon : Grid
{
    /// <summary>The slot's size in device-independent pixels: 16 for a marker in a line, 24 beside a heading.</summary>
    public static readonly DependencyProperty DipSizeProperty = DependencyProperty.Register(
        nameof(DipSize), typeof(double), typeof(ExchangeMarkIcon),
        new PropertyMetadata(16.0, (d, _) => ((ExchangeMarkIcon)d).Refresh()));

    private static readonly ConcurrentDictionary<int, BitmapSource> Frames = new();

    private readonly Image _mark = new DecorativeImage { Stretch = Stretch.Uniform };
    private readonly Viewbox _glyph = new() { Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed };

    public ExchangeMarkIcon()
    {
        FlowDirection = FlowDirection.LeftToRight;
        Focusable = false;
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Left;
        RenderOptions.SetBitmapScalingMode(_mark, BitmapScalingMode.HighQuality);

        var path = new System.Windows.Shapes.Path { Stretch = Stretch.Uniform, Width = 24, Height = 24 };
        path.SetResourceReference(Shape.FillProperty, "TextBrush");
        if (Application.Current?.TryFindResource("Icon.source.exchange-server") is Geometry geometry)
        {
            path.Data = geometry;
        }

        _glyph.Child = path;
        Children.Add(_mark);
        Children.Add(_glyph);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        Refresh();
    }

    public double DipSize
    {
        get => (double)GetValue(DipSizeProperty);
        set => SetValue(DipSizeProperty, value);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Refresh();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) =>
        SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;

    private void OnSystemParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemParameters.HighContrast))
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        Width = DipSize;
        Height = DipSize;
        var highContrast = SystemParameters.HighContrast;
        _glyph.Visibility = highContrast ? Visibility.Visible : Visibility.Collapsed;
        _mark.Visibility = highContrast ? Visibility.Collapsed : Visibility.Visible;
        if (highContrast)
        {
            return;
        }

        var scale = IsLoaded ? VisualTreeHelper.GetDpi(this).PixelsPerDip : 1.0;
        try
        {
            _mark.Source = Frames.GetOrAdd(ExchangeMarkSteps.For(DipSize, scale), Load);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or NotSupportedException)
        {
            // A mark that cannot be read is left out, never a window that cannot open: it is decoration, and the
            // term beside it still names the server. Logged, not shown.
            HandlerBoundary.Report(nameof(ExchangeMarkIcon), exception, notifyUser: false);
        }
    }

    /// <summary>An image outside the UI Automation tree: a screen reader would otherwise announce an unnamed "image".</summary>
    private sealed class DecorativeImage : Image
    {
        protected override AutomationPeer? OnCreateAutomationPeer() => null;
    }

    private static BitmapSource Load(int step)
    {
        // A relative resource path, which LaunchAddressSourceTests lets the App construct; BitmapImage.UriSource does
        // not resolve one on its own, so the stream is opened here, as CountryFlags does.
        var resource = Application.GetResourceStream(new Uri(ExchangeMarkSteps.ResourcePath(step), UriKind.Relative))
            ?? throw new InvalidOperationException($"The exchange mark step {step} is not compiled into the application.");
        using var stream = resource.Stream;
        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = stream;
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
