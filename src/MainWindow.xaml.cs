using System.Windows;
using RenoDXLauncher.ViewModels;

namespace RenoDXLauncher;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Loaded += async (_, _) => await _vm.LoadAsync();
    }

    private void OnProfileClick(object sender, RoutedEventArgs e)
    {
        var win = new ProfileWindow(_vm) { Owner = this };
        win.ShowDialog();
    }

    private void OnGuideClick(object sender, RoutedEventArgs e)
    {
        var win = new GuideWindow { Owner = this };
        win.Show();
    }
}
