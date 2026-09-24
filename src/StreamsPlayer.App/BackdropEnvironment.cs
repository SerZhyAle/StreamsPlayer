using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using StreamsPlayer.Core;
using Windows.System.Power;

namespace StreamsPlayer.App;

/// <summary>
/// The system states the animated backdrop answers to (SP-0110): high contrast (no backdrop at all - a
/// product placement decision the contract leaves to us) and energy saver (<c>WAVE-PARTICLES</c> rule 11).
/// Each is read live and re-announced on change, so flipping a Windows setting reaches a running backdrop
/// without a restart. Windows "Animation effects" is deliberately not among them - the app's own switch
/// governs motion (owner decision 2026-09-23, a dated exception to rule 9 in the contract registry).
/// </summary>
/// <remarks>
/// Static because each of these is one machine-wide fact; the subscriptions are made once, on the UI
/// thread, and power events - which Windows raises on a worker thread - are marshalled back to it.
/// </remarks>
internal static class BackdropEnvironment
{
    private static bool _initialized;
    private static bool _energySaverOn;
    private static bool _onBattery;
    private static Dispatcher? _dispatcher;

    /// <summary>Raised on the UI thread whenever any of the states below may have changed.</summary>
    public static event EventHandler? Changed;

    public static bool HighContrast => SystemParameters.HighContrast;

    /// <summary>
    /// Rule 11 mapped onto the one power-saving switch Windows exposes: a decorative backdrop freezes as
    /// soon as energy saver is on; an ambient one only when energy saver is on and there is no mains
    /// power - the stricter of the two states Windows can report.
    /// </summary>
    public static bool PowerFreezes(WaveParticlesIntent intent) =>
        intent == WaveParticlesIntent.Decorative ? _energySaverOn : _energySaverOn && _onBattery;

    public static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _dispatcher = Dispatcher.CurrentDispatcher;
        SystemParameters.StaticPropertyChanged += SystemParameters_StaticPropertyChanged;
        try
        {
            ReadPower();
            PowerManager.EnergySaverStatusChanged += PowerManager_Changed;
            PowerManager.PowerSupplyStatusChanged += PowerManager_Changed;
        }
        catch (Exception exception) when (exception is TypeLoadException or PlatformNotSupportedException
            or COMException or InvalidOperationException or NotSupportedException)
        {
            // Without the power API the backdrop simply never freezes for power, which is the behaviour
            // of every build before this one.
            _energySaverOn = false;
            _onBattery = false;
        }
    }

    private static void ReadPower()
    {
        _energySaverOn = PowerManager.EnergySaverStatus == EnergySaverStatus.On;
        _onBattery = PowerManager.PowerSupplyStatus == PowerSupplyStatus.NotPresent;
    }

    private static void SystemParameters_StaticPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemParameters.HighContrast))
        {
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    private static void PowerManager_Changed(object? sender, object e) =>
        _dispatcher?.BeginInvoke(() =>
        {
            try
            {
                ReadPower();
            }
            catch (COMException)
            {
                return;
            }

            Changed?.Invoke(null, EventArgs.Empty);
        });
}
