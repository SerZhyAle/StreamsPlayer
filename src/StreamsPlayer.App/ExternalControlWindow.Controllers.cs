using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public partial class ExternalControlWindow
{
    private readonly Dictionary<string, ControllerRow> _controllerRows = new(StringComparer.Ordinal);

    private void RefreshControllers(RemoteControlSettings settings)
    {
        var listed = settings.Listening ? Target.Controllers : [];
        var ids = listed.Select(device => device.DeviceId).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _controllerRows.Keys.Where(id => !ids.Contains(id)).ToArray())
        {
            ControllersList.Children.Remove(_controllerRows[id].View);
            _controllerRows.Remove(id);
        }

        ControllersState.Visibility = listed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ControllersState.SetResourceReference(TextBlock.TextProperty,
            settings.Listening ? "RemoteControllersEmpty" : "RemoteControllersOff");
        foreach (var device in listed)
        {
            var grant = settings.GrantFor(device.DeviceId);
            if (!_controllerRows.TryGetValue(device.DeviceId, out var row))
            {
                row = new ControllerRow(this, device.DeviceId, grant is not null);
                _controllerRows.Add(device.DeviceId, row);
                ControllersList.Children.Add(row.View);
            }

            row.Update(device, grant);
        }
    }

    private bool ConfirmAlways(string deviceName)
    {
        var dialog = new Window
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.Height,
            Width = 430,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            FlowDirection = FlowDirection
        };
        dialog.SetResourceReference(Window.TitleProperty, "RemoteAlwaysTitle");
        dialog.SetResourceReference(Window.BackgroundProperty, "WindowBackground");
        var content = new StackPanel { Margin = new Thickness(18) };
        content.Children.Add(new TextBlock
        {
            Text = LocalizationService.Format("RemoteAlwaysBody", deviceName),
            TextWrapping = TextWrapping.Wrap
        });
        var answers = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var allow = new Button { Margin = new Thickness(0, 0, 8, 0) };
        allow.SetResourceReference(FrameworkElement.StyleProperty, "ConfirmAnswerGlyphButton");
        allow.SetResourceReference(ContentControl.ContentProperty, "RemoteConsentAlways");
        allow.Click += (_, _) => dialog.DialogResult = true;
        var cancel = new Button { IsDefault = true, IsCancel = true };
        cancel.SetResourceReference(FrameworkElement.StyleProperty, "CancelAnswerGlyphButton");
        cancel.SetResourceReference(ContentControl.ContentProperty, "RemoteCancel");
        answers.Children.Add(allow);
        answers.Children.Add(cancel);
        content.Children.Add(answers);
        dialog.Content = content;
        dialog.Loaded += (_, _) => cancel.Focus();
        return dialog.ShowDialog() == true;
    }

    private sealed class ControllerRow
    {
        private readonly ExternalControlWindow _host;
        private readonly string _id;
        private readonly TextBlock _heading = new() { TextWrapping = TextWrapping.Wrap };
        private readonly CheckBox _allowed = new();
        private readonly StackPanel _body = new();
        private readonly StackPanel _classes = new() { Margin = new Thickness(0, 6, 0, 0) };
        private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        private readonly Dictionary<string, ComboBox> _choices = new(StringComparer.Ordinal);
        private string _name = string.Empty;
        private bool _syncing;

        internal ControllerRow(ExternalControlWindow host, string id, bool expanded)
        {
            _host = host;
            _id = id;
            View = new Expander { Header = _heading, Content = _body, IsExpanded = expanded, Margin = new Thickness(0, 0, 0, 6) };
            _allowed.SetResourceReference(ContentControl.ContentProperty, "RemoteAllowDevice");
            _allowed.Click += Allowed_Click;
            _error.SetResourceReference(TextBlock.TextProperty, "RemoteSaveFailed");
            _body.Children.Add(_allowed);
            _body.Children.Add(_classes);
            _body.Children.Add(_error);
            foreach (var risk in DisplayedClasses)
            {
                AddClass(risk);
            }
        }

        internal Expander View { get; }

        internal void Update(ExchangeDeviceRecord device, RemoteControllerGrant? grant)
        {
            _name = device.DeviceName;
            var presence = device.IsOnline ? LocalizationService.Get("RemoteOnline") :
                device.LastSeen is { } seen
                    ? LocalizationService.Format("RemoteOfflineSince", RemoteControlText.Time(seen))
                    : LocalizationService.Get("RemoteOffline");
            _heading.Text = $"{_name} · {RemoteControlText.Program(device.Product)} · {presence}";
            AutomationProperties.SetName(_allowed, $"{_allowed.Content} - {_name}");
            _syncing = true;
            _allowed.IsChecked = grant is not null;
            _classes.Visibility = grant is null ? Visibility.Collapsed : Visibility.Visible;
            foreach (var (risk, choice) in _choices)
            {
                choice.SelectedIndex = grant?.ConsentFor(risk) switch
                {
                    RemoteConsent.Ask => 1,
                    RemoteConsent.Always => 2,
                    _ => 0
                };
            }

            _syncing = false;
        }

        private void AddClass(string risk)
        {
            var name = RemoteControlText.ClassName(risk);
            var heading = new TextBlock { Text = name, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
            var summaries = string.Join(" ", RemoteCommands.Where(command => command.Risk == risk).Select(RemoteControlText.Summary));
            // Leave room for the truncation mark even when the viewer is at minimum width.
            var shortSummaries = summaries.Length > 100 ? summaries[..98].TrimEnd() + ".." : summaries;
            var description = new TextBlock { Text = shortSummaries, ToolTip = summaries, TextTrimming = TextTrimming.CharacterEllipsis };
            description.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            var choice = new ComboBox { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 160, Margin = new Thickness(0, 3, 0, 8) };
            AutomationProperties.SetName(choice, name);
            foreach (var key in new[] { "RemoteConsentNone", "RemoteConsentAsk", "RemoteConsentAlways" })
            {
                var item = new ComboBoxItem();
                item.SetResourceReference(ContentControl.ContentProperty, key);
                choice.Items.Add(item);
            }

            choice.SelectionChanged += (_, _) =>
                HandlerBoundary.Run(nameof(ChangeClassAsync), () => ChangeClassAsync(risk, choice.SelectedIndex));
            _choices.Add(risk, choice);
            _classes.Children.Add(heading);
            _classes.Children.Add(description);
            _classes.Children.Add(choice);
        }

        private void Allowed_Click(object sender, RoutedEventArgs e) =>
            HandlerBoundary.Run(nameof(Allowed_Click), SetAllowedAsync);

        private async Task SetAllowedAsync()
        {
            var allowed = _allowed.IsChecked == true;
            await SaveAsync(settings => allowed && _host.Target.IsController(_id)
                ? settings.WithGrant(RemoteControllerGrant.Ticked(_id, _name))
                : settings.WithoutGrant(_id));
        }

        private async Task ChangeClassAsync(string risk, int selected)
        {
            if (_syncing || selected < 0)
            {
                return;
            }

            var consent = selected switch
            {
                1 => RemoteConsent.Ask,
                2 => RemoteConsent.Always,
                _ => null
            };
            if (consent == RemoteConsent.Always && risk != RemoteRiskClass.Safe && !_host.ConfirmAlways(_name))
            {
                _host.RefreshState();
                return;
            }

            await SaveAsync(settings => _host.Target.IsController(_id) && settings.GrantFor(_id) is { } grant
                ? settings.WithGrant(grant.With(risk, consent))
                : settings);
        }

        private async Task SaveAsync(Func<RemoteControlSettings, RemoteControlSettings> change)
        {
            _body.IsEnabled = false;
            var saved = await _host.Target.UpdateSettingsAsync(change, "external-control");
            if (!_host._closed)
            {
                _body.IsEnabled = true;
                _error.Visibility = saved ? Visibility.Collapsed : Visibility.Visible;
                _host.RefreshState();
            }
        }
    }
}
