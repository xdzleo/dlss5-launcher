using System;
using System.Windows;
using RenoDXLauncher.ViewModels;

namespace RenoDXLauncher;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    /// <summary>
    /// A barra de titulo da janela, escura, e o fundo Mica do Windows 11.
    ///
    /// A barra vinha do sistema em BRANCO por cima de um app inteiro escuro — a primeira coisa
    /// que a pessoa ve ao abrir, e a unica parte da janela que nao era nossa. Sao dois atributos
    /// do DWM, e nao ha versao gerenciada deles.
    ///
    /// O Mica pinta o fundo da janela com o papel de parede desfocado pelo proprio compositor.
    /// Nao da para imita-lo em WPF (nao existe desfoque do que esta ATRAS da janela), e e ele
    /// que faz a tonalidade do painel mudar conforme a area de trabalho por baixo. Onde nao
    /// existe — Windows 10, ou build anterior a 22621 — a chamada devolve erro e o fundo fica o
    /// gradiente que ja pintamos: nada quebra, so nao ha Mica.
    /// </summary>
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmSystemBackdropType = 38;
    private const int BackdropMica = 2;

    private void AplicarVidroDaJanela()
    {
        try
        {
            var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (h == IntPtr.Zero) return;
            var ligado = 1;
            DwmSetWindowAttribute(h, DwmUseImmersiveDarkMode, ref ligado, sizeof(int));
            var mica = BackdropMica;
            DwmSetWindowAttribute(h, DwmSystemBackdropType, ref mica, sizeof(int));
        }
        catch (Exception ex) { Services.Log.Warn($"dwm: {ex.Message}"); }
    }

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        // Antes do primeiro quadro: aplicado depois, a barra pisca em branco.
        SourceInitialized += (_, _) => AplicarVidroDaJanela();
        Loaded += async (_, _) =>
        {
            await _vm.LoadAsync();
            // Depois da lista de jogos, nunca antes: a checagem fala com a rede e o aviso dela
            // nao vale atrasar a tela que a pessoa abriu o app para ver.
            await _vm.CheckLauncherUpdateAsync();
        };
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

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow(_vm) { Owner = this };
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
