using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using RenoDXLauncher.Models;
using RenoDXLauncher.Services;

namespace RenoDXLauncher.ViewModels;

public class MainViewModel : ObservableObject
{
    private readonly CatalogService _catalog = new();
    private readonly RhiManifestService _rhi = new();
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
    }

    // ---------- top-level state ----------

    private string _statusText = "Pronto.";
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

    public string[] FilterOptions { get; } = { "Todos os jogos", "Com mod disponível", "Mod instalado", "Sem mod" };
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
                _ = LoadDetailSafeAsync();
                RaiseCommands();
            }
        }
    }

    public bool HasSelection => _selected != null;

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
                    DetailStatus = "Atenção: existe um mod instalado na pasta anterior " +
                        $"({Path.GetDirectoryName(previousState.AddonPath)}) — remova-o de lá ou volte para aquele executável.";
                _ = LoadSettingsSafeAsync(_detailToken);
                RaiseCommands();
            }
        }
    }

    public ObservableCollection<SettingVm> Settings { get; } = new();
    public ObservableCollection<Advice> Advice { get; } = new();
    public ObservableCollection<string> Notes { get; } = new();

    /// <summary>Verdict from ReShade.log: did the mod actually load last time the game ran?</summary>
    private string _loadVerdict = "";
    public string LoadVerdict { get => _loadVerdict; set => Set(ref _loadVerdict, value); }
    private bool _hasLoadVerdict;
    public bool HasLoadVerdict { get => _hasLoadVerdict; set => Set(ref _hasLoadVerdict, value); }

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

    private void RaiseCommands()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        AddManualGameCommand.RaiseCanExecuteChanged();
        InstallCommand.RaiseCanExecuteChanged();
        ToggleCommand.RaiseCanExecuteChanged();
        RemoveCommand.RaiseCanExecuteChanged();
        SaveSettingsCommand.RaiseCanExecuteChanged();
        ApplyProfileCommand.RaiseCanExecuteChanged();
        ResetSettingsCommand.RaiseCanExecuteChanged();
        OpenFolderCommand.RaiseCanExecuteChanged();
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
            StatusText = "Carregando catálogo RenoDX...";
            Config = LauncherConfig.Load();
            OnPropertyChanged(nameof(Config));
            _manifest ??= await Task.Run(() => new ManifestService());
            var rhiTask = _rhi.LoadAsync();
            _catalogEntries = await _catalog.LoadAsync(forceRefresh);
            await rhiTask;

            StatusText = "Procurando jogos instalados...";
            // the folder scan only surfaces dirs whose NAME matches a catalog game,
            // so standalone installs appear without polluting the grid with random folders
            var knownNames = _catalogEntries
                .SelectMany(e => e.NormalizedAliases)
                .ToHashSet(StringComparer.Ordinal);
            bool KnownGame(string folderName) =>
                knownNames.Contains(MatchService.Normalize(folderName))
                || knownNames.Contains(MatchService.Normalize(MatchService.StripEditionSuffix(folderName)));
            var games = await StoreScanners.ScanAllAsync(KnownGame);
            foreach (var dir in Config.ManualGameDirs.Where(Directory.Exists))
                games.Add(new GameInfo
                {
                    Name = Path.GetFileName(dir.TrimEnd('\\', '/')),
                    InstallDir = dir,
                    Store = GameStore.Manual,
                });

            Games.Clear();
            foreach (var g in games)
            {
                var match = MatchService.FindMatch(g, _catalogEntries);
                Games.Add(new GameItemVm(g, match));
            }
            var withMod = Games.Count(g => g.HasMod);
            StatusText = $"{Games.Count} jogos encontrados — {withMod} com mod RenoDX disponível.";

            var ct = cts.Token;
            _ = Task.Run(() => BackgroundEnrichAsync(Games.ToList(), ct));
        }
        catch (Exception ex)
        {
            Log.Warn($"load: {ex}");
            StatusText = $"Erro ao carregar: {ex.Message}";
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
            if (token == _detailToken) DetailStatus = $"Erro ao carregar detalhes: {ex.Message}";
        }
    }

    private async Task LoadDetailAsync(int token)
    {
        ExeCandidates.Clear();
        Settings.Clear();
        Advice.Clear();
        Notes.Clear();
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
                Advice.Insert(0, new Advice("⛔",
                    $"{ac} detectado neste jogo — usar o mod ONLINE pode BANIR sua conta. Jogue offline.",
                    AdviceKind.AntiCheat));
        }

        // full free-text notes below the cards
        if (item.Mod?.Note is { } n1) Notes.Add(n1);
        if (rhiNote is { } n2) Notes.Add(n2);
        if (nativeHdr && Advice.All(a => a.Kind is not (AdviceKind.HdrOn or AdviceKind.HdrOff)))
            Notes.Add("Este jogo tem HDR nativo — o mod corrige/melhora o HDR do próprio jogo. Normalmente o HDR precisa estar LIGADO dentro do jogo.");
        if (item.Mod?.Kind == ModKind.UnrealEngine)
            Notes.Add("Mod GENÉRICO de Unreal Engine: depois de instalar, ajustes de \"Upgrade\" podem ser necessários no overlay (tecla Home) — veja a nota do jogo acima, se houver.");
        if (item.Mod?.Kind == ModKind.UnityEngine)
            Notes.Add("Mod GENÉRICO de Unity: evite fullscreen exclusivo; se faltar cor, ative Upgrade R11G11B10_FLOAT no overlay (Home).");
        if (item.Mod != null && !item.Mod.Working)
            Notes.Add("Status na wiki: EM CONSTRUÇÃO/instável — pode ter problemas.");

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

        await LoadSettingsSafeAsync(token);
        await CheckLoadVerdictAsync(token);
        RaiseCommands();
    }

    /// <summary>Ask ReShade.log whether the mod really loaded — the only ground truth
    /// available from outside the game.</summary>
    private async Task CheckLoadVerdictAsync(int token)
    {
        LoadVerdict = "";
        HasLoadVerdict = false;
        var item = _detailItem;
        if (item?.State is null || item.State.AddonPath is null) return;
        var dir = item.State.TargetDir;
        var report = await Task.Run(() => ReShadeLogService.Check(dir));
        if (token != _detailToken) return;
        LoadVerdict = report.Message;
        HasLoadVerdict = true;

        // mods RenoDX atualizam direto — avisa quando há build nova no servidor
        if (item.Mod != null)
        {
            var newer = await AddonService.IsUpdateAvailableAsync(item.Mod, item.State);
            if (token != _detailToken) return;
            if (newer == true)
                LoadVerdict += "\n\n🔄 Há uma versão MAIS NOVA deste mod disponível — clique em \"Instalar / Atualizar mod\".";
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
                SetNoSettings("Este mod é distribuído só pelo Nexus/Discord — o launcher não conhece as " +
                    "configurações dele. Ajuste pelo overlay do ReShade no jogo (tecla Home).");
                return;
            }
            var defs = _manifest?.GetSettings(item.Mod.Slug);
            if (defs is null)
            {
                SetNoSettings($"Ainda não tenho a lista de configurações deste mod ({item.Mod.Slug}) — " +
                    "ele é mais novo que o catálogo embutido. Instalar funciona normalmente; ajuste " +
                    "pelo overlay do ReShade no jogo (tecla Home).");
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
            DetailStatus = "Escolha o executável do jogo primeiro (lista acima).";
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
                DetailStatus = $"Instalação bloqueada: o mod é {item.Mod.AddonBits}-bit mas o executável escolhido é " +
                    $"{(pe.Is64Bit ? 64 : 32)}-bit. Selecione o executável certo do jogo na lista acima.";
                return;
            }

            // anti-cheat: o único dano IRREVERSÍVEL que o app pode causar (ban de conta).
            // Detecta pelos arquivos no disco — não depende da nota da wiki citar o assunto.
            var ac = await Task.Run(() => AntiCheatScanner.Detect(item.Game.InstallDir, item.TargetDir));
            if (ac != null)
            {
                var answer = MessageBox.Show(
                    $"⚠️ ATENÇÃO: este jogo usa {ac}.\n\n" +
                    "O ReShade com suporte a add-ons NÃO é assinado. Em jogos ONLINE protegidos por " +
                    "anti-cheat, isso pode resultar em BANIMENTO PERMANENTE da sua conta.\n\n" +
                    "Só continue se você for jogar OFFLINE (ou se souber exatamente o que está fazendo).\n\n" +
                    "Instalar mesmo assim?",
                    $"{ac} detectado — risco de banimento", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                    MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes)
                {
                    DetailStatus = $"Instalação cancelada ({ac} detectado).";
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
                        DetailStatus = "Falha ao baixar o mod — o ReShade que eu tinha acabado de instalar foi removido " +
                            "(o jogo ficou como estava). Tente de novo.";
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
                    profileMsg = applied > 0
                        ? $" Perfil do monitor aplicado ({Config.PeakNits:0} nits)."
                        : " (Este mod não tem ajustes de nits — HDR nativo.)";
                }
                catch (Exception ex)
                {
                    Log.Warn($"profile-on-install: {ex.Message}");
                    profileMsg = " O mod instalou, mas o perfil de nits não foi aplicado — use 'Usar meu perfil' depois.";
                }
            }
            if (item == _detailItem) await LoadSettingsSafeAsync(_detailToken);
            DetailStatus = $"Mod instalado e ativado. {deploy.Message}{profileMsg} Abra o jogo e pressione Home para ver o overlay.";
            RefreshViewKeepSelection();
        }
        catch (Exception ex)
        {
            Log.Warn($"install {item.Name}: {ex}");
            DetailStatus = $"Erro: {ex.Message}";
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
            DetailStatus = enable ? "Mod ATIVADO." : "Mod DESATIVADO (arquivo renomeado para .disabled).";
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
        var answer = MessageBox.Show(
            $"Remover o mod RenoDX de \"{item.Name}\"?\n\nSim = remove o addon e o ReShade (se não houver outros addons).\nNão = remove só o addon RenoDX.",
            "Remover mod", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return;
        try
        {
            await Task.Run(() => AddonService.Remove(item.State, alsoReShade: answer == MessageBoxResult.Yes));
            item.RefreshState();
            Settings.Clear();
            DetailStatus = "Mod removido. As configurações ficaram salvas no ReShade.ini.";
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
            if (dirty.Count == 0) { DetailStatus = "Nada para salvar."; return; }
            var iniPath = item.State.IniPath;
            await Task.Run(() => SettingsService.Write(iniPath, dirty));
            await LoadSettingsSafeAsync(_detailToken);
            DetailStatus = $"{dirty.Count} configurações salvas em {iniPath}. " +
                "Com o jogo aberto: mova o slider Preset para 2 e volte para 1 no overlay (Home) para aplicar sem reiniciar.";
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
                ? $"Perfil aplicado: pico {Config.PeakNits:0} / jogo {Config.GameNits:0} / UI {Config.UiNits:0} nits."
                : "Este mod não expõe ajustes de nits (HDR nativo) — nada foi alterado.";
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
            DetailStatus = "Configurações resetadas para o padrão do mod (chaves removidas do ini).";
        }
        catch (Exception ex) { DetailStatus = ex.Message; }
    }

    private void OpenFolder()
    {
        var dir = Selected?.TargetDir ?? Selected?.Game.InstallDir;
        if (dir != null && Directory.Exists(dir))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    private void OpenNexus()
    {
        var url = Selected?.Mod?.NexusUrl ?? Selected?.Mod?.InfoUrl;
        if (url != null)
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private async Task AddManualGameAsync()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Escolha a pasta do jogo" };
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
