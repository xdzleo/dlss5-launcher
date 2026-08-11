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

    /// <summary>Open a link that came from a mod note in the default browser. The URLs come from
    /// the RenoDX wiki and the curated index, so they are opened as-is — but only http(s), so a
    /// malformed entry can never turn into a local command.</summary>
    private void OnNoteLinkNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        e.Handled = true;
        var url = e.Uri?.ToString();
        if (url is null) return;
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            Services.Log.Warn($"link de nota ignorado (esquema inesperado): {url}");
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { Services.Log.Warn($"abrir link da nota: {ex.Message}"); }
    }
}
