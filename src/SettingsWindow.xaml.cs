using System.IO;
using System.Windows;
using System.Windows.Media;
using RenoDXLauncher.Localization;
using RenoDXLauncher.Services;
using RenoDXLauncher.ViewModels;

namespace RenoDXLauncher;

/// <summary>
/// Ajustes do launcher: tudo o que vale para todos os jogos e nao pertence a nenhum.
///
/// Sao dois assuntos — as pecas compartilhadas do DLSS 5 e a calibracao da tela. O segundo tinha
/// janela propria ("My Monitor") e virou secao daqui: eram dois botoes na barra para a mesma
/// pergunta, "onde eu mexo nas configuracoes".
///
/// Nao ha botao de salvar. Os cartoes de importar agem no clique, e os controles da tela gravam
/// na hora que sao mexidos; um "Salvar" que valesse so para metade da janela seria uma armadilha.
///
/// Das tres pecas do DLSS 5, duas tem botao de atualizar e a terceira nao, de proposito: no
/// runtime nao ha o que escolher — ou a NVIDIA assinou, ou ele nao entra.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _pronta;

    public SettingsWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
        };
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };

        PeakSlider.Value = vm.Config.PeakNits;
        ApplyOnInstall.IsChecked = vm.Config.ApplyProfileOnInstall;
        PopulateLanguages();
        // so agora: preencher os controles dispara os eventos deles, e gravar durante a montagem
        // reescreveria a config com o que ainda nao foi lido.
        _pronta = true;
        UpdateTexts();
        Refresh();
    }

    private IEnumerable<string> InstallDirs() =>
        _vm.Games.Select(g => g.Game.InstallDir).Where(d => d is not null).Distinct()!;

    // ------------------------------------------------------------------ DLSS 5

    private void Refresh()
    {
        var v = NeuralUpliftService.ReadAddonVersion(NeuralUpliftService.LibraryAddon);
        var temAddon = File.Exists(NeuralUpliftService.LibraryAddon);
        AddonVersionPill.Text = v is not null ? $"v{v}"
                              : temAddon ? L.T("Settings_Pill_Unknown")
                              : L.T("Settings_Pill_Missing");
        AddonVersionText.Text = temAddon ? L.T("Settings_Addon_Current") : L.T("Settings_Addon_None");

        var temPonte = File.Exists(NeuralUpliftService.LibraryBridge);
        BridgePill.Text = temPonte ? L.T("Settings_Pill_Have") : L.T("Settings_Pill_Missing");
        BridgeText.Text = temPonte ? L.T("Settings_Bridge_Have") : L.T("Settings_Bridge_None");

        var temRuntime = File.Exists(NeuralUpliftService.LibraryRuntime);
        RuntimePill.Text = temRuntime
            ? $"{new FileInfo(NeuralUpliftService.LibraryRuntime).Length / (1024 * 1024)} MB"
            : L.T("Settings_Pill_Missing");
        RuntimeText.Text = temRuntime ? L.T("Settings_Runtime_Have") : L.T("Settings_Runtime_None");
    }

    private void Say(string texto, bool ok)
    {
        StatusBox.Visibility = Visibility.Visible;
        StatusBox.Background = new SolidColorBrush(ok ? Color.FromRgb(0x1C, 0x2E, 0x24)
                                                      : Color.FromRgb(0x33, 0x20, 0x22));
        StatusBox.BorderBrush = (Brush)FindResource(ok ? "GreenBrush" : "RedBrush");
        StatusText.Foreground = (Brush)FindResource(ok ? "GreenBrush" : "RedBrush");
        StatusText.Text = texto;
    }

    /// <summary>Escolhe um .addon64 no disco. Null quando o usuario desiste.</summary>
    private string? Pick(string titulo)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = titulo,
            Filter = "ReShade addon (*.addon64)|*.addon64",
            CheckFileExists = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                               + @"\Downloads",
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    /// <summary>
    /// Guarda o addon baixado na biblioteca e o leva a todos os jogos.
    ///
    /// A validacao nao e formalidade: um arquivo errado aqui nao produz erro nenhum dentro do
    /// jogo — ele simplesmente nao faz nada, que e o sintoma mais caro de diagnosticar deste
    /// projeto inteiro.
    /// </summary>
    private void OnImportAddon(object sender, RoutedEventArgs e)
    {
        if (Pick(L.T("Settings_Addon_Pick")) is not { } caminho) return;
        try
        {
            NeuralUpliftService.ImportAddon(caminho);
            var n = NeuralUpliftService.PropagateAddon(InstallDirs());
            Refresh();
            Say(L.T("Settings_Addon_Done", n), true);
        }
        catch (Exception ex) { Say(ex.Message, false); }
    }

    private void OnImportBridge(object sender, RoutedEventArgs e)
    {
        if (Pick(L.T("Settings_Bridge_Pick")) is not { } caminho) return;
        try
        {
            var n = NeuralUpliftService.ImportBridge(caminho, InstallDirs());
            Refresh();
            Say(L.T("Settings_Bridge_Done", n), true);
        }
        catch (Exception ex) { Say(ex.Message, false); }
    }

    // ------------------------------------------------------------------ tela e idioma

    /// <summary>
    /// Item do seletor. Publico de proposito: DisplayMemberPath e SelectedValuePath resolvem por
    /// reflexao, e WPF falha em silencio com tipo nao-publico.
    ///
    /// ToString e sobrescrito porque DisplayMemberPath sozinho nao basta: ele vale para a lista
    /// aberta, mas a caixa fechada usa o ContentPresenter do template do ComboBox, e o nosso nao
    /// herda o ItemTemplate — sem isto o idioma escolhido aparecia como
    /// "LanguageChoice { Tag = , Nome = ... }".
    /// </summary>
    public sealed record LanguageChoice(string Tag, string Nome)
    {
        public override string ToString() => Nome;
    }

    private void PopulateLanguages()
    {
        var itens = new List<LanguageChoice> { new("", L.T("Settings_Language_System")) };
        foreach (var (tag, nativo) in L.Available)
            itens.Add(new LanguageChoice(tag, nativo));

        LanguageBox.ItemsSource = itens;
        LanguageBox.SelectedValue = _vm.Config.Language ?? "";
    }

    private void OnLanguageChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_pronta) return;
        if (LanguageBox.SelectedValue is not string tag) return;

        var escolhido = string.IsNullOrEmpty(tag) ? null : tag;
        if (escolhido == _vm.Config.Language) return;

        _vm.Config.Language = escolhido;
        _vm.Config.Save();
        // A interface toda esta ligada por binding ao indexador de L, entao a troca aparece na
        // hora, sem fechar a janela.
        L.SetLanguage(escolhido);
        UpdateTexts();
        Refresh();
    }

    private void OnAnyValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateTexts();
        Salvar();
    }

    private void OnApplyOnInstallChanged(object sender, RoutedEventArgs e) => Salvar();

    private void UpdateTexts()
    {
        if (PeakText is null) return; // durante o InitializeComponent
        PeakText.Text = L.T("Common_NitsValue", PeakSlider.Value.ToString("0"));
    }

    /// <summary>
    /// Grava o perfil. Os nits de jogo e de interface nao aparecem mais aqui — sao ajuste fino
    /// que se faz olhando a cena, dentro do jogo pelo Home — entao o que ja estava gravado neles
    /// e repassado intacto: sumir da tela nao e motivo para zerar o valor de alguem.
    /// </summary>
    private void Salvar()
    {
        if (!_pronta) return;
        _vm.SaveDisplayProfile(PeakSlider.Value, _vm.Config.GameNits, _vm.Config.UiNits,
                               ApplyOnInstall.IsChecked == true);
    }

    private void OnPeakPreset(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button b && double.TryParse(b.Content as string, out var v))
            PeakSlider.Value = v;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
