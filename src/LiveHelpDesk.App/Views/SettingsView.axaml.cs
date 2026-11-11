using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Media;
using LiveHelpDesk.App.Converters;
using LiveHelpDesk.App.ViewModels;

namespace LiveHelpDesk.App.Views;

public partial class SettingsView : UserControl
{
    // The colour each picker shows while its setting is blank ("automatic"): the overlay's own defaults.
    private static readonly Color AutomaticBackground = Color.Parse("#F59E0B");
    private static readonly Color AutomaticText = Color.Parse("#1F1300");
    private static readonly Color AutomaticAccent = Color.Parse("#5C3D00");

    private SettingsViewModel? _vm;
    private bool _syncing;

    public SettingsView()
    {
        InitializeComponent();
        Wire(BackgroundPicker, AutomaticBackground, vm => vm.OverlayBackground, (vm, hex) => vm.OverlayBackground = hex);
        Wire(TextPicker, AutomaticText, vm => vm.OverlayTextColor, (vm, hex) => vm.OverlayTextColor = hex);
        Wire(AccentPicker, AutomaticAccent, vm => vm.OverlayAccent, (vm, hex) => vm.OverlayAccent = hex);
        DataContextChanged += (_, _) => Attach(DataContext as SettingsViewModel);
    }

    private void Attach(SettingsViewModel? vm)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnSettingChanged;
        }

        _vm = vm;
        if (vm is not null)
        {
            vm.PropertyChanged += OnSettingChanged;
        }

        ShowColours();
    }

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.OverlayBackground) or nameof(SettingsViewModel.OverlayTextColor)
            or nameof(SettingsViewModel.OverlayAccent))
        {
            ShowColours();
        }
    }

    private void ShowColours()
    {
        if (_vm is not { } vm)
        {
            return;
        }

        _syncing = true;
        try
        {
            BackgroundPicker.Color = HexColor.Parse(vm.OverlayBackground, AutomaticBackground);
            TextPicker.Color = HexColor.Parse(vm.OverlayTextColor, AutomaticText);
            AccentPicker.Color = HexColor.Parse(vm.OverlayAccent, AutomaticAccent);
        }
        finally
        {
            _syncing = false;
        }
    }

    // Only a colour that differs from the one in use is a choice: the picker also reports colours it sets on itself
    // (when its template loads), and those must not turn "automatic" into a fixed colour.
    private void Wire(ColorPicker picker, Color automatic, Func<SettingsViewModel, string> get, Action<SettingsViewModel, string> set) =>
        picker.ColorChanged += (_, e) =>
        {
            if (_syncing || _vm is not { } vm)
            {
                return;
            }

            var hex = HexColor.ToHex(e.NewColor);
            if (hex != HexColor.ToHex(HexColor.Parse(get(vm), automatic)))
            {
                set(vm, hex);
            }
        };
}
