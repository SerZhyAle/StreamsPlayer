using System.Windows;
using System.Windows.Media;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0113: the glyph a button draws. A glyph style sets it to one of the geometries in
/// <c>Glyphs.xaml</c> (an <c>ICON-SET</c> meaning, a pending one, or chrome), and the glyph templates in
/// <c>App.xaml</c> bind their path to it - so a style names its meaning in one setter, and a style swap
/// at run time swaps the glyph with it.
/// </summary>
/// <remarks>
/// A property rather than a resource alias: WPF cannot alias a merged-dictionary geometry inside a
/// style's own resources (a <c>StaticResource</c> element there fails to load), and a per-style copy of
/// the figures is what the vocabulary exists to prevent.
/// </remarks>
public static class Glyph
{
    public static readonly DependencyProperty GeometryProperty = DependencyProperty.RegisterAttached(
        "Geometry", typeof(Geometry), typeof(Glyph), new FrameworkPropertyMetadata(null));

    public static Geometry? GetGeometry(DependencyObject element) => (Geometry?)element.GetValue(GeometryProperty);

    public static void SetGeometry(DependencyObject element, Geometry? value) => element.SetValue(GeometryProperty, value);
}
