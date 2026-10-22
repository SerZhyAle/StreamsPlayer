namespace StreamsPlayer.App;

/// <summary>
/// SP-0234, <c>EXCHANGE-MARK</c> rule 5: a window with no SVG renderer takes the PNG of the size it needs, and
/// may scale one <em>down</em> to a size between two held steps, never up. This is the one place that says which
/// held step a slot of a given size on a given screen scale takes; it knows nothing of WPF so a test can hold it.
/// </summary>
internal static class ExchangeMarkSteps
{
    /// <summary>
    /// The pixel sizes of the coloured mark this product holds, ascending (<c>assets/exchange-mark/png/mark-N.png</c>).
    /// 16 and 24 device-independent pixels at 100, 125, 150, 175 and 200 per cent are 16/20/24/28/32 and
    /// 24/30/36/42/48; 40, 56, 64 and 96 are the next steps for a larger scale.
    /// </summary>
    internal static readonly int[] Held = [16, 20, 24, 28, 30, 32, 36, 40, 42, 48, 56, 64, 96];

    /// <summary>
    /// The smallest held step that is at least as large as the slot in device pixels, so a PNG is only ever
    /// scaled down. A slot past the largest step takes the largest (the mark is not offered that large here).
    /// </summary>
    /// <param name="slot">The slot's size in device-independent pixels.</param>
    /// <param name="pixelsPerDip">The screen scale: 1.0 at 100 per cent, 1.25 at 125 per cent.</param>
    internal static int For(double slot, double pixelsPerDip)
    {
        var pixels = slot * (pixelsPerDip > 0 ? pixelsPerDip : 1.0);
        // A hair of tolerance: 24 * 1.25 is 30.000000000000004 in floating point and must still be 30.
        var wanted = (int)Math.Ceiling(pixels - 1e-6);
        foreach (var step in Held)
        {
            if (step >= wanted)
            {
                return step;
            }
        }

        return Held[^1];
    }

    /// <summary>The step's compiled-in resource, relative to the application (the csproj links the vendored file there).</summary>
    internal static string ResourcePath(int step) => $"Assets/ExchangeMark/mark-{step}.png";
}
