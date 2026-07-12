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

    public LauncherConfig Config { get; private set; } = new();
    public ObservableCollection<GameItemVm> Games { get; } = new();
    public ICollectionView GamesView { get; }

    public MainViewModel()
    {
        GamesView = CollectionViewSource.GetDefaultView(Games);
        GamesView.Filter = FilterGame;
        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(forceRefresh: true));
        InstallCommand = new AsyncRelayCommand(InstallAsync, () => Selected?.Mod?.DownloadUrl != null);
        ToggleCommand = new AsyncRelayCommand(ToggleAsync, () => Selected?.IsInstalled == true);
        RemoveCommand = new AsyncRelayCommand(RemoveAsync, () => Selected?.IsInstalled == true);
        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync, () => Settings.Count > 0);
        ApplyProfileCommand = new AsyncRelayCommand(ApplyProfileAsync, () => Settings.Count > 0);
        ResetSettingsCommand = new AsyncRelayCommand(ResetSettingsAsync, () => Settings.Count > 0);
        OpenFolderCommand = new RelayCommand(OpenFolder, () => Selected?.TargetDir != null || Selected != null);
        OpenNexusCommand = new RelayCommand(OpenNexus, () => Selected?.Mod?.NexusUrl != null);
        AddManualGameCommand = new AsyncRelayCommand(AddManualGameAsync);
    }

    // ---------- top-level state ----------

    private string _statusText = "Pronto.";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    private bool _busy;
    public bool Busy { get => _busy; set => Set(ref _busy, value); }

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
                _ = LoadDetailAsync();
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
            if (Set(ref _selectedExe, value) && value != null && Selected != null)
            {
                Selected.ChosenExe = value;
                Config.PinnedExes[Selected.Key] = value;
                Config.Save();
                _ = LoadSettingsAsync();
                RaiseCommands();
            }
        }
    }

    public ObservableCollection<SettingVm> Settings { get; } = new();
    public ObservableCollection<string> Notes { get; } = new();

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
        Busy = true;
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
            var games = await StoreScanners.ScanAllAsync();
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

            // background: covers + existing-install detection + exe pinning restore
            _ = Task.Run(async () =>
            {
                foreach (var item in Games.ToList())
                {
                    if (Config.PinnedExes.TryGetValue(item.Key, out var pinned) && File.Exists(pinned))
                        Application.Current.Dispatcher.Invoke(() => item.ChosenExe = pinned);
                    else if (item.HasMod)
                        Application.Current.Dispatcher.Invoke(item.DetectExistingInstall);

                    var cover = await CoverService.GetCoverAsync(item.Game, item.Mod?.SteamAppId);
                    if (cover != null)
                        Application.Current.Dispatcher.Invoke(() => item.CoverPath = cover);
                }
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"load: {ex}");
            StatusText = $"Erro ao carregar: {ex.Message}";
        }
        finally { Busy = false; }
    }

    private async Task LoadDetailAsync()
    {
        ExeCandidates.Clear();
        Settings.Clear();
        Notes.Clear();
        DetailStatus = "";
        var item = Selected;
        if (item is null) return;

        // notes
        if (item.Mod?.Note is { } n1) Notes.Add(n1);
        if (_rhi.GameNote(item.Name) is { } n2) Notes.Add(n2);
        if (item.Mod != null && _rhi.IsNativeHdr(item.Name))
            Notes.Add("Este jogo tem HDR nativo — o mod corrige/melhora o HDR do próprio jogo. Normalmente o HDR precisa estar LIGADO dentro do jogo.");
        if (item.Mod?.Kind == ModKind.UnrealEngine)
            Notes.Add("Mod GENÉRICO de Unreal Engine: depois de instalar, ajustes de \"Upgrade\" podem ser necessários no overlay (tecla Home) — veja a nota do jogo acima, se houver.");
        if (item.Mod?.Kind == ModKind.UnityEngine)
            Notes.Add("Mod GENÉRICO de Unity: evite fullscreen exclusivo; se faltar cor, ative Upgrade R11G11B10_FLOAT no overlay (Home).");

        // exe candidates in background (recursive dir scan)
        var subdir = _rhi.InstallSubdir(item.Name);
        var candidates = await Task.Run(() => ExeLocator.FindCandidates(item.Game, subdir));
        foreach (var c in candidates) ExeCandidates.Add(c);
        var exe = Config.PinnedExes.TryGetValue(item.Key, out var pinned) && File.Exists(pinned)
            ? pinned
            : candidates.FirstOrDefault();
        _selectedExe = exe; // set field directly: avoid re-pinning on auto-select
        OnPropertyChanged(nameof(SelectedExe));
        if (exe != null) item.ChosenExe = exe;

        await LoadSettingsAsync();
        RaiseCommands();
    }

    private async Task LoadSettingsAsync()
    {
        Settings.Clear();
        var item = Selected;
        if (item?.Mod?.Slug is null) return;
        var defs = _manifest?.GetSettings(item.Mod.Slug);
        if (defs is null || item.State is null) return;
        var iniPath = item.State.IniPath;
        var values = await Task.Run(() => SettingsService.Read(iniPath, defs));
        foreach (var v in values)
            Settings.Add(new SettingVm(v));
        OnPropertyChanged(nameof(Settings));
        RaiseCommands();
    }

    // ---------- actions ----------

    private async Task InstallAsync()
    {
        var item = Selected;
        if (item?.Mod is null) return;
        if (item.ChosenExe is null || item.TargetDir is null)
        {
            DetailStatus = "Escolha o executável do jogo primeiro (lista acima).";
            return;
        }
        Busy = true;
        var progress = new Progress<string>(s => DetailStatus = s);
        try
        {
            // bitness sanity: .addon32 mod + 64-bit exe (or vice versa) means wrong exe picked
            var pe = PeUtils.Inspect(item.ChosenExe, readImports: false);
            if (pe != null && item.Mod.AddonBits != 0 && (pe.Is64Bit ? 64 : 32) != item.Mod.AddonBits)
            {
                DetailStatus = $"Atenção: o mod é {item.Mod.AddonBits}-bit mas o exe escolhido é {(pe.Is64Bit ? 64 : 32)}-bit. Confira o executável.";
            }

            var deploy = await _reshade.DeployAsync(item.TargetDir, item.ChosenExe,
                _rhi.GraphicsApi(item.Name), _rhi.DllNameOverride(item.Name), progress);
            if (!deploy.Success)
            {
                DetailStatus = deploy.Message;
                return;
            }
            await AddonService.DownloadAddonAsync(item.Mod, item.TargetDir, progress);
            item.RefreshState();

            if (Config.ApplyProfileOnInstall && item.Mod.Slug != null
                && _manifest?.GetSettings(item.Mod.Slug) is { } defs)
            {
                SettingsService.ApplyDisplayProfile(item.State!.IniPath, defs, Config);
            }
            await LoadSettingsAsync();
            DetailStatus = $"Mod instalado e ativado. {deploy.Message} Abra o jogo e pressione Home para ver o overlay.";
        }
        catch (Exception ex)
        {
            Log.Warn($"install {item.Name}: {ex}");
            DetailStatus = $"Erro: {ex.Message}";
        }
        finally { Busy = false; RaiseCommands(); }
    }

    private async Task ToggleAsync()
    {
        var item = Selected;
        if (item?.State?.AddonPath is null) return;
        try
        {
            var enable = !item.State.AddonEnabled;
            await Task.Run(() => AddonService.SetEnabled(item.State, enable));
            item.RefreshState();
            DetailStatus = enable ? "Mod ATIVADO." : "Mod DESATIVADO (arquivo renomeado para .disabled).";
        }
        catch (Exception ex) { DetailStatus = ex.Message; }
        RaiseCommands();
    }

    private async Task RemoveAsync()
    {
        var item = Selected;
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
        }
        catch (Exception ex) { DetailStatus = ex.Message; }
        RaiseCommands();
    }

    private async Task SaveSettingsAsync()
    {
        var item = Selected;
        if (item?.State is null) return;
        try
        {
            var dirty = Settings.Where(s => s.IsDirty || s.WasSetInIni)
                .Select(s => (s.Def, s.Value)).ToList();
            if (dirty.Count == 0) { DetailStatus = "Nada para salvar."; return; }
            await Task.Run(() => SettingsService.Write(item.State.IniPath, dirty));
            await LoadSettingsAsync();
            DetailStatus = $"{dirty.Count} configurações salvas em {item.State.IniPath}. " +
                "Com o jogo aberto: mova o slider Preset para 2 e volte para 1 no overlay (Home) para aplicar sem reiniciar.";
        }
        catch (Exception ex) { DetailStatus = ex.Message; }
    }

    private async Task ApplyProfileAsync()
    {
        var item = Selected;
        if (item?.State is null || item.Mod?.Slug is null) return;
        try
        {
            var defs = _manifest?.GetSettings(item.Mod.Slug);
            if (defs is null) return;
            await Task.Run(() => SettingsService.ApplyDisplayProfile(item.State.IniPath, defs, Config));
            await LoadSettingsAsync();
            DetailStatus = $"Perfil aplicado: pico {Config.PeakNits:0} / jogo {Config.GameNits:0} / UI {Config.UiNits:0} nits.";
        }
        catch (Exception ex) { DetailStatus = ex.Message; }
    }

    private async Task ResetSettingsAsync()
    {
        var item = Selected;
        if (item?.State is null) return;
        try
        {
            await Task.Run(() => SettingsService.Reset(item.State.IniPath, Settings.Select(s => s.Def)));
            await LoadSettingsAsync();
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
        if (Selected?.Mod?.NexusUrl is { } url)
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
