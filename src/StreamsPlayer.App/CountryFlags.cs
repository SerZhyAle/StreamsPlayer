using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace StreamsPlayer.App;

/// <summary>
/// The flag bitmap for a country code, from the <c>Assets\Flags</c> resources.
/// </summary>
/// <remarks>
/// A bitmap and not a flag character: Windows draws no flag emoji, only the two letters of the code.
/// A flag denotes a country, so it is shown for a country and never for a language (<c>ICON-EXTERNAL</c>
/// section 4 rule 6). <c>ru.png</c> and <c>by.png</c> are the white-blue-white and white-red-white
/// bitmaps the owner chose on purpose (<c>STREAM-BANK</c> section 5.1: RU and BY use a custom bitmap);
/// they replace the state flags of the pack on purpose. The rest come from the flag-icons set
/// (THIRD-PARTY-NOTICES.txt). A code without a bitmap answers <c>null</c> and is drawn bare.
/// </remarks>
internal static class CountryFlags
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The frozen flag for <paramref name="code"/>, or <c>null</c> when this build has none.</summary>
    public static ImageSource? For(string? code)
    {
        if (code is not { Length: 2 } || !char.IsAsciiLetter(code[0]) || !char.IsAsciiLetter(code[1]))
        {
            return null;
        }

        if (Cache.TryGetValue(code, out var cached))
        {
            return cached;
        }

        ImageSource? flag = null;
        try
        {
            // A relative resource path, which is what LaunchAddressSourceTests lets the App construct; the
            // application resolves it against its own resources, never an address a stream comes from.
            // BitmapImage.UriSource does not resolve a relative path on its own, so the stream is opened here.
            var resource = Application.GetResourceStream(
                new Uri($"Assets/Flags/{code.ToLowerInvariant()}.png", UriKind.Relative));
            if (resource is not null)
            {
                using var stream = resource.Stream;
                var image = new BitmapImage();
                image.BeginInit();
                image.StreamSource = stream;
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                image.Freeze();
                flag = image;
            }
        }
        catch (Exception exception)
        {
            // No bitmap for this code: the option is drawn without one rather than failing the facet. Logged
            // once per code (the cache below), never shown - a flag is decoration.
            HandlerBoundary.Report(nameof(CountryFlags), exception, notifyUser: false);
        }

        Cache[code] = flag;
        return flag;
    }
}
