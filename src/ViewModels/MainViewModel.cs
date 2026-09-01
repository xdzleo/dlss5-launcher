using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using RenoDXLauncher;
using RenoDXLauncher.Localization;
using RenoDXLauncher.Models;
using RenoDXLauncher.Services;

namespace RenoDXLauncher.ViewModels;

public class MainViewModel : ObservableObject
{
    private readonly CatalogService _catalog = new();
    private readonly RhiManifestService _rhi = new();
    private readonly DlssIndexService _dlssIndex = new();
    private readonly ReShadeService _reshade = new();
    private ManifestService? _manifest;
    private List<CatalogEntry> _catalogEntries = new();

    /// <summary>Bumped whenever the selection changes; async detail loads bail out when stale.</summary>
    private int _detailToken;
    /// <summary>The item the current Settings/ExeCandidates belong to — save operations target
    /// THIS item, never the live Selected (which may have changed mid-flight).</summary>
    private GameItemVm? _detailItem;
    private CancellationTokenSource? _backgroundCts;

    public LauncherConfig Config { get; private set; } = new();
    public ObservableCollection<GameItemVm> Games { get; } = new();
    public ICollectionView GamesView { get; }

    public MainViewModel()
    {
        GamesView = CollectionViewSource.GetDefaultView(Games);
        GamesView.Filter = FilterGame;
        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(forceRefresh: true), () => !Busy);
        InstallCommand = new AsyncRelayCommand(InstallAsync, () => !Busy && Selected?.Mod?.DownloadUrl != null);
        ToggleCommand = new AsyncRelayCommand(ToggleAsync, () => !Busy && Selected?.IsInstalled == true);
        RemoveCommand = new AsyncRelayCommand(RemoveAsync, () => !Busy && Selected?.IsInstalled == true);
        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync, () => !Busy && Settings.Count > 0);
        ApplyProfileCommand = new AsyncRelayCommand(ApplyProfileAsync, () => !Busy && Settings.Count > 0);
        ResetSettingsCommand = new AsyncRelayCommand(ResetSettingsAsync, () => !Busy && Settings.Count > 0);
        OpenFolderCommand = new RelayCommand(OpenFolder, () => Selected != null);
        OpenNexusCommand = new RelayCommand(OpenNexus,
            () => Selected?.Mod?.NexusUrl != null || Selected?.Mod?.InfoUrl != null);
        AddManualGameCommand = new AsyncRelayCommand(AddManualGameAsync, () => !Busy);
        CheckUpdatesCommand = new AsyncRelayCommand(CheckUpdatesAsync, () => !Busy);
        CloseDialogCommand = new RelayCommand(() => IsDialogOpen = false);
        DlssFixCommand = new AsyncRelayCommand(ToggleDlssFixAsync, () => !Busy);
        NeuralCommand = new AsyncRelayCommand(ToggleNeuralAsync, () => !Busy && NeuralBlocker is null);
        Dlss5Command = new AsyncRelayCommand(ToggleDlss5Async, () => !Busy);
        // A guarda de download veio junto do banner que este interruptor substituiu. Sem ela, um
        // mod so-Nexus parecia operavel: o clique instalava o ReShade na pasta e SO ENTAO falhava
        // por nao ter download direto. Desinstalar ja instalado nao precisa de download.
        ModCommand = new AsyncRelayCommand(ToggleModAsync,
            () => !Busy && Selected?.HasMod == true
                  && (Selected?.IsInstalled == true || Selected?.HasDirectDownload == true));
        // O Reparo NAO pode depender de haver mod no catalogo: quem chega ao estado que ele
        // conserta — ReShade sem suporte a add-ons — costuma ser justamente um jogo sem entrada de
        // catalogo, com DLSS 5 instalado. Ligado no InstallCommand ele ficava permanentemente
        // cinza, no unico lugar da tela que oferecia caminho de volta.
        RepairReShadeCommand = new AsyncRelayCommand(RepairReShadeAsync, () => !Busy && NeedsRepair);

        // Trocar de idioma retraduz na hora tudo que passa por {loc:Tr}, porque essas bindings
        // observam L.Instance. O que NAO passa por la sao as propriedades que a view model
        // calcula chamando L.T(...) — os rotulos do filtro, os selos dos cartoes, a barra de
        // status, os textos LIGADO/DESLIGADO. Sem isto a janela ficava meio traduzida ate
        // reiniciar, apesar de dois comentarios no codigo afirmarem o contrario.
        L.Instance.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(FilterOptions));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(ModStateText));
            OnPropertyChanged(nameof(Dlss5StateText));
            OnPropertyChanged(nameof(DlssButtonText));
            OnPropertyChanged(nameof(NeuralButtonText));
            foreach (var g in Games) g.RaiseLocalizedText();
        };
        ImportNeuralRuntimeCommand = new AsyncRelayCommand(ImportNeuralRuntimeAsync, () => !Busy);
        DlssCommand = new AsyncRelayCommand(ToggleDlssAsync, () => !Busy);
        DlssRepairCommand = new AsyncRelayCommand(RepairDlssAsync, () => !Busy);
        RestoreAllDlssCommand = new AsyncRelayCommand(RestoreAllDlssAsync, () => !Busy);
        LaunchGameCommand = new RelayCommand(LaunchGame, () => Selected != null);
        OpenMaintainerCommand = new RelayCommand(OpenMaintainer,
            () => AvatarService.ProfileUrl(Selected?.Mod) != null);
        UpdateAllCommand = new AsyncRelayCommand(UpdateAllAsync, () => !Busy && UpdateCount > 0);
        UpdateLauncherCommand = new AsyncRelayCommand(UpdateLauncherAsync, () => !Busy && _launcherUpdate != null);
    }

    // ---------------------------------------------------------------- launcher self-update

    private LauncherRelease? _launcherUpdate;
    public AsyncRelayCommand UpdateLauncherCommand { get; }

    /// <summary>A release nova do proprio launcher, quando existe. Vira o aviso de topo.</summary>
    public LauncherRelease? LauncherUpdate
    {
        get => _launcherUpdate;
        private set
        {
            if (!Set(ref _launcherUpdate, value)) return;
            OnPropertyChanged(nameof(HasLauncherUpdate));
            OnPropertyChanged(nameof(LauncherUpdateTitle));
            OnPropertyChanged(nameof(LauncherUpdateDetail));
            RaiseCommands();
        }
    }

    public bool HasLauncherUpdate => _launcherUpdate != null;

    public string LauncherUpdateTitle =>
        _launcherUpdate is { } r ? L.T("Update_Banner_Title", r.Version.ToString()) : "";

    public string LauncherUpdateDetail =>
        _launcherUpdate is { } r
            ? L.T("Update_Banner_Detail", LauncherUpdateService.Current.ToString(),
                  r.Size > 0 ? $"{r.Size / (1024 * 1024)} MB" : "?")
            : "";

    /// <summary>
    /// Checagem silenciosa na abertura. Quem instala mod sozinho nao volta ao repositorio para
    /// conferir versao, entao a versao nova precisa vir ate a pessoa. So aparece se houver uma.
    /// </summary>
    public async Task CheckLauncherUpdateAsync()
    {
        var r = await LauncherUpdateService.CheckAsync();
        if (r != null) LauncherUpdate = r;
    }

    /// <summary>
    /// Baixa a release nova, confere o hash e entrega ao instalador.
    ///
    /// A partir do RunSetup o launcher esta com os dias contados — o Inno fecha esta janela para
    /// trocar os arquivos. Por isso a mensagem final e escrita antes, e nao depois.
    /// </summary>
    private async Task UpdateLauncherAsync()
    {
        if (_launcherUpdate is not { } rel) return;
        ActionBusy = true;
        try
        {
            var progresso = new Progress<string>(t => StatusText = t);
            var setup = await LauncherUpdateService.DownloadAsync(rel, progresso);
            StatusText = L.T("Update_Installing", rel.Version.ToString());
            if (LauncherUpdateService.RunSetup(setup))
            {
                LauncherUpdate = null;
            }
            else
            {
                StatusText = L.T("Update_Failed", L.T("Update_Cancelled"));
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"launcher update: {ex}");
            StatusText = L.T("Update_Failed", ex.Message);
        }
        finally { ActionBusy = false; }
    }

    // ---------- top-level state ----------

    private string _statusText = L.T("Main_Status_Ready");
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    // Flags SEPARADAS: o finally de uma ação (Install) não pode zerar o single-flight do
    // LoadAsync — isso deixava duas cargas concorrentes cancelarem o enriquecimento da grade
    // (badges e capas sumiam até reiniciar o app).
    private bool _loading;
    private bool _actionBusy;

    private bool Loading
    {
        get => _loading;
        set
        {
            if (_loading == value) return;
            _loading = value;
            OnPropertyChanged(nameof(Busy));
            RaiseCommands();
        }
    }

    private bool ActionBusy
    {
        get => _actionBusy;
        set
        {
            if (_actionBusy == value) return;
            _actionBusy = value;
            OnPropertyChanged(nameof(Busy));
            RaiseCommands();
        }
    }

    /// <summary>Qualquer operação longa em andamento (liga a ProgressBar e trava os comandos).</summary>
    public bool Busy => _loading || _actionBusy;

    private string _search = "";
    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value)) GamesView.Refresh(); }
    }

    // Propriedade calculada, e nao array fixo: a ordem dos itens define FilterIndex, mas o
    // texto e lido do resx a cada leitura do binding, entao trocar de idioma nao deixa a
    // combo em portugues numa janela reaberta em ingles.
    public string[] FilterOptions =>
    [
        L.T("Main_Filter_All"),
        L.T("Main_Filter_HasMod"),
        L.T("Main_Filter_Installed"),
        L.T("Main_Filter_NoMod"),
    ];
    private int _filterIndex;
    public int FilterIndex
    {
        get => _filterIndex;
        set { if (Set(ref _filterIndex, value)) GamesView.Refresh(); }
    }

    private bool FilterGame(object o)
    {
        if (o is not GameItemVm g) return false;
        if (_filterIndex == 1 && !g.HasMod) return false;
        if (_filterIndex == 2 && !g.IsInstalled) return false;
        if (_filterIndex == 3 && g.HasMod) return false;
        if (_search.Length > 0 && !g.Name.Contains(_search, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>Re-run the view filter without losing the current selection.</summary>
    private void RefreshViewKeepSelection()
    {
        var sel = Selected;
        GamesView.Refresh();
        if (sel != null && GamesView.Cast<object>().Contains(sel)) Selected = sel;
    }

    // ---------- selection / detail ----------

    private GameItemVm? _selected;
    public GameItemVm? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                // O interruptor do mod le o estado do jogo selecionado; sem avisar aqui ele
                // continua mostrando o do jogo anterior.
                RaiseModState();
                if (value != null) IsDialogOpen = true;
                _ = LoadDetailSafeAsync();
                RaiseCommands();
            }
        }
    }

    public bool HasSelection => _selected != null;

    /// <summary>The game modal (cover + options) is showing. Opened by picking a tile.</summary>
    private bool _isDialogOpen;
    public bool IsDialogOpen
    {
        get => _isDialogOpen;
        set
        {
            if (!Set(ref _isDialogOpen, value)) return;
            if (!value) Selected = null;   // closing clears the selection highlight
        }
    }

    /// <summary>
    /// Qual tradutor de D3D9 este jogo usa. So aparece em jogo DX9 de 32 bits, que e onde a
    /// pergunta existe.
    ///
    /// A escolha e do usuario porque nao ha resposta certa: os dois cobrem conjuntos diferentes,
    /// e nao se contem. Resident Evil Revelations 2 so roda com DXVK; Saints Row 2 so roda com
    /// dgVoodoo2. Nao da para deduzir qual serve sem abrir o jogo — entao quem abre escolhe.
    /// </summary>
    public ObservableCollection<string> D3d9Translators { get; } =
        new() { "DXVK (Vulkan)", "dgVoodoo2 (D3D11)" };

    private bool _mostraTradutorD3d9;
    public bool MostraTradutorD3d9 { get => _mostraTradutorD3d9; set => Set(ref _mostraTradutorD3d9, value); }

    private string? _tradutorD3d9;
    public string? TradutorD3d9
    {
        get => _tradutorD3d9;
        set
        {
            if (!Set(ref _tradutorD3d9, value) || value is null || Selected is null) return;
            Config.D3d9Translator[Selected.Key] = value.StartsWith("DXVK") ? "dxvk" : "dgvoodoo";
            Config.Save();

            // Trocar o tradutor troca o d3d9.dll, o modo do ReShade (camada x proxy) e as metades
            // de 32 bits — tudo de uma vez. Antes isto so gravava a preferencia e pedia para
            // reinstalar, o que era um beco: com o DLSS 5 ja ligado, o interruptor REMOVE em vez
            // de reinstalar, entao nao havia caminho pela interface. Se ja esta instalado, a troca
            // reinstala sozinha; se nao esta, a preferencia fica guardada para a primeira vez.
            if (Dlss5Ready || FeederActive) _ = TrocarTradutorAsync();
            else DetailStatus = L.T("Main_D3d9Translator_Changed");
        }
    }

    public ObservableCollection<string> ExeCandidates { get; } = new();
    private string? _selectedExe;
    public string? SelectedExe
    {
        get => _selectedExe;
        set
        {
            if (Set(ref _selectedExe, value) && value != null && Selected != null && Selected == _detailItem)
            {
                var previousState = Selected.State;
                Selected.ChosenExe = value;
                Config.PinnedExes[Selected.Key] = value;
                Config.Save();
                if (previousState?.AddonPath != null && Selected.State?.AddonPath is null)
                    DetailStatus = L.T("Main_Detail_ModInPreviousFolder",
                        Path.GetDirectoryName(previousState.AddonPath));
                // trocar de exe troca TargetDir e AddonPath: a deteccao de NR tem de ser refeita
                // antes das settings, senao os sliders de Neural Uplift sobram (ou faltam) por
                // estarem baseados no addon da pasta anterior
                // Envolvido: solto, uma falha de I/O aqui (permissao, unidade desconectada) caia
                // numa Task que ninguem observa e sumia — sem log, sem DetailStatus, sem dialogo.
                // A mesma falha pela selecao normal E reportada, via LoadDetailSafeAsync.
                _ = SafeRefreshNeuralAsync(_detailToken);
                RaiseCommands();
            }
        }
    }

    public ObservableCollection<SettingVm> Settings { get; } = new();
    public ObservableCollection<Advice> Advice { get; } = new();
    /// <summary>Everything known about how to configure THIS game, from every source.</summary>
    public ObservableCollection<ModNote> Notes { get; } = new();

    /// <summary>Things to know BEFORE installing (required ReShade version, external download,
    /// anti-cheat). Rendered above the install button on purpose: below it they sat under the
    /// fold, so the user pressed Install without ever seeing the prerequisite.</summary>
    public ObservableCollection<ModNote> Prerequisites { get; } = new();

    /// <summary>Rules that apply to every game of this engine (the wiki's callout blocks). Kept
    /// apart because they are long and identical for hundreds of games — they belong behind a
    /// disclosure, not on top of the game's own instructions.</summary>
    public ObservableCollection<ModNote> EngineNotes { get; } = new();

    /// <summary>Verdict from ReShade.log: did the mod actually load last time the game ran?</summary>
    private string _loadVerdict = "";
    public string LoadVerdict { get => _loadVerdict; set => Set(ref _loadVerdict, value); }
    private bool _hasLoadVerdict;
    public bool HasLoadVerdict { get => _hasLoadVerdict; set => Set(ref _hasLoadVerdict, value); }

    /// <summary>
    /// O veredito e uma boa noticia ("carregou") e nao ha build nova esperando.
    ///
    /// Separa quem fala sozinho de quem espera ser chamado: um "nao carregou" precisa aparecer
    /// na hora, e um "carregou certinho" nao merece ocupar o painel toda vez que se clica em um
    /// jogo — esse vai para dentro do bloco recolhido.
    /// </summary>
    private bool _loadVerdictOk;
    public bool LoadVerdictOk
    {
        get => _loadVerdictOk;
        set { if (Set(ref _loadVerdictOk, value)) OnPropertyChanged(nameof(HasLoadProblem)); }
    }

    public bool HasLoadProblem => _hasLoadVerdict && !_loadVerdictOk;

    private bool _needsRepair;
    /// <summary>O ReShade que está na pasta é a build SEM suporte a add-ons — o mod nunca vai
    /// carregar até ser substituído. Como o banner de instalar some depois de instalado, este é
    /// o único caminho de volta para quem caiu nesse estado.</summary>
    public bool NeedsRepair { get => _needsRepair; set => Set(ref _needsRepair, value); }

    /// <summary>A correção de DLSS FG faz sentido neste jogo (mod converte SDR->HDR e o jogo
    /// tem o runtime de Frame Generation).</summary>
    /// <summary>Foto do autor do mod (GitHub). Null = mostra a inicial.</summary>
    private string? _maintainerAvatar;
    public string? MaintainerAvatar
    {
        get => _maintainerAvatar;
        set { if (Set(ref _maintainerAvatar, value)) OnPropertyChanged(nameof(HasMaintainerAvatar)); }
    }
    public bool HasMaintainerAvatar => _maintainerAvatar != null;

    private bool _showDlssFix;
    public bool ShowDlssFix { get => _showDlssFix; set => Set(ref _showDlssFix, value); }

    private bool _dlssFixApplied;
    public bool DlssFixApplied
    {
        get => _dlssFixApplied;
        set { if (Set(ref _dlssFixApplied, value)) OnPropertyChanged(nameof(DlssFixButtonText)); }
    }

    public string DlssFixButtonText =>
        L.T(_dlssFixApplied ? "Main_DlssFix_Remove" : "Main_DlssFix_Apply");

    private DlssFixService.Detection? _dlssDetection;

    /// <summary>O addon instalado sabe acionar o DLSS-NR e o jogo tem DLSS — só então o cartão
    /// aparece. Requisito de máquina (GPU/driver/runtime) não esconde o cartão: vira
    /// <see cref="NeuralBlocker"/>, porque "não apareceu nada" não explica nada a ninguém.</summary>
    private bool _showNeural;
    public bool ShowNeural { get => _showNeural; set => Set(ref _showNeural, value); }

    private bool _neuralApplied;
    public bool NeuralApplied
    {
        get => _neuralApplied;
        set { if (Set(ref _neuralApplied, value)) OnPropertyChanged(nameof(NeuralButtonText)); }
    }

    public string NeuralButtonText =>
        L.T(_neuralApplied ? "Main_Neural_Remove" : "Main_Neural_Apply");

    /// <summary>Motivo pelo qual esta máquina não pode ligar, ou null quando pode.</summary>
    private string? _neuralBlocker;
    public string? NeuralBlocker
    {
        get => _neuralBlocker;
        set { if (Set(ref _neuralBlocker, value)) OnPropertyChanged(nameof(NeuralHasBlocker)); }
    }
    public bool NeuralHasBlocker => _neuralBlocker != null;

    /// <summary>
    /// Um elo da cadeia do DLSS 5, com o estado dele.
    ///
    /// Cada elo quebrado produz o mesmo sintoma de fora — o jogo abre e nada acontece — entao um
    /// unico "ligado/desligado" nao ajuda ninguem a agir. Mostrar os elos separados e o que
    /// transforma "nao funciona" em "falta o ReShade".
    /// </summary>
    public record ChainLink(string Label, bool Ok);

    public ObservableCollection<ChainLink> Dlss5Chain { get; } = new();

    private bool _dlss5Ready;
    /// <summary>
    /// Todos os elos no lugar. E o que o interruptor reflete.
    ///
    /// A notificacao e INCONDICIONAL, e nao so quando o valor muda. Um ToggleButton move o
    /// proprio botao ao ser clicado (SetCurrentValue), entao so uma notificacao da origem o traz
    /// de volta. Quando a instalacao falha e o valor recalculado e o MESMO de antes, um `Set`
    /// que suprime a notificacao por igualdade deixa o botao na posicao que o clique produziu —
    /// mostrando LIGADO com as pastilhas vermelhas embaixo. E como o modal e reaproveitado entre
    /// os jogos, a posicao errada acompanha o proximo jogo aberto.
    /// </summary>
    public bool Dlss5Ready
    {
        get => _dlss5Ready;
        set
        {
            _dlss5Ready = value;
            OnPropertyChanged(nameof(Dlss5Ready));
            OnPropertyChanged(nameof(Dlss5StateText));
        }
    }

    public string Dlss5StateText => L.T(_dlss5Ready ? "Dlss5_State_On" : "Dlss5_State_Off");

    private bool _bridgeActive;
    /// <summary>A ponte DX11 esta em uso neste jogo. Vira o aviso no cartao.</summary>
    public bool BridgeActive
    {
        get => _bridgeActive;
        set { _bridgeActive = value; OnPropertyChanged(nameof(BridgeActive)); }
    }

    private bool _feederActive;
    /// <summary>O Feeder esta em uso neste jogo — o jogo nao tem DLSS proprio.</summary>
    public bool FeederActive
    {
        get => _feederActive;
        set { _feederActive = value; OnPropertyChanged(nameof(FeederActive)); }
    }

    /// <summary>Recolhe o estado de cada elo para os indicadores do cartao.</summary>
    private void BuildDlss5Chain(string targetDir, string iniPath, NeuralUpliftService.Detection det,
                                 string? exePath)
    {
        Dlss5Chain.Clear();
        var addon = NeuralUpliftService.DeployedGenericAddon(targetDir);
        var early = false;
        if (File.Exists(iniPath) && addon is not null)
        {
            var list = new IniFile(iniPath).Get("ADDON", "LoadFromDllMain", ignoreCase: true) ?? "";
            early = list.Split(',').Any(e => e.Trim()
                .Equals(Path.GetFileName(addon), StringComparison.OrdinalIgnoreCase));
        }

        // No caminho de 32 bits quem carrega o addon e o ReShade do host64\, com o ini DELE — o da
        // raiz nunca lista carga antecipada, porque o processo do jogo nao carrega addon de 64
        // bits. Medir so a raiz deixava este elo vermelho num jogo perfeitamente instalado.
        var iniHost64 = Path.Combine(targetDir, FeederService.Host64Dir, "ReShade.ini");
        if (!early && File.Exists(iniHost64))
        {
            var list = new IniFile(iniHost64).Get("ADDON", "LoadFromDllMain", ignoreCase: true) ?? "";
            early = list.Split(',').Any(e => e.Trim()
                .Equals("renodx-dlss5.addon64", StringComparison.OrdinalIgnoreCase));
        }
        // Num jogo de 32 bits o pass neural nao roda no processo do jogo: roda no host64\, e e LA
        // que o addon e os runtimes moram. O proprio DeployBits32Async os tira da raiz de
        // proposito — sao 271 MB que um processo de 32 bits nao carrega.
        //
        // A cadeia media so a raiz, entao um jogo de 32 bits corretamente instalado exibia os elos
        // "addon" e "neural" em vermelho para sempre, Dlss5Ready nunca virava true, e o interruptor
        // continuava dizendo "instalar" depois de instalar. O Hitman: Absolution avaliou 7200
        // frames com DLSS 5 enquanto a interface o mostrava como nao instalado.
        var host64 = Path.Combine(targetDir, FeederService.Host64Dir);
        var noHost64 = Directory.Exists(host64);
        var addonNoHost64 = noHost64 && File.Exists(Path.Combine(host64, "renodx-dlss5.addon64"));
        var runtimeNoHost64 = noHost64
            && File.Exists(Path.Combine(host64, NeuralUpliftService.RuntimeFile));

        // Em jogo Vulkan — nativo ou D3D9 traduzido pelo DXVK — o ReShade entra como CAMADA, e
        // um proxy dxgi.dll nunca e carregado. Medir so o proxy deixava este elo vermelho para
        // sempre numa instalacao correta, e como Dlss5Ready exige a cadeia inteira, o interruptor
        // continuava dizendo "instalar" depois de instalar. Foi o que apareceu no ENSLAVED: a
        // instalacao ia toda para Binaries\Win32, completa, e a interface mostrava desligado.
        var bits64Jogo = exePath is null || PeUtils.Inspect(exePath, readImports: false)?.Is64Bit != false;
        var camadaVk = VulkanLayerService.IsRegistered(targetDir, bits64Jogo);
        Dlss5Chain.Add(new ChainLink("ReShade", det.ReShadeDllName is not null || camadaVk));
        Dlss5Chain.Add(new ChainLink(L.T("Dlss5_Link_Addon"), det.AddonSupportsNr || addonNoHost64));
        Dlss5Chain.Add(new ChainLink(L.T("Dlss5_Link_Neural"), det.RuntimeDeployed || runtimeNoHost64));
        // O Ray Reconstruction so e exigido onde o jogo resolve runtimes na propria pasta. Onde
        // quem resolve e o driver, nao implantamos nada (um runtime parcial na pasta do
        // executavel quebra a resolucao do NGX) — e cobrar o arquivo aqui deixaria a cadeia
        // incompleta para sempre, num jogo que esta certo.
        var rrEsperado = NeuralUpliftService.TemRuntimeLocal(targetDir);
        Dlss5Chain.Add(new ChainLink(L.T("Dlss5_Link_Rr"),
            !rrEsperado || File.Exists(Path.Combine(targetDir, NeuralUpliftService.RayReconstructionFile))));
        Dlss5Chain.Add(new ChainLink(L.T("Dlss5_Link_EarlyLoad"), early || addon is null));
        Dlss5Chain.Add(new ChainLink(L.T("Dlss5_Link_Switch"), NeuralApplied));

        // A ponte e o Feeder entram na cadeia quando sao NECESSARIOS, com o estado que de fato
        // tem — e nao, como antes, apenas quando ja estao na pasta.
        //
        // Aquela regra tinha um buraco no unico lugar que importa: a AUSENCIA da peca, que e
        // exatamente a falha, era o estado que a cadeia nao sabia representar. Faltando a ponte,
        // nenhum elo aparecia, todos os outros ficavam verdes, o interruptor dizia "ligado" e
        // nada rodava dentro do jogo. Foi assim que a ponte do Baldur's Gate ficou renomeada para
        // .teste sem que o launcher notasse.
        FeederActive = FeederService.IsDeployed(targetDir);
        BridgeActive = NeuralUpliftService.BridgeDeployed(targetDir);

        var alcancaD3d12 = Dlss5Installer.ReachesD3D12(exePath);
        var temDlssNativo = det.HasDlss && !FeederActive;
        var pedePonte = temDlssNativo && !alcancaD3d12;
        var pedeFeeder = !temDlssNativo && FeederService.Applies(exePath, temDlssNativo, alcancaD3d12);

        if (pedePonte || BridgeActive)
            Dlss5Chain.Add(new ChainLink(L.T("Dlss5_Link_Bridge"), BridgeActive));
        if (pedeFeeder || FeederActive)
            Dlss5Chain.Add(new ChainLink(L.T("Dlss5_Link_Feeder"), FeederActive));

        // Depois de TODOS os elos, nao antes: o calculo ficava acima dos dois ultimos, entao nem
        // um elo vermelho ali derrubava o "pronto".
        Dlss5Ready = Dlss5Chain.All(l => l.Ok);
    }

    /// <summary>O runtime falta na biblioteca — o único bloqueio que o usuário resolve aqui
    /// mesmo, então ganha um botão em vez de só um aviso.</summary>
    private bool _neuralNeedsRuntime;
    public bool NeuralNeedsRuntime { get => _neuralNeedsRuntime; set => Set(ref _neuralNeedsRuntime, value); }

    private NeuralUpliftService.Detection? _neuralDetection;

    // ---------- runtimes de DLSS ----------

    /// <summary>O jogo carrega pelo menos um runtime NGX. Sem isso nao ha o que atualizar.</summary>
    private bool _showDlss;
    public bool ShowDlss { get => _showDlss; set => Set(ref _showDlss, value); }

    /// <summary>Ha backup na pasta do jogo: algum runtime ali e nosso, nao o do estudio.</summary>
    private bool _dlssApplied;
    public bool DlssApplied
    {
        get => _dlssApplied;
        set { if (Set(ref _dlssApplied, value)) OnPropertyChanged(nameof(DlssButtonText)); }
    }

    public string DlssButtonText => L.T(_dlssApplied ? "Main_Dlss_Restore" : "Main_Dlss_Update");

    /// <summary>O que o jogo tem hoje, versus o que a biblioteca pode oferecer.</summary>
    private string? _dlssSummary;
    public string? DlssSummary { get => _dlssSummary; set => Set(ref _dlssSummary, value); }

    /// <summary>Problema comprovado no estado atual dos runtimes do jogo, ou null. Aparece
    /// independente de quem causou — este launcher, o DLSS Swapper, ou troca feita a mao.</summary>
    private string? _dlssHealth;
    public string? DlssHealth
    {
        get => _dlssHealth;
        set { if (Set(ref _dlssHealth, value)) OnPropertyChanged(nameof(DlssHasIssue)); }
    }
    public bool DlssHasIssue => _dlssHealth != null;

    private string _detailStatus = "";
    public string DetailStatus { get => _detailStatus; set => Set(ref _detailStatus, value); }

    // ---------- commands ----------

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand InstallCommand { get; }
    public AsyncRelayCommand ToggleCommand { get; }
    public AsyncRelayCommand RemoveCommand { get; }
    public AsyncRelayCommand SaveSettingsCommand { get; }
    public AsyncRelayCommand ApplyProfileCommand { get; }
    public AsyncRelayCommand ResetSettingsCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand OpenNexusCommand { get; }
    public AsyncRelayCommand AddManualGameCommand { get; }
    public AsyncRelayCommand CheckUpdatesCommand { get; }
    public RelayCommand CloseDialogCommand { get; }
    public AsyncRelayCommand DlssFixCommand { get; }
    public AsyncRelayCommand NeuralCommand { get; }

    /// <summary>Instala a cadeia inteira do DLSS 5 neste jogo, ou a desinstala. Um clique.</summary>
    public AsyncRelayCommand Dlss5Command { get; }

    /// <summary>Troca um ReShade sem suporte a add-ons pela build completa. Ver o construtor.</summary>
    public AsyncRelayCommand RepairReShadeCommand { get; }

    /// <summary>
    /// O mod de HDR do jogo, num interruptor so.
    ///
    /// Sao tres estados por baixo — ausente, instalado e ligado, instalado e desligado — e o
    /// usuario so quer saber de um: esta valendo ou nao. Ligar instala se preciso e reativa se
    /// estava desativado; desligar renomeia para .disabled, que preserva a instalacao (comparar
    /// HDR ligado x desligado e o uso principal disso e nao deveria custar um download).
    /// </summary>
    public AsyncRelayCommand ModCommand { get; }

    /// <summary>O mod esta instalado E ativo. E o que o interruptor reflete.</summary>
    public bool ModReady => Selected?.IsInstalled == true && Selected?.IsEnabled == true;

    public string ModStateText => L.T(ModReady ? "Dlss5_State_On" : "Dlss5_State_Off");

    /// <summary>Relanca a leitura do estado de DLSS 5 sem deixar a falha virar Task nao observada.</summary>
    private async Task SafeRefreshNeuralAsync(int token)
    {
        try { await RefreshNeuralAndSettingsAsync(token); }
        catch (Exception ex)
        {
            Log.Warn($"neural refresh: {ex.Message}");
            DetailStatus = ex.Message;
        }
    }

    /// <summary>Reavalia o interruptor do mod. Chamado sempre que a instalacao pode ter mudado.</summary>
    private void RaiseModState()
    {
        OnPropertyChanged(nameof(ModReady));
        OnPropertyChanged(nameof(ModStateText));
    }
    public AsyncRelayCommand ImportNeuralRuntimeCommand { get; }
    public AsyncRelayCommand DlssCommand { get; }
    public AsyncRelayCommand DlssRepairCommand { get; }
    public AsyncRelayCommand RestoreAllDlssCommand { get; }
    public RelayCommand LaunchGameCommand { get; }
    public RelayCommand OpenMaintainerCommand { get; }
    public AsyncRelayCommand UpdateAllCommand { get; }

    /// <summary>Quantos mods instalados têm build mais nova disponível.</summary>
    private int _updateCount;
    public int UpdateCount
    {
        get => _updateCount;
        set
        {
            if (Set(ref _updateCount, value))
            {
                OnPropertyChanged(nameof(HasUpdates));
                OnPropertyChanged(nameof(UpdateAllText));
                RaiseCommands();
            }
        }
    }
    public bool HasUpdates => _updateCount > 0;
    public string UpdateAllText => L.T("Main_UpdateAll_Button", _updateCount);

    private void RaiseCommands()
    {
        // O interruptor do mod le estado do jogo selecionado, entao ele se reavalia junto com os
        // comandos — nao em dois ou tres lugares escolhidos a mao.
        //
        // Espalhar a chamada era o defeito: dez caminhos mudam o estado do addon e so dois
        // avisavam. O botao de energia da barra de baixo, por exemplo, desativava o mod e deixava
        // o interruptor logo acima dele dizendo LIGADO — dois controles contradizendo um ao outro
        // no mesmo painel. Aqui a regra e uma so: mexeu no estado, os comandos sao reavaliados;
        // logo o interruptor tambem.
        RaiseModState();
        RefreshCommand.RaiseCanExecuteChanged();
        AddManualGameCommand.RaiseCanExecuteChanged();
        CheckUpdatesCommand.RaiseCanExecuteChanged();
        UpdateLauncherCommand.RaiseCanExecuteChanged();
        UpdateAllCommand.RaiseCanExecuteChanged();
        InstallCommand.RaiseCanExecuteChanged();
        ToggleCommand.RaiseCanExecuteChanged();
        RemoveCommand.RaiseCanExecuteChanged();
        SaveSettingsCommand.RaiseCanExecuteChanged();
        ApplyProfileCommand.RaiseCanExecuteChanged();
        ResetSettingsCommand.RaiseCanExecuteChanged();
        OpenFolderCommand.RaiseCanExecuteChanged();
        LaunchGameCommand.RaiseCanExecuteChanged();
        DlssFixCommand.RaiseCanExecuteChanged();
        NeuralCommand.RaiseCanExecuteChanged();
        RepairReShadeCommand.RaiseCanExecuteChanged();
        // Faltar aqui nao deixa o botao "as vezes" desabilitado: o CanExecute e avaliado uma vez,
        // na construcao, quando Selected ainda e nulo — e nunca mais. O ModCommand exige
        // Selected.HasMod, entao ficava morto para sempre.
        Dlss5Command.RaiseCanExecuteChanged();
        ModCommand.RaiseCanExecuteChanged();
        ImportNeuralRuntimeCommand.RaiseCanExecuteChanged();
        DlssCommand.RaiseCanExecuteChanged();
        DlssRepairCommand.RaiseCanExecuteChanged();
        RestoreAllDlssCommand.RaiseCanExecuteChanged();
        OpenMaintainerCommand.RaiseCanExecuteChanged();
        OpenNexusCommand.RaiseCanExecuteChanged();
    }

    // ---------- load pipeline ----------

    public async Task LoadAsync(bool forceRefresh = false)
    {
        if (_loading) return; // single flight: Loaded event, Refresh and AddManualGame can overlap
        Loading = true;
        _backgroundCts?.Cancel();
        var cts = _backgroundCts = new CancellationTokenSource();
        try
        {
            StatusText = L.T("Main_Status_LoadingCatalog");
            Config = LauncherConfig.Load();
            OnPropertyChanged(nameof(Config));
            _manifest ??= await Task.Run(() => new ManifestService());
            var rhiTask = _rhi.LoadAsync();
            var indexTask = _dlssIndex.LoadAsync();
            _catalogEntries = await _catalog.LoadAsync(forceRefresh);
            AvatarService.Learn(_catalogEntries);
            await rhiTask;
            await indexTask;

            StatusText = L.T("Main_Status_ScanningGames");
            // the folder scan only surfaces dirs whose NAME matches a catalog game,
            // so standalone installs appear without polluting the grid with random folders
            var knownNames = _catalogEntries
                .SelectMany(e => e.NormalizedAliases)
                .ToHashSet(StringComparer.Ordinal);
            bool KnownGame(string folderName) =>
                knownNames.Contains(MatchService.Normalize(folderName))
                || knownNames.Contains(MatchService.Normalize(MatchService.StripEditionSuffix(folderName)));
            var games = await StoreScanners.ScanAllAsync(KnownGame);
            // a hand-picked folder is named by whoever packed it, not by the developer — the
            // resolver reads the exe and the parent folders to find out which game it is
            foreach (var dir in Config.ManualGameDirs.Where(Directory.Exists))
                games.Add(FolderGameResolver.Resolve(dir, _catalogEntries));

            Games.Clear();
            foreach (var g in games)
            {
                var match = MatchService.FindMatch(g, _catalogEntries);
                Games.Add(new GameItemVm(g, match));
            }
            var withMod = Games.Count(g => g.HasMod);
            StatusText = L.T("Main_Status_GamesFound", Games.Count, withMod);

            var ct = cts.Token;
            _ = Task.Run(() => BackgroundEnrichAsync(Games.ToList(), ct));
            _ = Task.Run(() => CheckSwappedRuntimesAsync(Games.Select(g => g.Game.InstallDir).ToList()!));
        }
        catch (Exception ex)
        {
            Log.Warn($"load: {ex}");
            StatusText = L.T("Error_Load", ex.Message);
        }
        finally { Loading = false; }
    }

    /// <summary>Covers + existing-install detection + pinned-exe restore. All disk/network I/O
    /// happens HERE (pool thread); only property assignments hop to the dispatcher.</summary>
    private async Task BackgroundEnrichAsync(List<GameItemVm> items, CancellationToken ct)
    {
        var dispatcher = Application.Current.Dispatcher;
        foreach (var item in items)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                string? exe = null;
                ModState? state = null;
                if (Config.PinnedExes.TryGetValue(item.Key, out var pinned) && File.Exists(pinned))
                {
                    exe = pinned;
                    state = AddonService.GetState(Path.GetDirectoryName(pinned)!, pinned);
                }
                else if (item.HasMod)
                {
                    (exe, state) = item.DetectExistingInstall();
                }
                if (exe != null || state != null)
                    await dispatcher.InvokeAsync(() =>
                    {
                        item.ApplyDetected(exe, state);
                        if (item == Selected) RaiseCommands();
                    });

                var cover = await CoverService.GetCoverAsync(item.Game, item.Mod?.SteamAppId);
                if (cover != null && !ct.IsCancellationRequested)
                    await dispatcher.InvokeAsync(() => item.CoverPath = cover);
            }
            catch (Exception ex) { Log.Warn($"enrich {item.Name}: {ex.Message}"); }
        }
        if (!ct.IsCancellationRequested)
            await dispatcher.InvokeAsync(RefreshViewKeepSelection);
    }

    private async Task LoadDetailSafeAsync()
    {
        var token = ++_detailToken;
        try { await LoadDetailAsync(token); }
        catch (Exception ex)
        {
            Log.Warn($"detail: {ex}");
            if (token == _detailToken) DetailStatus = L.T("Error_DetailLoad", ex.Message);
        }
    }

    private async Task LoadDetailAsync(int token)
    {
        ExeCandidates.Clear();
        Settings.Clear();
        Advice.Clear();
        Notes.Clear();
        Prerequisites.Clear();
        EngineNotes.Clear();
        DetailStatus = "";
        var item = Selected;
        _detailItem = item;
        if (item is null) return;

        // structured recommendations (parsed from every note source) shown as prominent cards
        var rhiNote = _rhi.GameNote(item.Name);
        var nativeHdr = item.Mod != null && _rhi.IsNativeHdr(item.Name);
        if (item.Mod != null)
        {
            var noteText = string.Join(" . ", new[] { item.Mod.Note, rhiNote }.Where(s => s != null));
            foreach (var a in AdviceService.Build(noteText, nativeHdr, item.Name))
                Advice.Add(a);

            // anti-cheat detectado no disco: aviso sempre visível, mesmo sem nota na wiki
            var installDir = item.Game.InstallDir;
            var targetDir = item.TargetDir;
            var ac = await Task.Run(() => AntiCheatScanner.Detect(installDir, targetDir));
            if (token != _detailToken) return;
            if (ac != null && Advice.All(a => a.Kind != AdviceKind.AntiCheat))
                Advice.Insert(0, new Advice("",
                    L.T("Install_AntiCheat_Advice", ac),
                    AdviceKind.AntiCheat));
        }

        // Every source of guidance, most game-specific first. The hand-written one-liners that
        // used to stand in for all of this are gone: the real text is better than a summary of it.
        BuildNotes(item, nativeHdr);

        // exe candidates in background (recursive dir scan)
        var subdir = _rhi.InstallSubdir(item.Name);
        var candidates = await Task.Run(() => ExeLocator.FindCandidates(item.Game, subdir));
        if (token != _detailToken) return; // selection changed while scanning — drop stale results

        // an exe already chosen (pin or existing-install detection) always wins and leads the list
        if (item.ChosenExe != null && !candidates.Contains(item.ChosenExe, StringComparer.OrdinalIgnoreCase))
            candidates.Insert(0, item.ChosenExe);
        foreach (var c in candidates) ExeCandidates.Add(c);
        var exe = item.ChosenExe ?? candidates.FirstOrDefault();
        _selectedExe = exe; // set field directly: avoid re-pinning on auto-select
        OnPropertyChanged(nameof(SelectedExe));
        if (exe != null && item.ChosenExe is null) item.ChosenExe = exe;

        // A escolha do tradutor so faz sentido em jogo DX9 de 32 bits — e onde os dois existem.
        MostraTradutorD3d9 = exe is not null
                             && DgVoodooService.Applies(exe)
                             && PeUtils.Inspect(exe, readImports: false)?.Is64Bit == false;
        if (MostraTradutorD3d9)
        {
            var escolhido = Config.D3d9Translator.TryGetValue(item.Key, out var v) ? v
                            : (DxvkService.RecomendadoPara(exe) ? "dxvk" : "dgvoodoo");
            _tradutorD3d9 = escolhido == "dgvoodoo" ? D3d9Translators[1] : D3d9Translators[0];
            OnPropertyChanged(nameof(TradutorD3d9));
        }

        await LoadAvatarAsync(token);
        await RefreshNeuralAndSettingsAsync(token);
        await CheckDlssFixAsync(token);
        await CheckLoadVerdictAsync(token);
        RaiseCommands();
    }

    private async Task LoadAvatarAsync(int token)
    {
        MaintainerAvatar = null;
        var mod = _detailItem?.Mod;
        if (mod is null) return;
        var path = await AvatarService.GetAvatarAsync(mod);
        if (token == _detailToken) MaintainerAvatar = path;
    }

    /// <summary>A correção de DLSS FG só é oferecida quando o mod converte SDR->HDR e o jogo
    /// traz o runtime do DLSS FG — aplicar às cegas mentiria para o DLSS na direção oposta.</summary>
    private async Task CheckDlssFixAsync(int token)
    {
        ShowDlssFix = false;
        _dlssDetection = null;
        var item = _detailItem;
        if (item?.Mod is null || item.TargetDir is null) return;
        var installDir = item.Game.InstallDir;
        var detection = await Task.Run(() => DlssFixService.Detect(installDir));
        if (token != _detailToken) return;
        if (!DlssFixService.ShouldOffer(item.Mod, detection)) return;
        _dlssDetection = detection;
        DlssFixApplied = DlssFixService.IsInstalled(item.TargetDir);
        ShowDlssFix = true;
    }

    private async Task ToggleDlssFixAsync()
    {
        var item = _detailItem;
        if (item?.TargetDir is null || _dlssDetection is null) return;
        ActionBusy = true;
        try
        {
            var dir = item.TargetDir;
            if (DlssFixApplied)
            {
                await Task.Run(() => DlssFixService.Remove(dir));
                DlssFixApplied = false;
                DetailStatus = L.T("Main_DlssFix_Removed");
            }
            else
            {
                var det = _dlssDetection;
                await Task.Run(() => DlssFixService.ApplyAsync(dir, det, new Progress<string>(s => DetailStatus = s)));
                DlssFixApplied = true;
                DetailStatus = L.T("Main_DlssFix_Applied");
            }
        }
        catch (Exception ex) { DetailStatus = ex.Message; }
        finally { ActionBusy = false; RaiseCommands(); }
    }

    /// <summary>
    /// Detecção de NR e leitura das settings, nesta ordem e sempre juntas: é a detecção que decide
    /// se os sliders de Neural Uplift entram na lista (eles não vêm do manifesto gerado), então
    /// rodar as settings sozinhas usa a detecção do jogo — ou da pasta — anterior.
    /// </summary>
    private async Task RefreshNeuralAndSettingsAsync(int token)
    {
        await CheckNeuralAsync(token);
        await RefreshDlssAsync(token);
        await LoadSettingsSafeAsync(token);

        // As bolinhas do card vivem no GameItemVm, nao nesta view model, e nada as tocava
        // depois de instalar: o interruptor daqui ficava verde e a bolinha do card, vermelha.
        // Este e o ponto por onde instalar, remover e trocar de tradutor passam, entao vale
        // para os tres.
        _detailItem?.RefreshLuzes();
    }

    /// <summary>
    /// O Neural Uplift depende de três coisas independentes: o BUILD do addon saber acionar o
    /// DLSS-NR, o JOGO ter DLSS ativo (é de onde saem profundidade e motion vectors) e a MÁQUINA
    /// ser Blackwell com driver 616+ e o runtime na biblioteca.
    ///
    /// As duas primeiras escondem o cartão — sem elas não há o que oferecer. A terceira não: ela
    /// vira um aviso dentro do cartão. Alguém que instalou um addon com NR e não vê nada na tela
    /// precisa ler POR QUE, e um cartão ausente não diz isso.
    /// </summary>
    private async Task CheckNeuralAsync(int token)
    {
        ShowNeural = false;
        NeuralBlocker = null;
        NeuralNeedsRuntime = false;
        _neuralDetection = null;
        var item = _detailItem;
        // De proposito NAO exige item.Mod: suporte a NR e propriedade do ARQUIVO do addon, nao
        // do catalogo. Os builds com NR sao passados no Discord e caem em jogos que o catalogo
        // pode nem listar — exigir entrada de catalogo aqui esconderia o cartao exatamente no
        // caso para o qual ele existe. O que importa e haver um addon detectado na pasta.
        // NAO exige mais item.State.AddonPath: com o addon generico na biblioteca, qualquer jogo
        // com DLSS pode receber o neural render, mesmo sem mod RenoDX proprio. O addon generico
        // engancha os exports do NGX que o jogo ja chama e roda o pass inline.
        if (item?.TargetDir is null) return;
        // O indice curado marca os jogos onde mexer no DLSS quebra alguma coisa. Uma lista que
        // alguem mantem contra relato real vale mais do que qualquer heuristica daqui.
        if (_rhi.SkipsDlss(item.Game.Name)) return;

        var installDir = item.Game.InstallDir;
        var targetDir = item.TargetDir;
        var addonPath = item.State?.AddonPath;
        // O addon é o build da comunidade. Uma cópia já presente na máquina vem primeiro — quem
        // seguiu as instruções do Discord já a tem, e pode ser mais nova que a que sabemos buscar.
        var allDirs = Games.Select(g => g.Game.InstallDir).Where(d => d is not null).Distinct().ToList()!;
        await Task.Run(() => NeuralUpliftService.AutoDiscoverAddon(allDirs!));
        if (token != _detailToken) return;
        try { await NeuralUpliftService.FetchAddonAsync(new Progress<string>(s => DetailStatus = s)); }
        catch (Exception ex) { DetailStatus = ex.Message; }
        if (token != _detailToken) return;
        var detection = await Task.Run(() => NeuralUpliftService.Detect(installDir, targetDir, addonPath));
        if (token != _detailToken) return;

        // Offerable exige DLSS no jogo, e essa era a regra certa enquanto a unica forma de rodar
        // o pass era consumir o DLSS que o jogo ja tinha. Com o Feeder deixou de ser: um jogo
        // DX11 sem DLSS nenhum passa a ser atendivel, e esconder o cartao dele significaria que
        // o caminho existe no instalador e nao tem por onde ser pedido.
        // Mesma correcao do instalador: depois da primeira instalacao os runtimes que copiamos
        // ficam na pasta, e HasDlss passa a falar deles em vez do jogo.
        var temDlssNativo = detection.HasDlss && !FeederService.IsDeployed(targetDir);
        var feederServe = !temDlssNativo
                          && FeederService.Applies(item.ChosenExe, temDlssNativo,
                                                   Dlss5Installer.ReachesD3D12(item.ChosenExe))
                          && (detection.AddonSupportsNr || detection.GenericAddonInLibrary);
        if (!detection.Offerable && !feederServe) return;

        // O runtime nao vem em driver nem em SDK publico: as unicas copias sao as que ja estao
        // nesta maquina. Procura sozinho antes de pedir que o usuario ache o arquivo na mao —
        // achar um .dll de 158 MB pelo Explorer nao e trabalho do usuario.
        if (detection.Host.Blackwell
            && detection.Host.DriverBranch >= NeuralUpliftService.MinDriverBranch
            && !detection.Host.RuntimeInLibrary)
        {
            var dirs = Games.Select(g => g.Game.InstallDir).Where(d => d is not null).Distinct().ToList()!;
            var found = await Task.Run(() => NeuralUpliftService.AutoDiscoverRuntime(
                dirs!, new Progress<string>(s => DetailStatus = s)));
            if (token != _detailToken) return;

            // Nenhuma cópia nesta máquina. O runtime não vem em driver nem em SDK público, então
            // sem isto o usuário fica travado num bloqueio que ele não tem como resolver — a não
            // ser saindo para procurar um DLL de 158 MB. Só é instalado se a NVIDIA assinou.
            if (found is null)
            {
                try
                {
                    var version = await NeuralUpliftService.FetchRuntimeAsync(
                        _dlssIndex, new Progress<string>(s => DetailStatus = s));
                    if (version is not null) { found = version; DetailStatus = L.T("Neural_Fetched", version); }
                }
                catch (Exception ex) { DetailStatus = ex.Message; }
                if (token != _detailToken) return;
            }
            else DetailStatus = L.T("Neural_AutoFound", found);

            if (found is not null)
            {
                detection = await Task.Run(() => NeuralUpliftService.Detect(installDir, targetDir, addonPath));
                if (token != _detailToken) return;
            }
        }

        _neuralDetection = detection;
        // o bloqueio do host (GPU/driver/runtime) vem primeiro; so depois o do caminho generico
        NeuralBlocker = detection.Host.Blocker ?? detection.GenericBlocker;
        NeuralNeedsRuntime = detection.Host.Blackwell
            && detection.Host.DriverBranch >= NeuralUpliftService.MinDriverBranch
            && !detection.Host.RuntimeInLibrary;
        // sem mod RenoDX nao ha State; o ReShade.ini fica ao lado do addon na pasta do jogo
        var neuralIni = item.State?.IniPath ?? System.IO.Path.Combine(targetDir, "ReShade.ini");
        NeuralApplied = NeuralUpliftService.IsApplied(targetDir, neuralIni, addonPath);
        BuildDlss5Chain(targetDir, neuralIni, detection, item.ChosenExe);

        // O interruptor volta sozinho, sempre que o launcher olha para o jogo — nao apenas ao
        // clicar em Play. A tecla F6 do addon desliga o neural e o estado fica GRAVADO no ini,
        // entao um toque acidental (F6 e quicksave em muitos jogos) apagava o efeito para sempre,
        // em silencio, com tudo instalado e verde.
        //
        // Reafirmar so quando a unica peca fora do lugar e o interruptor: se falta arquivo, quem
        // resolve e a instalacao, e ligar a chave ali so criaria um verde falso. E nunca com o
        // jogo aberto — o addon reescreve o ini ao sair e levaria a nossa correcao junto.
        if (!Dlss5Ready
            && !NeuralApplied
            && Dlss5Chain.All(l => l.Ok || l.Label == L.T("Dlss5_Link_Switch"))
            && !AddonService.IsGameRunning(targetDir)
            && NeuralUpliftService.ReassertEnabled(neuralIni))
        {
            NeuralApplied = NeuralUpliftService.IsApplied(targetDir, neuralIni, addonPath);
            BuildDlss5Chain(targetDir, neuralIni, detection, item.ChosenExe);
            DetailStatus = L.T("Main_Neural_Reasserted");
        }

        ShowNeural = true;
    }

    /// <summary>Estado dos runtimes de DLSS do jogo: o que ele carrega e o que a biblioteca tem
    /// de mais novo. Independente do neural — vale para qualquer jogo com DLSS.</summary>
    private async Task RefreshDlssAsync(int token)
    {
        ShowDlss = false;
        DlssSummary = null;
        DlssHealth = null;
        var item = _detailItem;
        if (item?.TargetDir is null) return;
        if (_rhi.SkipsDlss(item.Game.Name)) return;

        var installDir = item.Game.InstallDir;
        var targetDir = item.TargetDir;

        var (inGame, library, applied) = await Task.Run(() =>
        {
            var g = DlssRuntimeService.DetectInGame(installDir);
            var l = DlssRuntimeService.Library();
            return (g, l, DlssRuntimeService.IsApplied(installDir));
        });
        if (token != _detailToken) return;
        if (inGame.Count == 0) return;   // jogo sem DLSS: nao ha o que atualizar

        // uma linha por feature, com a versao do jogo e, quando houver, para onde subiria
        var byName = library.ToDictionary(r => r.FileName, StringComparer.OrdinalIgnoreCase);
        var lines = inGame
            .GroupBy(r => r.FileName, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var cur = g.OrderBy(r => r.Version).First();   // a mais antiga manda: e a que limita
                var text = $"{cur.Feature} {cur.Version}";
                if (!DlssRuntimeService.IsSwappable(cur.FileName))
                    // Sem isso o usuario ve o Frame Generation listado, clica em Atualizar e nada
                    // acontece com ele — parece defeito, e e decisao deliberada.
                    text += " (" + L.T("Dlss_NotSwapped") + ")";
                else if (byName.TryGetValue(cur.FileName, out var lib) && lib.Version > cur.Version)
                    text += $" → {lib.Version}";
                return text;
            });
        DlssSummary = string.Join("   ", lines);

        // Problema comprovado no estado atual do jogo — venha de onde vier (deste launcher, do
        // DLSS Swapper, ou de uma troca manual). So reporta: nao da para saber de fora qual versao
        // este jogo aceita, entao adivinhar o conserto seria repetir o erro que causou isso.
        var issues = await Task.Run(() => DlssRuntimeService.CheckHealth(installDir));
        if (token != _detailToken) return;
        DlssHealth = issues.Count == 0 ? null
            : string.Join("\n", issues.Select(i => $"[{i.Severity}] {i.Message}"));

        DlssApplied = applied;
        ShowDlss = true;
    }

    /// <summary>
    /// Na abertura, avisa se algum jogo ficou com runtime trocado.
    ///
    /// Uma troca nao aparece em lugar nenhum ate o jogo abrir errado, e quem mexeu em seis jogos
    /// nao tem como lembrar quais. O launcher sabe exatamente onde mexeu — omitir isso e o que faz
    /// a ferramenta parecer que "deixou coisa pela metade".
    /// </summary>
    /// <summary>
    /// Na abertura, devolve o neural a "ligado" em todo jogo onde ele foi desligado por dentro.
    ///
    /// A tecla F6 do addon desliga o filtro e o estado fica GRAVADO no ReShade.ini — e F6 e
    /// quicksave em meio mundo de jogo. Um toque acidental apagava o efeito para sempre, sem
    /// erro, sem aviso, com a instalacao inteira intacta.
    ///
    /// Varrer na abertura fecha o caso de quem joga pela Steam e nunca abre o cartao no launcher:
    /// basta o launcher subir uma vez. Nao mexe em jogo aberto (o addon reescreve o ini ao sair
    /// e levaria a correcao junto) nem em pasta onde a cadeia nao esta completa — la o que falta
    /// nao e a chave.
    /// </summary>
    private static async Task ReassertNeuralAsync(IReadOnlyList<string> dirs)
    {
        try
        {
            // Sem filtrar por IsAppliedAnywhere: ele responde "esta LIGADO?", e usa-lo aqui
            // excluiria justamente os jogos desligados, que sao os unicos com o que corrigir.
            // Quem decide e o ReassertEnabledIn, que so mexe onde ha addon implantado.
            var religados = await Task.Run(() => dirs
                .Where(d => d is not null && Directory.Exists(d))
                .Where(d => !AddonService.IsGameRunning(d!))
                .Count(d => NeuralUpliftService.ReassertEnabledIn(d!)));
            if (religados > 0) Log.Info($"neural: interruptor devolvido em {religados} jogo(s)");
        }
        catch (Exception ex) { Log.Warn($"reafirmar neural na varredura: {ex.Message}"); }
    }

    private async Task CheckSwappedRuntimesAsync(IReadOnlyList<string> dirs)
    {
        await ReassertNeuralAsync(dirs);
        try
        {
            // Um jogo com DLSS 5 instalado tem runtime trocado POR DEFINICAO — foi o que o usuario
            // pediu. Listar esses aqui transforma o aviso em ruido permanente, e o botao ao lado
            // dele desfaria justamente a instalacao. So entra quem tem troca SEM a feature ligada,
            // que e o caso que o aviso existe para pegar: mexeu e esqueceu.
            //
            // A checagem NAO pode depender do TargetDir: ele vem do executavel escolhido, que so e
            // resolvido no enriquecimento em segundo plano — disparado em paralelo com esta
            // varredura. Perguntar por ele aqui devolvia nulo para quase todo jogo, e todos eles
            // apareciam no aviso. IsAppliedAnywhere acha o addon sozinho, sem essa corrida.
            var swapped = await Task.Run(() => dirs
                .Where(d => d is not null && Directory.Exists(d))
                .Where(d => DlssRuntimeService.IsApplied(d!))
                .Where(d => !NeuralUpliftService.IsAppliedAnywhere(d!))
                .Select(d => Path.GetFileName(d!.TrimEnd('\\', '/')))
                .ToList());
            if (swapped.Count == 0) { SwappedGames = null; return; }
            SwappedGames = string.Join(", ", swapped);
        }
        catch (Exception ex) { Log.Warn($"sweep check: {ex.Message}"); }
    }

    /// <summary>Jogos com runtime de DLSS trocado, ou null. Vira o aviso de topo.</summary>
    private string? _swappedGames;
    public string? SwappedGames
    {
        get => _swappedGames;
        set { if (Set(ref _swappedGames, value)) OnPropertyChanged(nameof(HasSwappedGames)); }
    }
    public bool HasSwappedGames => _swappedGames != null;

    /// <summary>Devolve TODOS os runtimes trocados, em todos os jogos, de uma vez.</summary>
    private async Task RestoreAllDlssAsync()
    {
        ActionBusy = true;
        try
        {
            var dirs = Games.Select(g => g.Game.InstallDir).Where(d => d is not null).ToList();
            var r = await Task.Run(() => DlssRuntimeService.RestoreAll(
                dirs!, new Progress<string>(s => StatusText = s)));
            StatusText = r.Locked.Count == 0
                ? L.T("Dlss_Sweep_Done", r.Restored)
                : L.T("Dlss_Sweep_Locked", r.Restored, string.Join(", ", r.Locked));
            await CheckSwappedRuntimesAsync(dirs!);
            if (r.Locked.Count == 0) SwappedGames = null;
        }
        catch (Exception ex) { StatusText = ex.Message; }
        finally { ActionBusy = false; RaiseCommands(); }
    }

    /// <summary>Conserta o conjunto de runtimes quebrado — o botao existe porque apontar o defeito
    /// e mandar a pessoa resolver na pasta nao serve para quem usa um launcher.</summary>
    private async Task RepairDlssAsync()
    {
        var item = _detailItem;
        if (item?.TargetDir is null) return;
        var installDir = item.Game.InstallDir;
        var targetDir = item.TargetDir;
        var iniPath = item.State?.IniPath ?? System.IO.Path.Combine(targetDir, "ReShade.ini");
        ActionBusy = true;
        try
        {
            // ---- 1. o conjunto de runtimes ----
            // Sem backup, o conserto precisa de um conjunto COMPLETO de referencia; procura um
            // antes de desistir.
            if (!System.IO.Directory.Exists(DlssRuntimeService.LibrarySetDir))
            {
                var refs = Games.Select(g => g.Game.InstallDir).Where(d => d is not null).Distinct().ToList();
                refs.Add(System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
                await Task.Run(() => DlssRuntimeService.AutoDiscoverStreamlineSet(
                    refs!, new Progress<string>(s => DetailStatus = s)));
            }

            var notas = new List<string>();
            try
            {
                var r = await Task.Run(() => DlssRuntimeService.Repair(installDir, targetDir,
                    new Progress<string>(s => DetailStatus = s)));
                notas.AddRange(r.Notes);
            }
            catch (Exception ex) { notas.Add(ex.Message); }

            // ---- 2. a cadeia que carrega o filtro ----
            // Consertar so os runtimes deixava metade do problema de pe: sem o ReShade, sem o
            // addon ou sem a chave ligada, o sintoma e identico ao das DLLs erradas — o jogo abre
            // e nada acontece. Quem clica em Corrigir quer o conjunto TODO funcionando, entao a
            // verificacao vai ate o fim da cadeia.
            var det = await Task.Run(() => NeuralUpliftService.Detect(installDir, targetDir,
                item.State?.AddonPath));
            if (det.Offerable && det.Host.Blocker is null)
            {
                if (det.NeedsReShade && item.ChosenExe is not null)
                {
                    DetailStatus = L.T("Neural_InstallingReShade");
                    var dep = await _reshade.DeployAsync(targetDir, item.ChosenExe, null, null,
                        new Progress<string>(s => DetailStatus = s));
                    notas.Add(dep.Success ? L.T("Neural_ReShadeInstalled") : dep.Message);
                }

                if (!NeuralUpliftService.IsApplied(targetDir, iniPath, item.State?.AddonPath))
                {
                    await Task.Run(() => NeuralUpliftService.Apply(targetDir, iniPath, det.UsesGeneric,
                        new Progress<string>(s => DetailStatus = s)));
                    notas.Add(L.T("Neural_Applied"));
                }
            }

            DetailStatus = string.Join("; ", notas.Where(n => !string.IsNullOrWhiteSpace(n)));
            if (item == _detailItem) await RefreshNeuralAndSettingsAsync(_detailToken);
        }
        catch (Exception ex) { DetailStatus = ex.Message; }
        finally { ActionBusy = false; RaiseCommands(); }
    }

    /// <summary>Atualiza os runtimes do jogo para os da biblioteca, ou devolve os originais.</summary>
    private async Task ToggleDlssAsync()
    {
        var item = _detailItem;
        if (item?.TargetDir is null) return;
        var installDir = item.Game.InstallDir;
        var targetDir = item.TargetDir;
        ActionBusy = true;
        try
        {
            if (DlssApplied)
            {
                var n = await Task.Run(() => DlssRuntimeService.Restore(installDir, targetDir));
                DetailStatus = L.T("Main_Dlss_Restored", n);
            }
            else
            {
                // A biblioteca se enche das copias que ja estao na maquina: todo jogo com DLSS
                // carrega um runtime assinado, e o mais novo entre eles serve para o mais antigo.
                var dirs = Games.Select(g => g.Game.InstallDir).Where(d => d is not null).Distinct().ToList();
                await Task.Run(() => DlssRuntimeService.AutoDiscover(
                    dirs!, new Progress<string>(s => DetailStatus = s)));

                var r = await Task.Run(() => DlssRuntimeService.Apply(installDir, targetDir,
                    new Progress<string>(s => DetailStatus = s)));
                DetailStatus = r.Updated > 0
                    ? L.T("Main_Dlss_Updated", r.Updated) + " — " + string.Join("; ", r.Notes)
                    : L.T("Main_Dlss_AlreadyCurrent", r.AlreadyCurrent);
            }
            if (item == _detailItem) await RefreshDlssAsync(_detailToken);
        }
        catch (Exception ex) { DetailStatus = ex.Message; }
        finally { ActionBusy = false; RaiseCommands(); }
    }

    /// <summary>
    /// O botao unico. Liga: roda a cadeia inteira pelo mesmo <see cref="Dlss5Installer"/> que o
    /// comando `dlss5` usa — um caminho de codigo, nao dois. Desliga: devolve o que foi posto.
    ///
    /// A interface montava a propria versao da ordem, e o CLI montava outra; era assim que cada
    /// uma esquecia um elo diferente.
    /// </summary>
    /// <summary>
    /// Reinstala o ReShade por cima da build limitada, mantendo o mod que ja esta na pasta.
    ///
    /// O `InstallCommand` fazia isto de carona, e por isso o Reparo ligava nele — mas o
    /// InstallCommand exige mod de catalogo com download, e quem chega neste estado costuma nao
    /// ter nenhum dos dois.
    /// </summary>
    private async Task RepairReShadeAsync()
    {
        var item = _detailItem;
        if (item?.TargetDir is null || item.ChosenExe is null) return;
        ActionBusy = true;
        try
        {
            var dep = await _reshade.DeployAsync(item.TargetDir, item.ChosenExe,
                _rhi.GraphicsApi(item.Game.Name), _rhi.DllNameOverride(item.Game.Name),
                new Progress<string>(s => DetailStatus = s));
            DetailStatus = dep.Message;
            if (item == _detailItem) await LoadDetailSafeAsync();
        }
        catch (Exception ex) { DetailStatus = ex.Message; }
        finally { ActionBusy = false; RaiseCommands(); }
    }

    /// <summary>Ver <see cref="ModCommand"/>: tres estados por baixo, um interruptor por cima.</summary>
    private async Task ToggleModAsync()
    {
        var item = _detailItem;
        if (item is null) return;
        // Desligar REMOVE, nao renomeia para .disabled.
        //
        // Ter as duas coisas pedia que a pessoa escolhesse entre "desativar" e "remover" sem nada
        // na tela explicando a diferenca — e a diferenca era so onde os bytes ficam guardados,
        // que nao e problema de quem usa. Religar nao custa download: o addon fica no cache de
        // downloads do launcher e e reaproveitado quando o build remoto e o mesmo.
        if (!item.IsInstalled) await InstallAsync();
        else await RemoveAsync();
        RaiseModState();
    }

    /// <summary>
    /// Reinstala com o tradutor recem-escolhido. E uma instalacao inteira de proposito: o
    /// instalador ja sabe desfazer o tradutor anterior (e a camada Vulkan dele, quando for o
    /// caso) antes de por o novo, e refazer a cadeia toda em cima.
    /// </summary>
    private async Task TrocarTradutorAsync()
    {
        var item = _detailItem;
        if (item?.TargetDir is null) return;
        var ini = item.State?.IniPath ?? System.IO.Path.Combine(item.TargetDir, "ReShade.ini");
        var escolha = Config.D3d9Translator.TryGetValue(item.Key, out var t) ? t : null;
        ActionBusy = true;
        try
        {
            var r = await Dlss5Installer.InstallAsync(
                item.Game, item.TargetDir, ini, item.ChosenExe, item.State?.AddonPath,
                _dlssIndex, _reshade, _rhi, new Progress<string>(s => DetailStatus = s),
                default,
                preferirDxvk: escolha != "dgvoodoo",
                forcarDgVoodoo: escolha == "dgvoodoo");
            DetailStatus = r.Ok ? L.T("Main_D3d9Translator_Reinstalled")
                                : r.Blocker ?? string.Join("; ", r.Steps);
            if (item == _detailItem) await RefreshNeuralAndSettingsAsync(_detailToken);
        }
        catch (Exception ex) { DetailStatus = ex.Message; }
        finally { ActionBusy = false; RaiseCommands(); }
    }

    private async Task ToggleDlss5Async()
    {
        var item = _detailItem;
        if (item?.TargetDir is null) return;
        var dir = item.TargetDir;
        var ini = item.State?.IniPath ?? System.IO.Path.Combine(dir, "ReShade.ini");
        ActionBusy = true;
        try
        {
            // Só remove quando a cadeia está INTEIRA. Com uma peça faltando o interruptor mostra
            // "desligado", e quem clica em algo desligado quer ligá-lo — mas NeuralApplied
            // continua verdadeiro (a chave segue no ini), então esta condição desinstalava.
            // Clicar para consertar e receber uma desinstalação é o pior desfecho possível aqui.
            if (Dlss5Ready)
            {
                await Task.Run(() => NeuralUpliftService.Remove(dir, ini));
                DetailStatus = L.T("Main_Neural_Removed");
            }
            else
            {
                // A escolha do tradutor, quando o usuario fez uma. Sem escolha, o padrao decide.
                var escolha = Config.D3d9Translator.TryGetValue(item.Key, out var t) ? t : null;
                var r = await Dlss5Installer.InstallAsync(
                    item.Game, dir, ini, item.ChosenExe, item.State?.AddonPath,
                    _dlssIndex, _reshade, _rhi, new Progress<string>(s => DetailStatus = s),
                    default,
                    preferirDxvk: escolha != "dgvoodoo",
                    forcarDgVoodoo: escolha == "dgvoodoo");
                DetailStatus = r.Ok
                    ? string.Join("  •  ", r.Manual)
                    : r.Blocker ?? string.Join("; ", r.Steps);
            }
            if (item == _detailItem) await RefreshNeuralAndSettingsAsync(_detailToken);
        }
        catch (Exception ex) { DetailStatus = ex.Message; }
        finally { ActionBusy = false; RaiseCommands(); }
    }

    private async Task ToggleNeuralAsync()
    {
        var item = _detailItem;
        if (item?.TargetDir is null || _neuralDetection is null) return;
        // Sem mod RenoDX o item.State pode nao existir; o ReShade.ini fica ao lado do addon.
        var iniPath = item.State?.IniPath ?? System.IO.Path.Combine(item.TargetDir, "ReShade.ini");
        ActionBusy = true;
        try
        {
            var dir = item.TargetDir;
            var ini = iniPath;
            var generic = _neuralDetection.UsesGeneric;
            if (NeuralApplied)
            {
                await Task.Run(() => NeuralUpliftService.Remove(dir, ini));
                NeuralApplied = false;
                DetailStatus = L.T("Main_Neural_Removed");
            }
            else
            {
                // O addon generico e um addon de ReShade: sem esse host nao ha nada que o carregue.
                // Instala na hora em vez de bloquear e mandar o usuario resolver por fora — era o
                // passo manual que fazia "ligar o neural" falhar em jogo sem mod nenhum.
                if (_neuralDetection.NeedsReShade && item.ChosenExe is not null)
                {
                    var deploy = await _reshade.DeployAsync(dir, item.ChosenExe, null, null,
                        new Progress<string>(s => DetailStatus = s));
                    if (!deploy.Success) { DetailStatus = deploy.Message; return; }
                }

                await Task.Run(() => NeuralUpliftService.Apply(dir, ini, generic,
                    new Progress<string>(s => DetailStatus = s)));
                NeuralApplied = true;
                DetailStatus = L.T("Main_Neural_Applied");
            }
            // os sliders de NR entram/saem da lista junto com o estado
            if (item == _detailItem) await RefreshNeuralAndSettingsAsync(_detailToken);
        }
        catch (Exception ex) { DetailStatus = ex.Message; }
        finally { ActionBusy = false; RaiseCommands(); }
    }

    /// <summary>Traz o nvngx_dlssnr.dll para a biblioteca do launcher. Uma vez só: dali ele serve
    /// todos os jogos.</summary>
    private async Task ImportNeuralRuntimeAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = L.T("Main_Neural_ImportTitle"),
            Filter = $"{NeuralUpliftService.RuntimeFile}|{NeuralUpliftService.RuntimeFile}",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;

        ActionBusy = true;
        try
        {
            var path = dlg.FileName;
            DetailStatus = L.T("Main_Neural_Importing");
            await Task.Run(() => NeuralUpliftService.ImportRuntime(path));
            DetailStatus = L.T("Main_Neural_Imported");
            await CheckNeuralAsync(_detailToken);
        }
        catch (Exception ex) { DetailStatus = ex.Message; }
        finally { ActionBusy = false; RaiseCommands(); }
    }

    /// <summary>
    /// Collect the game's guidance from every source, in the order someone configuring the game
    /// needs it: what the mod's own author wrote, then the wiki row, then the curated index, then
    /// the engine-wide rules (which go behind a disclosure because they repeat for hundreds of
    /// games). Duplicates across sources are dropped — the wiki and the index often say the same
    /// thing in the same words.
    /// </summary>
    private void BuildNotes(GameItemVm item, bool nativeHdr)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(ModNote n, ObservableCollection<ModNote> into)
        {
            if (n.Text.Length == 0 && n.Preformatted is null) return;
            if (n.DedupKey.Length > 0 && !seen.Add(n.DedupKey)) return;
            // prerequisites are hoisted above the install button wherever they came from
            if (n.Location == "ANTES DE INSTALAR" && !ReferenceEquals(into, EngineNotes))
                Prerequisites.Add(n);
            else into.Add(n);
        }

        if (item.Mod is null) return;

        // 1. the mod author's own words, written inside the overlay
        foreach (var ins in _manifest?.GetInstructions(item.Mod.Slug) ?? Array.Empty<SettingDef>())
        {
            var text = AdviceService.StripSymbols(ins.Label ?? ins.Tooltip ?? "");
            if (text.Length == 0) continue;
            Add(new ModNote(NoteSource.ModSource, NoteKind.Step,
                ins.PresetValues != null
                    ? L.T("Main_Note_AuthorPreset", ins.Label)
                    : L.T("Main_Note_AuthorInstruction"),
                ins.PresetValues != null ? (ins.Tooltip ?? text) : text,
                null, null, AdviceService.GuessLocation(text, ins.Section)), Notes);
        }

        // 2. the game's row in the wiki, plus the status/Nexus/discussion pointers
        foreach (var n in item.Mod.Notes) Add(n, Notes);

        // 3. the curated index (install warnings, required ReShade version, external download)
        foreach (var n in _rhi.GameNotes(item.Name))
        {
            // the index points at the author's page for some mods that DO have a working snapshot
            // here; showing it as a prerequisite next to an enabled Install button reads as a
            // contradiction, so it degrades to a plain link
            if (n.Id == RhiManifestService.ModPageNoteId && item.Mod.DownloadUrl != null)
            {
                Add(n with { Kind = NoteKind.Info, Location = null }, Notes);
                continue;
            }
            Add(n, Notes);
        }

        if (nativeHdr && Advice.All(a => a.Kind is not (AdviceKind.HdrOn or AdviceKind.HdrOff)))
            Add(new ModNote(NoteSource.Launcher, NoteKind.Info,
                L.T("Main_Note_NativeHdr_Title"),
                L.T("Main_Note_NativeHdr_Text"),
                null, null, "NO JOGO"), Notes);

        // 4. engine-wide rules: the only place that explains how to apply an upgrade at all
        var callouts = item.Mod.Kind switch
        {
            ModKind.UnrealEngine => CatalogService.UnrealCallouts,
            ModKind.UnityEngine => CatalogService.UnityCallouts,
            _ => new List<ModNote>(),
        };
        foreach (var n in callouts) Add(n, EngineNotes);
        if (EngineNotes.Count > 0)
            foreach (var n in CatalogService.GlobalCallouts) Add(n, EngineNotes);
        OnPropertyChanged(nameof(EngineNotesHeader));
    }


    public string EngineNotesHeader => EngineNotes.Count == 0 ? ""
        : L.T("Main_EngineNotes_Header",
            Selected?.Mod?.Kind == ModKind.UnityEngine ? "UNITY" : "UNREAL",
            EngineNotes.Count);

    /// <summary>Ask ReShade.log whether the mod really loaded — the only ground truth
    /// available from outside the game.</summary>
    private async Task CheckLoadVerdictAsync(int token)
    {
        LoadVerdict = "";
        HasLoadVerdict = false;
        LoadVerdictOk = false;
        NeedsRepair = false;
        var item = _detailItem;
        if (item?.State is null || item.State.AddonPath is null) return;
        var dir = item.State.TargetDir;
        var report = await Task.Run(() => ReShadeLogService.Check(dir));
        if (token != _detailToken) return;
        LoadVerdict = report.Message;
        HasLoadVerdict = true;
        LoadVerdictOk = report.Result is LoadResult.Loaded;
        NeedsRepair = report.Result is LoadResult.LimitedBuild or LoadResult.NoAddonSupport;

        // mods RenoDX atualizam direto — avisa quando há build nova no servidor
        if (item.Mod != null)
        {
            var newer = await AddonService.IsUpdateAvailableAsync(item.Mod, item.State);
            if (token != _detailToken) return;
            if (newer == true)
            {
                LoadVerdict += "\n\n" + L.T("Main_Detail_UpdateAvailable");
                // Build nova e algo a fazer, entao o bloco sai do recolhido e aparece.
                LoadVerdictOk = false;
            }
        }
    }

    /// <summary>Por que o painel de configurações está vazio (null = há settings).</summary>
    private string _noSettingsReason = "";
    public string NoSettingsReason { get => _noSettingsReason; set => Set(ref _noSettingsReason, value); }
    private bool _hasNoSettingsReason;
    public bool HasNoSettingsReason { get => _hasNoSettingsReason; set => Set(ref _hasNoSettingsReason, value); }

    private void SetNoSettings(string reason)
    {
        NoSettingsReason = reason;
        HasNoSettingsReason = reason.Length > 0;
    }

    private async Task LoadSettingsSafeAsync(int token)
    {
        try
        {
            Settings.Clear();
            SetNoSettings("");
            var item = _detailItem;
            if (item?.Mod is null) return;
            if (item.Mod.Slug is null)
            {
                SetNoSettings(L.T("Main_NoSettings_ExternalMod"));
                return;
            }
            var defs = _manifest?.GetSettings(item.Mod.Slug);
            if (defs is null && _manifest != null)
            {
                // mod publicado depois deste build: le as opcoes direto do fonte do mod
                SetNoSettings(L.T("Main_NoSettings_Fetching", item.Mod.Slug));
                var fetched = await SettingsFetcher.TryFetchAsync(item.Mod);
                if (token != _detailToken) return;
                if (fetched != null)
                {
                    _manifest.Merge(item.Mod.Slug, fetched);
                    defs = fetched;
                    SetNoSettings("");
                }
            }
            if (defs is null)
            {
                SetNoSettings(L.T("Main_NoSettings_Unavailable", item.Mod.Slug));
                return;
            }

            // Os controles de NR nao estao no manifesto (que sai do fonte publicado do renodx) e
            // so existem nos builds que carregam a feature. Quando este build tem, eles entram
            // aqui — senao o usuario ganha o botao de ligar e nenhuma forma de regular. Antes do
            // teste de lista vazia: um addon so-NR tem zero opcoes no manifesto e mesmo assim
            // precisa mostrar os sliders.
            if (_neuralDetection?.AddonSupportsNr == true)
                defs = [.. defs, .. NeuralUpliftService.Knobs];

            if (defs.Count == 0)
            {
                // "no knobs" does not mean "nothing to do": several of these mods are configured
                // entirely from the game's own menu, and the author says how in the notes above.
                bool hasInstructions = Notes.Any(n => n.Source == NoteSource.ModSource);
                SetNoSettings(L.T(hasInstructions
                    ? "Main_NoSettings_InGameOnly"
                    : "Main_NoSettings_Fixed"));
                return;
            }
            if (item.State is null) return;
            var iniPath = item.State.IniPath;
            var values = await Task.Run(() => SettingsService.Read(iniPath, defs));
            if (token != _detailToken) return;
            foreach (var v in values)
                Settings.Add(new SettingVm(v));
        }
        catch (Exception ex)
        {
            Log.Warn($"settings load: {ex}");
        }
        finally { RaiseCommands(); }
    }

    // ---------- actions ----------

    private async Task InstallAsync()
    {
        var item = _detailItem;
        if (item?.Mod is null) return;
        if (item.ChosenExe is null || item.TargetDir is null)
        {
            DetailStatus = L.T("Install_NoExe");
            return;
        }
        ActionBusy = true;
        var progress = new Progress<string>(s => DetailStatus = s);
        try
        {
            // bitness must match: a 64-bit ReShade never loads .addon32 — installing a
            // mismatched pair silently does nothing, so block and ask for the right exe
            var pe = await Task.Run(() => PeUtils.Inspect(item.ChosenExe, readImports: false));
            if (pe != null && item.Mod.AddonBits != 0 && (pe.Is64Bit ? 64 : 32) != item.Mod.AddonBits)
            {
                DetailStatus = L.T("Install_BitnessMismatch", item.Mod.AddonBits, pe.Is64Bit ? 64 : 32);
                return;
            }

            // anti-cheat: o único dano IRREVERSÍVEL que o app pode causar (ban de conta).
            // Detecta pelos arquivos no disco — não depende da nota da wiki citar o assunto.
            var ac = await Task.Run(() => AntiCheatScanner.Detect(item.Game.InstallDir, item.TargetDir));
            if (ac != null)
            {
                var confirmed = DialogWindow.Confirm(
                    Application.Current?.MainWindow,
                    L.T("Install_AntiCheat_Title", ac),
                    L.T("Install_AntiCheat_Modal", ac),
                    L.T("Install_AntiCheat_Confirm"), DialogKind.Danger);
                if (!confirmed)
                {
                    DetailStatus = L.T("Install_Cancelled_AntiCheat", ac);
                    return;
                }
            }

            var api = _rhi.GraphicsApi(item.Name);
            var dllOverride = _rhi.DllNameOverride(item.Name);
            var deploy = await Task.Run(() => _reshade.DeployAsync(item.TargetDir, item.ChosenExe, api, dllOverride, progress));
            if (!deploy.Success)
            {
                DetailStatus = deploy.Message;
                return;
            }
            try
            {
                await Task.Run(() => AddonService.DownloadAddonAsync(item.Mod, item.TargetDir!, progress));
            }
            catch
            {
                // o addon falhou DEPOIS do ReShade entrar: sem rollback o jogo fica com um
                // dxgi.dll injetado que o usuário não pediu e não sabe remover
                if (deploy.DllName != null)
                {
                    try
                    {
                        AddonService.RollbackReShade(item.TargetDir!, deploy.DllName);
                        DetailStatus = L.T("Install_Rollback_Done");
                    }
                    catch (Exception rex) { Log.Warn($"rollback: {rex.Message}"); }
                }
                throw;
            }
            item.RefreshState();

            var profileMsg = "";
            if (Config.ApplyProfileOnInstall && item.Mod.Slug != null
                && _manifest?.GetSettings(item.Mod.Slug) is { } defs)
            {
                try
                {
                    var applied = await Task.Run(() => SettingsService.ApplyDisplayProfile(item.State!.IniPath, defs, Config));
                    // o espaco fica no codigo, e nao no recurso: separador de frase nao e texto
                    profileMsg = " " + (applied > 0
                        ? L.T("Install_ProfileApplied", Config.PeakNits)
                        : L.T("Install_ProfileNoNits"));
                }
                catch (Exception ex)
                {
                    Log.Warn($"profile-on-install: {ex.Message}");
                    profileMsg = " " + L.T("Install_ProfileFailed");
                }
            }
            if (item == _detailItem) await LoadSettingsSafeAsync(_detailToken);
            DetailStatus = L.T("Install_Success", deploy.Message, profileMsg);
            RefreshViewKeepSelection();
        }
        catch (Exception ex)
        {
            Log.Warn($"install {item.Name}: {ex}");
            DetailStatus = L.T("Error_Install", ex.Message);
        }
        finally { ActionBusy = false; RaiseCommands(); }
    }

    private async Task ToggleAsync()
    {
        var item = _detailItem;
        if (item?.State?.AddonPath is null) return;
        try
        {
            var enable = !item.State.AddonEnabled;
            await Task.Run(() => AddonService.SetEnabled(item.State, enable));
            item.RefreshState();
            DetailStatus = L.T(enable ? "Main_Mod_Enabled" : "Main_Mod_Disabled");
            RefreshViewKeepSelection();
        }
        catch (Exception ex)
        {
            // o disco pode ter mudado por fora (addon apagado à mão): re-sincroniza o estado
            // em vez de deixar o badge mentindo "ATIVADO"
            DetailStatus = ex.Message;
            item.RefreshState();
            RefreshViewKeepSelection();
        }
        RaiseCommands();
    }

    private async Task RemoveAsync()
    {
        var item = _detailItem;
        if (item?.State?.AddonPath is null) return;
        var answer = DialogWindow.Choose(
            Application.Current?.MainWindow,
            L.T("Main_Remove_Title"),
            L.T("Main_Remove_Body", item.Name),
            L.T("Main_Remove_All"), L.T("Main_Remove_ModOnly"));
        if (answer == MessageBoxResult.Cancel) return;
        try
        {
            await Task.Run(() => AddonService.Remove(item.State, alsoReShade: answer == MessageBoxResult.Yes));
            item.RefreshState();
            Settings.Clear();
            DetailStatus = L.T("Main_Remove_Done");
            RefreshViewKeepSelection();
        }
        catch (Exception ex)
        {
            DetailStatus = ex.Message;
            item.RefreshState();
            RefreshViewKeepSelection();
        }
        RaiseCommands();
    }

    private async Task SaveSettingsAsync()
    {
        var item = _detailItem;
        if (item?.State is null) return;
        try
        {
            // only values the user actually changed — untouched ini keys keep their exact text
            var dirty = Settings.Where(s => s.IsDirty).Select(s => (s.Def, s.Value)).ToList();
            if (dirty.Count == 0) { DetailStatus = L.T("Main_Settings_NothingToSave"); return; }
            var iniPath = item.State.IniPath;
            await Task.Run(() => SettingsService.Write(iniPath, dirty));
            await LoadSettingsSafeAsync(_detailToken);
            DetailStatus = L.T("Main_Settings_Saved", dirty.Count, iniPath);
        }
        catch (Exception ex) { DetailStatus = ex.Message; }
    }

    private async Task ApplyProfileAsync()
    {
        var item = _detailItem;
        if (item?.State is null || item.Mod?.Slug is null) return;
        try
        {
            var defs = _manifest?.GetSettings(item.Mod.Slug);
            if (defs is null) return;
            var iniPath = item.State.IniPath;
            var applied = await Task.Run(() => SettingsService.ApplyDisplayProfile(iniPath, defs, Config));
            await LoadSettingsSafeAsync(_detailToken);
            DetailStatus = applied > 0
                ? L.T("Main_Profile_Applied", Config.PeakNits, Config.GameNits, Config.UiNits)
                : L.T("Main_Profile_NoNits");
        }
        catch (Exception ex) { DetailStatus = ex.Message; }
    }

    private async Task ResetSettingsAsync()
    {
        var item = _detailItem;
        if (item?.State is null) return;
        try
        {
            var iniPath = item.State.IniPath;
            var defs = Settings.Select(s => s.Def).ToList();
            await Task.Run(() => SettingsService.Reset(iniPath, defs));
            await LoadSettingsSafeAsync(_detailToken);
            DetailStatus = L.T("Main_Settings_Reset");
        }
        catch (Exception ex) { DetailStatus = ex.Message; }
    }

    /// <summary>
    /// Traz a ponte DX11 e o Feeder para a ultima versao, e leva aos jogos que ja os usam.
    ///
    /// Estas pecas nao tem cartao na grade nem badge de "update available": sao arquivos de
    /// terceiros que o launcher busca sozinho, e a unica forma de a pessoa saber que ficaram para
    /// tras seria acompanhar release de estranho no GitHub. Ninguem faz isso — a ponte ficou
    /// cinco versoes atras aqui mesmo, e ela e a peca sem a qual o Baldur's Gate nao roda.
    ///
    /// Silencioso quando nao ha novidade: devolve texto so quando algo mudou de fato.
    /// </summary>
    private async Task<string> AtualizarPecasDlss5Async()
    {
        var dirs = Games.Select(g => g.Game.InstallDir).Where(d => d is not null).Distinct().ToList()!;
        var partes = new List<string>();
        try
        {
            var n = await NeuralUpliftService.UpdateBridgeAsync(dirs!);
            if (n >= 0) partes.Add(L.T("Dlss5_Bridge_Updated", n));
        }
        catch (Exception ex) { Log.Warn($"atualizar ponte: {ex.Message}"); }
        try
        {
            var n = await FeederService.UpdateAsync(dirs!);
            if (n >= 0) partes.Add(L.T("Feeder_Updated", n));
        }
        catch (Exception ex) { Log.Warn($"atualizar feeder: {ex.Message}"); }
        return partes.Count == 0 ? "" : " " + string.Join(" ", partes);
    }

    /// <summary>Checa TODOS os mods instalados de uma vez (HEAD em paralelo, comparando ETag)
    /// e marca na grade quais têm build nova.</summary>
    private async Task CheckUpdatesAsync()
    {
        ActionBusy = true;
        try
        {
            // O launcher entra na mesma checagem dos mods: quem clica aqui esta perguntando
            // "tem coisa nova?", e a versao do proprio app faz parte da resposta.
            var launcherCheck = LauncherUpdateService.CheckAsync();

            // As pecas do DLSS 5 tambem. Elas nao aparecem na grade e ninguem vai conferir
            // release de terceiro no GitHub: a ponte ficou cinco versoes atras sem que nada
            // indicasse, e ela e o que faz o Baldur's Gate funcionar.
            var pecas = await AtualizarPecasDlss5Async();

            var installed = Games.Where(g => g.IsInstalled && g.Mod?.DownloadUrl != null && g.State != null).ToList();
            if (installed.Count == 0)
            {
                LauncherUpdate = await launcherCheck ?? _launcherUpdate;
                StatusText = (HasLauncherUpdate
                    ? L.T("Update_Found", _launcherUpdate!.Version.ToString())
                    : L.T("Main_Updates_NoneInstalled")) + pecas;
                UpdateCount = 0;
                return;
            }
            StatusText = L.T("Main_Updates_Checking", installed.Count);

            // limita a concorrência para não estourar conexões nem irritar os servidores
            using var gate = new SemaphoreSlim(6);
            var checks = installed.Select(async item =>
            {
                await gate.WaitAsync();
                try { return (item, newer: await AddonService.IsUpdateAvailableAsync(item.Mod!, item.State!)); }
                finally { gate.Release(); }
            });
            var results = await Task.WhenAll(checks);

            int count = 0, unknown = 0;
            foreach (var (item, newer) in results)
            {
                item.HasUpdate = newer == true;
                if (newer == true) count++;
                else if (newer is null) unknown++;
            }
            UpdateCount = count;
            RefreshViewKeepSelection();
            LauncherUpdate = await launcherCheck ?? _launcherUpdate;

            var unverified = unknown > 0 ? " " + L.T("Main_Updates_Unverified", unknown) : "";
            var doLauncher = HasLauncherUpdate ? " " + L.T("Update_Found", _launcherUpdate!.Version.ToString()) : "";
            StatusText = (count > 0
                ? L.T("Main_Updates_Available", count)
                : L.T("Main_Updates_AllCurrent")) + unverified + doLauncher + pecas;
        }
        catch (Exception ex)
        {
            Log.Warn($"check updates: {ex}");
            StatusText = L.T("Error_CheckUpdates", ex.Message);
        }
        finally { ActionBusy = false; }
    }

    /// <summary>Baixa a build nova de cada mod marcado, preservando as configurações
    /// (só o arquivo do addon é trocado; o ReShade.ini fica intacto).</summary>
    private async Task UpdateAllAsync()
    {
        var pending = Games.Where(g => g.HasUpdate && g.Mod?.DownloadUrl != null && g.TargetDir != null).ToList();
        if (pending.Count == 0) return;
        if (!DialogWindow.Confirm(
                Application.Current?.MainWindow,
                L.T("Main_UpdateAll_Title"),
                L.T("Main_UpdateAll_Body", pending.Count),
                L.T("Common_Update")))
            return;

        ActionBusy = true;
        int ok = 0;
        var failed = new List<string>();
        try
        {
            foreach (var item in pending)
            {
                try
                {
                    StatusText = L.T("Main_Updates_Updating", item.Name);
                    await Task.Run(() => AddonService.DownloadAddonAsync(item.Mod!, item.TargetDir!));
                    item.RefreshState();
                    item.HasUpdate = false;
                    ok++;
                }
                catch (Exception ex)
                {
                    Log.Warn($"update {item.Name}: {ex.Message}");
                    failed.Add($"{item.Name} ({ex.Message})");
                }
            }
            UpdateCount = Games.Count(g => g.HasUpdate);
            RefreshViewKeepSelection();
            StatusText = failed.Count == 0
                ? L.T("Main_Updates_Done", ok)
                : L.T("Main_Updates_DonePartial", ok, failed.Count, string.Join("; ", failed.Take(3)));
        }
        catch (Exception ex)
        {
            // O laco por item ja trata a falha de cada um; o que ficava sem rede era o RESUMO —
            // a contagem, a revarredura e a formatacao. Uma excecao ali escapava para o handler
            // global (o comando e `async void`), e o usuario recebia "Erro inesperado" em vez de
            // "N atualizados, M falharam" — sem nunca saber quais tinham dado certo.
            Log.Warn($"update all: {ex.Message}");
            StatusText = L.T("Main_Updates_DonePartial", ok, failed.Count,
                             string.Join("; ", failed.Take(3)));
        }
        finally { ActionBusy = false; }
    }

    /// <summary>Launch through the store when we know it (keeps the store's overlay and
    /// cloud saves working); fall back to running the chosen exe directly.</summary>
    /// <summary>
    /// Antes de abrir o jogo, garante que o interruptor do neural continua ligado.
    ///
    /// O addon persiste esse estado no ReShade.ini e o desliga por tres caminhos — o ini, o
    /// overlay e a tecla F6, que e facil de encostar sem querer durante o jogo. Uma vez desligado
    /// ele fica assim para sempre, e o sintoma e o pior possivel: tudo instalado, tudo verde na
    /// sessao anterior, e nenhum efeito na tela.
    ///
    /// So reafirma onde o launcher ja instalou a cadeia inteira. Num jogo que ele nao instalou,
    /// ou com peca faltando, nao ha nada a reafirmar — e escrever ali seria opiniao sobre uma
    /// instalacao que nao e nossa.
    /// </summary>
    private void ReafirmarNeural(GameItemVm item)
    {
        if (!Dlss5Ready || item.TargetDir is null) return;
        try
        {
            var ini = item.State?.IniPath ?? Path.Combine(item.TargetDir, "ReShade.ini");
            if (NeuralUpliftService.ReassertEnabled(ini))
                DetailStatus = L.T("Main_Neural_Reasserted");
        }
        catch (Exception ex) { Log.Warn($"reafirmar neural: {ex.Message}"); }
    }

    private void LaunchGame()
    {
        var item = _detailItem ?? Selected;
        if (item is null) return;
        ReafirmarNeural(item);
        try
        {
            string? uri = item.Game.Store switch
            {
                GameStore.Steam when item.Game.AppId != null => $"steam://rungameid/{item.Game.AppId}",
                GameStore.Gog when item.Game.AppId != null => $"goggalaxy://openGameView/{item.Game.AppId}",
                _ => null,
            };
            if (uri != null)
            {
                Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
                DetailStatus = L.T("Main_Launch_ViaStore");
                return;
            }
            var exe = item.ChosenExe;
            if (exe != null && File.Exists(exe))
            {
                Process.Start(new ProcessStartInfo(exe)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(exe)!,
                });
                DetailStatus = L.T("Main_Launch_Started");
            }
            else DetailStatus = L.T("Main_Launch_NoExe");
        }
        catch (Exception ex)
        {
            Log.Warn($"launch {item.Name}: {ex.Message}");
            DetailStatus = L.T("Error_Launch", ex.Message);
        }
    }

    private void OpenFolder()
    {
        var dir = Selected?.TargetDir ?? Selected?.Game.InstallDir;
        if (dir != null && Directory.Exists(dir))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    private void OpenMaintainer()
    {
        if (AvatarService.ProfileUrl(_detailItem?.Mod ?? Selected?.Mod) is { } url)
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void OpenNexus()
    {
        var url = Selected?.Mod?.NexusUrl ?? Selected?.Mod?.InfoUrl;
        if (url != null)
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private async Task AddManualGameAsync()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = L.T("Main_AddGame_FolderPickerTitle") };
        if (dlg.ShowDialog() != true) return;
        var dir = dlg.FolderName;
        if (!Config.ManualGameDirs.Contains(dir, StringComparer.OrdinalIgnoreCase))
        {
            Config.ManualGameDirs.Add(dir);
            Config.Save();
        }
        await LoadAsync();
    }

    public void SaveDisplayProfile(double peak, double game, double ui, bool applyOnInstall)
    {
        Config.PeakNits = peak;
        Config.GameNits = game;
        Config.UiNits = ui;
        Config.ApplyProfileOnInstall = applyOnInstall;
        Config.Save();
        OnPropertyChanged(nameof(Config));
    }
}
