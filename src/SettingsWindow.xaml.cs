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
        CarregarReleases();
    }

    private IEnumerable<string> InstallDirs() =>
        _vm.Games.Select(g => g.Game.InstallDir).Where(d => d is not null).Distinct()!;

    // ------------------------------------------------------------------ DLSS 5

    /// <summary>
    /// A versao exata de cada peca, e nao "na biblioteca" ou "158 MB".
    ///
    /// Quantidade de bytes nao responde "qual versao esta instalada?", e foi essa pergunta sem
    /// resposta na tela que fez a caçada do Feeder que derrubava o jogo demorar o que demorou:
    /// para descobrir o que estava na pasta, foi preciso ler o log do proprio add-on.
    ///
    /// Duas versoes convivem em cada arquivo: a que o add-on escreve sobre si (v4.7) e a do PE
    /// (0.2026.828.517). Elas nao sao a mesma coisa e as duas aparecem — a primeira e a que o
    /// autor usa para falar da build, a segunda e a que distingue duas builds do mesmo v4.7.
    /// </summary>
    private static string VersaoDe(string path, string? interna = null)
    {
        var pe = NeuralUpliftService.ReadFileVersion(path);
        if (interna is not null && pe is not null && !pe.StartsWith(interna, StringComparison.Ordinal))
            return $"v{interna} · {pe}";
        if (interna is not null) return $"v{interna}";
        return pe ?? L.T("Settings_Pill_Unknown");
    }

    private void Refresh()
    {
        var v = NeuralUpliftService.ReadAddonVersion(NeuralUpliftService.LibraryAddon);
        var temAddon = File.Exists(NeuralUpliftService.LibraryAddon);
        AddonVersionPill.Text = temAddon
            ? VersaoDe(NeuralUpliftService.LibraryAddon, v)
            : L.T("Settings_Pill_Missing");
        AddonVersionText.Text = temAddon ? L.T("Settings_Addon_Current") : L.T("Settings_Addon_None");

        var temPonte = File.Exists(NeuralUpliftService.LibraryBridge);
        BridgePill.Text = temPonte ? VersaoDe(NeuralUpliftService.LibraryBridge) : L.T("Settings_Pill_Missing");
        BridgeText.Text = temPonte ? L.T("Settings_Bridge_Have") : L.T("Settings_Bridge_None");

        var temFeeder = FeederService.VersaoNaBiblioteca() is not null;
        FeederPill.Text = temFeeder ? FeederService.VersaoNaBiblioteca()! : L.T("Settings_Pill_Missing");
        FeederText.Text = L.T("Settings_Feeder_State", FeederService.TagPadrao);
        var anterior = FeederService.VersaoAnterior();
        FeederRollbackBtn.IsEnabled = anterior is not null;
        FeederRollbackBtn.ToolTip = anterior is not null
            ? L.T("Settings_Feeder_RollbackTo", anterior) : L.T("Settings_Feeder_NoRollback");

        var temRuntime = File.Exists(NeuralUpliftService.LibraryRuntime);
        var daComunidade = temRuntime && NeuralUpliftService.RuntimeIsCommunityBuild;
        RuntimePill.Text = temRuntime
            ? $"{VersaoDe(NeuralUpliftService.LibraryRuntime)} · {new FileInfo(NeuralUpliftService.LibraryRuntime).Length / (1024 * 1024)} MB"
            : L.T("Settings_Pill_Missing");
        RuntimeText.Text = temRuntime ? L.T("Settings_Runtime_Have") : L.T("Settings_Runtime_None");

        // O visto verde afirma "a NVIDIA assinou". Com um build da comunidade isso deixa de ser
        // verdade, e manter o mesmo icone seria o launcher mentindo sobre o que instalou.
        RuntimeWarn.Visibility = daComunidade ? Visibility.Visible : Visibility.Collapsed;
        RuntimeIcon.Data = (System.Windows.Media.Geometry)FindResource(daComunidade ? "IconWarning" : "IconCheck");
        RuntimeIcon.Fill = (Brush)FindResource(daComunidade ? "AccentBrush" : "GreenBrush");
    }

    /// <summary>
    /// Preenche as duas listas de release, uma vez por abertura da janela.
    ///
    /// Fora da thread da interface e sem bloquear a abertura: sao duas paginas do GitHub, e a
    /// janela nao pode esperar a rede para aparecer. Falhou (offline, repositorio fora do ar),
    /// a lista fica vazia e os botoes desligados — o resto da tela continua servindo.
    /// </summary>
    private async void CarregarReleases()
    {
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RenoDXLauncher/1.0");
        await Task.WhenAll(
            Encher(BridgeReleases, BridgeInstall, NeuralUpliftService.BridgeRepoPublico, http, null),
            Encher(FeederReleases, FeederInstall, FeederService.RepoPublico, http, FeederService.TagPadrao));

        static async Task Encher(System.Windows.Controls.ComboBox combo, System.Windows.Controls.Button botao, string repo,
                                 System.Net.Http.HttpClient http, string? preferida)
        {
            var releases = await GitHubReleaseService.ListarReleasesAsync(http, repo);
            combo.Items.Clear();
            foreach (var r in releases)
                // O beta continua na lista, com o nome dele na frente: escondê-lo seria decidir
                // pela pessoa o que ela pode instalar. O que o launcher nao faz e escolher um
                // sozinho — ver GitHubReleaseService.EhPreRelease.
                combo.Items.Add(r.PreRelease ? $"{r.Tag}   ({L.T("Settings_Releases_Beta")})" : r.Tag);

            // O que ja vem selecionado NUNCA e um beta. A lista comeca pela release mais nova, e
            // com "a primeira" pre-escolhida bastava um clique em Instalar para pousar um beta na
            // maquina — que e exatamente o acidente que este launcher acabou de consertar.
            // Preferida (a versao fixada), senao a primeira estavel, senao a primeira.
            var alvo = preferida is null ? -1
                     : releases.FindIndex(r => string.Equals(r.Tag, preferida, StringComparison.OrdinalIgnoreCase));
            if (alvo < 0) alvo = releases.FindIndex(r => !r.PreRelease);
            combo.SelectedIndex = combo.Items.Count == 0 ? -1 : Math.Max(alvo, 0);
            botao.IsEnabled = combo.Items.Count > 0;
        }
    }

    /// <summary>A tag por tras do item escolhido — o rotulo pode trazer o aviso de beta junto.</summary>
    private static string? TagEscolhida(System.Windows.Controls.ComboBox combo) =>
        combo.SelectedItem is string s && s.Length > 0 ? s.Split(' ')[0] : null;

    private async void OnInstallBridgeRelease(object sender, RoutedEventArgs e)
    {
        if (TagEscolhida(BridgeReleases) is not { } tag) return;
        BridgeInstall.IsEnabled = false;
        try
        {
            var n = await NeuralUpliftService.UpdateBridgeAsync(InstallDirs(), null, default, tag);
            BridgeText.Text = n >= 0 ? L.T("Settings_Bridge_Done", n) : L.T("Settings_Releases_Same");
        }
        catch (Exception ex) { BridgeText.Text = ex.Message; }
        finally { BridgeInstall.IsEnabled = true; Refresh(); }
    }

    private async void OnInstallFeederRelease(object sender, RoutedEventArgs e)
    {
        if (TagEscolhida(FeederReleases) is not { } tag) return;
        FeederInstall.IsEnabled = false;
        try
        {
            // UpdateAsync, e nao FetchAsync: alem de trocar a biblioteca ele leva a versao a
            // todo jogo que ja tem o Feeder. Trocar so a biblioteca faria a tela mostrar uma
            // versao e o jogo carregar outra.
            // A escolha e gravada DEPOIS do update, e a ordem importa: o backup que o update faz
            // guarda a tag que estava valendo, e gravar antes fazia o "anterior" nascer com a
            // tag nova — voltar devolvia os arquivos certos e a configuracao errada.
            var n = await FeederService.UpdateAsync(InstallDirs(), null, default, tag);
            FeederService.LembrarEscolha(tag);
            FeederText.Text = n >= 0
                ? L.T("Settings_Feeder_Installed", FeederService.VersaoNaBiblioteca() ?? tag, n)
                : L.T("Settings_Releases_Same");
        }
        catch (Exception ex) { FeederText.Text = ex.Message; }
        finally { FeederInstall.IsEnabled = true; Refresh(); }
    }

    private void OnFeederRollback(object sender, RoutedEventArgs e)
    {
        var antes = FeederService.VersaoAnterior();
        if (FeederService.VoltarParaAnterior() is null)
        {
            FeederText.Text = L.T("Settings_Feeder_NoRollback");
            return;
        }
        // Voltar tambem tem de chegar aos jogos: e justamente quando um deles quebrou que se
        // aperta este botao.
        var n = FeederService.EspalharDaBiblioteca(InstallDirs());
        FeederText.Text = L.T("Settings_Feeder_Installed", antes ?? "?", n);
        Refresh();
    }

    /// <summary>
    /// Traz um runtime que o usuario achou. Aceita o build da comunidade — o que tem os kernels
    /// recompilados para Ada — e por isso NAO exige assinatura da NVIDIA aqui.
    ///
    /// O que ele exige e que o arquivo seja plausivelmente o runtime: o nome certo e o tamanho
    /// certo. E, quando a assinatura nao fecha, diz isso na cara em vez de instalar em silencio.
    /// </summary>
    private void OnImportRuntime(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = L.T("Settings_Runtime_Pick"),
            Filter = "nvngx_dlssnr.dll|nvngx_dlssnr*.dll|DLL|*.dll",
            CheckFileExists = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\Downloads",
        };
        if (dlg.ShowDialog() != true) return;

        // Copia intermediaria com o nome certo, quando o arquivo escolhido veio renomeado. Vive
        // so durante o import: sao 158 MB, e uma copia esquecida a cada import era o que enchia
        // o disco em silencio.
        string? staged = null;
        try
        {
            // O seletor aceita "nvngx_dlssnr (2).dll" — o nome que o navegador da a uma segunda
            // copia — mas o import exige o nome exato. Copiar para um intermediario com o nome
            // certo evita mandar a pessoa renomear arquivo para satisfazer o programa.
            //
            // Na pasta de cache do app, e nao em %TEMP%: e a regra do projeto (ver
            // AppPaths.CacheDir) — DLL de 158 MB aparecendo em %TEMP% e o par de atributos que
            // antivirus pontua como "binario suspeito".
            var origem = dlg.FileName;
            if (!Path.GetFileName(origem).Equals("nvngx_dlssnr.dll", StringComparison.OrdinalIgnoreCase))
            {
                staged = Path.Combine(AppPaths.CacheDir, "import", "nvngx_dlssnr.dll");
                Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                File.Copy(origem, staged, overwrite: true);
                origem = staged;
            }

            NeuralUpliftService.ImportRuntime(origem);
            Refresh();
            Say(L.T(NeuralUpliftService.RuntimeIsCommunityBuild
                        ? "Settings_Runtime_DoneCommunity"
                        : "Settings_Runtime_Done"),
                ok: !NeuralUpliftService.RuntimeIsCommunityBuild);
        }
        catch (Exception ex) { Say(ex.Message, false); }
        finally
        {
            // O ImportRuntime ja copiou o arquivo para a biblioteca (ou falhou antes); a copia
            // intermediaria e nossa, nao do usuario, e o original dele fica onde estava.
            if (staged is not null)
            {
                try { File.Delete(staged); }
                catch (Exception ex) { Log.Warn($"settings: copia intermediaria do runtime nao apagada: {ex.Message}"); }
            }
        }
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
