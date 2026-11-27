using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

internal static class HelpWindowCapture
{
    internal const string DirectoryVariable = "STREAMSPLAYER_HELP_CAPTURE_DIR";
    internal const string Argument = "render-help-capture";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal static int Run(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory) ||
            !File.Exists(Path.Combine(directory, "request.json")))
        {
            return 2;
        }

        try
        {
            if (SystemParameters.HighContrast)
            {
                throw new InvalidOperationException("Turn off Windows contrast themes for the light and dark help captures.");
            }

            var locales = JsonSerializer.Deserialize<LocaleRequest[]>(File.ReadAllText(Path.Combine(directory, "request.json")), Json)
                ?? throw new InvalidDataException("No authored locales were requested.");
            var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent();
            var shots = new List<Shot>();
            foreach (var request in locales)
            {
                var language = InterfaceLanguages.All.Single(entry => entry.DictionaryCode == request.Locale);
                LocalizationService.Apply(language.Language);
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(language.CultureCode);
                foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
                {
                    ThemeService.Apply(theme);
                    shots.Add(Capture(directory, request, theme.ToString().ToLowerInvariant()));
                }
            }

            using var result = new FileStream(Path.Combine(directory, "result.json"), FileMode.CreateNew);
            JsonSerializer.Serialize(result, new CaptureResult(ProductInfo.Version, shots), Json);
            return 0;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
                                      InvalidOperationException or ArgumentException or JsonException or
                                      System.Windows.Markup.XamlParseException)
        {
            var diagnostic = Path.Combine(directory, "error.txt");
            using var file = new FileStream(diagnostic, FileMode.CreateNew);
            using var writer = new StreamWriter(file);
            writer.WriteLine(error.Message);
            return 1;
        }
        finally
        {
            ThemeService.Shutdown();
        }
    }

    private static Shot Capture(string directory, LocaleRequest request, string theme)
    {
        var window = ExternalControlWindow.ForHelpCapture();
        try
        {
            var content = (FrameworkElement)window.Content;
            var markers = request.Markers.Select(marker => (marker.Id, Element: window.FindName(marker.Control) as FrameworkElement
                ?? throw new InvalidDataException($"No control named {marker.Control} for help marker {marker.Id}."))).ToArray();
            var width = (int)window.Width;
            const int height = 760;
            var size = new Size(width, height);
            window.Content = null;
            var viewport = new Border
            {
                Background = window.Background,
                FlowDirection = window.FlowDirection,
                Language = window.Language,
                Child = content
            };
            viewport.Measure(size);
            viewport.Arrange(new Rect(size));
            viewport.UpdateLayout();

            var bounds = markers.Select(marker => GetMarker(marker.Id, marker.Element, viewport, size)).ToArray();
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(viewport);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var filename = $"shot-external-control-{request.Locale}-{theme}.png";
            using var output = new FileStream(Path.Combine(directory, filename), FileMode.CreateNew);
            encoder.Save(output);
            return new Shot(request.Locale, theme, width, height, bounds);
        }
        finally
        {
            window.Close();
        }
    }

    private static Marker GetMarker(string id, FrameworkElement element, FrameworkElement viewport, Size size)
    {
        var bounds = element.TransformToAncestor(viewport).TransformBounds(new Rect(element.RenderSize));
        if (bounds.Width <= 0 || bounds.Height <= 0 || !new Rect(size).Contains(bounds))
        {
            throw new InvalidDataException($"Help marker {id} is outside the capture viewport or has no visible bounds.");
        }

        for (Visual? visual = element; visual is not null && visual != viewport; visual = VisualTreeHelper.GetParent(visual) as Visual)
        {
            if (visual is UIElement { Visibility: not Visibility.Visible } ||
                IsClippedBy(visual, viewport, bounds))
            {
                throw new InvalidDataException($"Help marker {id} is clipped or hidden in the capture viewport.");
            }
        }

        return new Marker(id, bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    private static bool IsClippedBy(Visual visual, FrameworkElement viewport, Rect bounds)
    {
        var transform = visual.TransformToAncestor(viewport);
        if (VisualTreeHelper.GetClip(visual) is { } clip &&
            !ContainsWithTolerance(transform.TransformBounds(clip.Bounds), bounds))
        {
            return true;
        }

        if (visual is FrameworkElement ancestor)
        {
            if (LayoutInformation.GetLayoutClip(ancestor) is { } layoutClip &&
                !ContainsWithTolerance(transform.TransformBounds(layoutClip.Bounds), bounds))
            {
                return true;
            }

            if (ancestor.ClipToBounds &&
                !ContainsWithTolerance(transform.TransformBounds(new Rect(ancestor.RenderSize)), bounds))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsWithTolerance(Rect container, Rect target)
    {
        container.Inflate(0.5, 0.5);
        return container.Contains(target);
    }

    private sealed record LocaleRequest(string Locale, MarkerRequest[] Markers);
    private sealed record MarkerRequest(string Id, string Control);
    private sealed record CaptureResult(string Version, IReadOnlyList<Shot> Shots);
    private sealed record Shot(string Locale, string Theme, int Width, int Height, Marker[] Markers);
    private sealed record Marker(string Id, double X, double Y, double Width, double Height);
}
