using System.Windows;
using System.Windows.Controls;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0231 (<c>UI-SIZING</c> 1.1): the settings window's two interface choices - the interface size and the input
/// profile - and the window's own response to them. Both apply at once, are remembered by
/// <see cref="UiProfileService"/>, and change only on the user's explicit choice, never on an input event.
/// </summary>
public partial class SettingsWindow
{
    // The size the window's declared dimensions currently carry. The markup declares them at the Standard size, so a
    // window opened at another size scales them once, before it is placed, and a later choice scales by the ratio.
    private double _windowScale = 1.0;

    private void InitializeInterfaceProfile()
    {
        RescaleWindow(UiProfileService.Metrics.Scale, refit: false);
        UiProfileService.Changed += UiProfile_Changed;
    }

    private void PopulateInterfaceOptions()
    {
        var sizes = new[]
        {
            new UiOption(nameof(UiInterfaceSize.Compact), LocalizationService.Get("InterfaceSizeCompact")),
            new UiOption(nameof(UiInterfaceSize.Standard), LocalizationService.Get("InterfaceSizeStandard")),
            new UiOption(nameof(UiInterfaceSize.Large), LocalizationService.Get("InterfaceSizeLarge")),
            new UiOption(nameof(UiInterfaceSize.VeryLarge), LocalizationService.Get("InterfaceSizeVeryLarge"))
        };
        InterfaceSizeBox.ItemsSource = sizes;
        InterfaceSizeBox.SelectedItem = sizes.FirstOrDefault(item => item.Value == UiProfileService.Current.Size.ToString()) ?? sizes[1];

        var inputs = new[]
        {
            new UiOption(nameof(UiInputProfile.Pointer), LocalizationService.Get("InputProfilePointer")),
            new UiOption(nameof(UiInputProfile.Touch), LocalizationService.Get("InputProfileTouch"))
        };
        InputProfileBox.ItemsSource = inputs;
        InputProfileBox.SelectedItem = inputs.FirstOrDefault(item => item.Value == UiProfileService.Current.Input.ToString()) ?? inputs[0];
    }

    private void InterfaceSizeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || InterfaceSizeBox.SelectedItem is not UiOption option)
        {
            return;
        }

        if (Enum.TryParse<UiInterfaceSize>(option.Value, out var size) && Enum.IsDefined(size))
        {
            UiProfileService.Apply(UiProfileService.Current with { Size = size });
        }
    }

    private void InputProfileBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || InputProfileBox.SelectedItem is not UiOption option)
        {
            return;
        }

        if (Enum.TryParse<UiInputProfile>(option.Value, out var input) && Enum.IsDefined(input))
        {
            UiProfileService.Apply(UiProfileService.Current with { Input = input });
        }
    }

    private void UiProfile_Changed()
    {
        if (!_closed)
        {
            RescaleWindow(UiProfileService.Metrics.Scale, refit: true);
        }
    }

    /// <summary>
    /// Brings the window's declared size and minimum size to <paramref name="scale"/>. A changed size refits the window to
    /// the work area of its monitor, so a larger interface never leaves a rectangle that runs off the screen. Selecting a
    /// page never reaches here: only an explicit choice of size resizes the window (<c>SETTINGS-NAVIGATION</c> section 4
    /// rule 3).
    /// </summary>
    private void RescaleWindow(double scale, bool refit)
    {
        var ratio = scale / _windowScale;
        if (Math.Abs(ratio - 1.0) < 1e-9)
        {
            return;
        }

        _windowScale = scale;

        // The new size is taken from the old one before the minimum moves: raising MinWidth coerces Width up to it at
        // once, and scaling that coerced value would grow the window by the ratio a second time.
        var width = Width * ratio;
        var height = Height * ratio;
        MinWidth *= ratio;
        MinHeight *= ratio;
        Width = width;
        Height = height;
        if (refit && IsLoaded)
        {
            var wanted = new ScreenRect(Left, Top, Width, Height);
            var fit = ScreenPlacement.Fit(wanted, MonitorWorkArea.Around(wanted, this), MinWidth, MinHeight);
            Left = fit.Left;
            Top = fit.Top;
            Width = fit.Width;
            Height = fit.Height;
        }
    }
}
