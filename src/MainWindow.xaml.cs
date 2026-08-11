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
        // Esc fecha o modal do jogo (comportamento esperado de qualquer diálogo)
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape && _vm.IsDialogOpen)
            {
                _vm.IsDialogOpen = false;
                e.Handled = true;
            }
        };
    }

    private void OnProfileClick(object sender, RoutedEventArgs e)
    {
        var win = new ProfileWindow(_vm) { Owner = this };
        win.ShowDialog();
    }

    private void OnHistoryClick(object sender, RoutedEventArgs e)
    {
        var item = _vm.Selected;
        if (item?.Mod is null) return;
        new HistoryWindow(item.Mod, item.Name) { Owner = this }.ShowDialog();
    }

    private void OnGuideClick(object sender, RoutedEventArgs e)
    {
        var win = new GuideWindow { Owner = this };
        win.Show();
    }
}
