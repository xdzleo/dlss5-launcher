using System;
using System.Windows;
using System.Windows.Media.Animation;
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

        // Onde o clique caiu, para o painel crescer DALI.
        //
        // A escala sozinha, sempre a partir do meio, mostra um painel que vem "da tela". Movendo
        // a origem para o lado do clique, ele vem do cartao — que e a ligacao que a Apple faz e
        // que explica, sem texto nenhum, de onde aquilo saiu.
        //
        // A fracao e medida na JANELA, e nao dentro do painel: o painel tem largura maxima e fica
        // centralizado, entao a maioria dos cartoes cai fora dele e uma fracao interna grudaria
        // em 0 ou 1. Os limites de 0,15 e 0,85 existem porque origem no canto exato le como o
        // painel deslizando de fora, e nao crescendo.
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (_vm.IsDialogOpen) return;
            var p = e.GetPosition(this);
            if (ActualWidth <= 0 || ActualHeight <= 0) return;
            Painel.RenderTransformOrigin = new Point(
                Math.Clamp(p.X / ActualWidth, 0.15, 0.85),
                Math.Clamp(p.Y / ActualHeight, 0.15, 0.85));
        };

        // A entrada e a saida do modal.
        //
        // Conduzidas daqui, e nao por um DataTrigger no XAML, porque Storyboard.TargetName so
        // resolve no namescope de quem chama Begin — dentro de um Style ele nem compila.
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(MainViewModel.IsDialogOpen)) return;
            if (_vm.IsDialogOpen)
            {
                // Visivel ANTES de comecar: a animacao anima opacidade e escala, e um elemento
                // recolhido nao tem o que animar.
                Modal.Visibility = Visibility.Visible;
                ((Storyboard)FindResource("AbrirModal")).Begin(this, true);
            }
            else if (Modal.Visibility == Visibility.Visible)
            {
                var sb = (Storyboard)FindResource("FecharModal");
                void Fim(object? s, EventArgs _)
                {
                    sb.Completed -= Fim;
                    // So recolhe se ninguem reabriu no meio da saida — clicar noutro jogo com o
                    // modal ainda saindo e comum, e recolher depois disso deixaria a tela vazia.
                    if (!_vm.IsDialogOpen) Modal.Visibility = Visibility.Collapsed;
                }
                sb.Completed += Fim;
                sb.Begin(this, true);
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
