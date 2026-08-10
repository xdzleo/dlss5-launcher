using System.Windows;
using RenoDXLauncher.ViewModels;

namespace RenoDXLauncher;

public partial class ProfileWindow : Window
{
    private readonly MainViewModel _vm;

    public ProfileWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
        };
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
        PeakSlider.Value = vm.Config.PeakNits;
        GameSlider.Value = vm.Config.GameNits;
        UiSlider.Value = vm.Config.UiNits;
        ApplyOnInstall.IsChecked = vm.Config.ApplyProfileOnInstall;
        UpdateTexts();
    }

    private void OnAnyValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateTexts();

    private void UpdateTexts()
    {
        if (PeakText is null) return; // during InitializeComponent
        PeakText.Text = $"{PeakSlider.Value:0} nits";
        GameText.Text = $"{GameSlider.Value:0} nits";
        UiText.Text = $"{UiSlider.Value:0} nits";
    }

    private void OnPeakPreset(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button b && double.TryParse(b.Content as string, out var v))
            PeakSlider.Value = v;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _vm.SaveDisplayProfile(PeakSlider.Value, GameSlider.Value, UiSlider.Value, ApplyOnInstall.IsChecked == true);
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
