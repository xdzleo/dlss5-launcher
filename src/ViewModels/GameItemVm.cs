using System.IO;
using RenoDXLauncher.Models;
using RenoDXLauncher.Services;

namespace RenoDXLauncher.ViewModels;

public enum ModBadge { None, Available, NexusOnly, Disabled, Enabled, UpdateAvailable }

/// <summary>One tile in the game grid.</summary>
public class GameItemVm : ObservableObject
{
    public GameInfo Game { get; }
    public CatalogEntry? Mod { get; }

    private string? _coverPath;
    private ModState? _state;
    private string? _chosenExe;

    public GameItemVm(GameInfo game, CatalogEntry? mod)
    {
        Game = game;
        Mod = mod;
    }

    public string Name => Game.Name;
    public string StoreLabel => Game.Store switch
    {
        GameStore.Steam => "Steam",
        GameStore.Epic => "Epic",
        GameStore.Gog => "GOG",
        GameStore.Xbox => "Xbox",
        GameStore.Ubisoft => "Ubisoft",
        GameStore.EA => "EA",
        GameStore.BattleNet => "Battle.net",
        GameStore.Rockstar => "Rockstar",
        GameStore.Folder => "Pasta",
        _ => "Manual",
    };

    public string Key => $"{Game.Store}_{Game.AppId ?? Game.InstallDir}";

    public bool HasMod => Mod != null;

    /// <summary>Quem mantém o mod deste jogo (crédito em destaque no modal).</summary>
    public string MaintainerName => string.IsNullOrWhiteSpace(Mod?.Maintainer)
        ? "Comunidade RenoDX" : Mod!.Maintainer!;

    public string MaintainerInitial
    {
        get
        {
            var n = MaintainerName.TrimStart('(', '[', ' ');
            return n.Length > 0 ? char.ToUpperInvariant(n[0]).ToString() : "?";
        }
    }
    public bool HasDirectDownload => Mod?.DownloadUrl != null;

    /// <summary>Selo de status da wiki: estavel (check verde) x em construcao (aviso).</summary>
    public bool ModIsStable => Mod?.Working == true;
    public bool ModIsUnstable => Mod != null && !Mod.Working;
    public string ModStatusText => ModIsStable ? "Estável" : "Instável";
    public string ModStatusTooltip => ModIsStable
        ? "A wiki do RenoDX marca este mod como funcionando"
        : "A wiki do RenoDX marca este mod como em construção — pode ter problemas";

    public string? CoverPath { get => _coverPath; set { if (Set(ref _coverPath, value)) OnPropertyChanged(nameof(HasCover)); } }
    public bool HasCover => _coverPath != null;
    public string Initials
    {
        get
        {
            var words = Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return string.Concat(words.Take(3).Select(w => char.ToUpperInvariant(w[0])));
        }
    }

    /// <summary>Exe the mod targets (pinned by user or best heuristic candidate).</summary>
    public string? ChosenExe
    {
        get => _chosenExe;
        set
        {
            if (Set(ref _chosenExe, value))
            {
                OnPropertyChanged(nameof(TargetDir));
                RefreshState();
            }
        }
    }

    public string? TargetDir => _chosenExe != null ? Path.GetDirectoryName(_chosenExe) : null;

    public ModState? State
    {
        get => _state;
        private set
        {
            _state = value;
            OnPropertyChanged(nameof(State));
            OnPropertyChanged(nameof(Badge));
            OnPropertyChanged(nameof(BadgeText));
            OnPropertyChanged(nameof(IsInstalled));
            OnPropertyChanged(nameof(IsEnabled));
        }
    }

    public bool IsInstalled => _state?.AddonPath != null;
    public bool IsEnabled => _state?.AddonEnabled == true;

    private bool _hasUpdate;
    /// <summary>Uma build mais nova deste mod está disponível no servidor.</summary>
    public bool HasUpdate
    {
        get => _hasUpdate;
        set
        {
            if (Set(ref _hasUpdate, value))
            {
                OnPropertyChanged(nameof(Badge));
                OnPropertyChanged(nameof(BadgeText));
            }
        }
    }

    public ModBadge Badge =>
        _hasUpdate && _state?.AddonPath != null ? ModBadge.UpdateAvailable :
        _state?.AddonPath != null
            ? (_state.AddonEnabled ? ModBadge.Enabled : ModBadge.Disabled)
            : Mod is null ? ModBadge.None
            : Mod.DownloadUrl != null ? ModBadge.Available
            : ModBadge.NexusOnly;

    public string BadgeText => Badge switch
    {
        ModBadge.UpdateAvailable => "ATUALIZAÇÃO",
        ModBadge.Enabled => "ATIVADO",
        ModBadge.Disabled => "DESATIVADO",
        ModBadge.Available => "MOD DISPONÍVEL",
        ModBadge.NexusOnly => "MOD NO NEXUS",
        _ => "SEM MOD",
    };

    public void RefreshState()
    {
        if (TargetDir is null) { State = null; return; }
        State = AddonService.GetState(TargetDir, _chosenExe);
    }

    /// <summary>Apply pre-computed detection results (exe + state) from a background thread.
    /// Must be called on the UI thread; does no disk I/O.</summary>
    public void ApplyDetected(string? exe, ModState? state)
    {
        if (exe != null)
        {
            _chosenExe = exe;
            OnPropertyChanged(nameof(ChosenExe));
            OnPropertyChanged(nameof(TargetDir));
        }
        if (state != null) State = state;
    }

    /// <summary>Find an existing RenoDX install anywhere in the game dir (installed manually or by
    /// a previous run) and the exe that lives beside it. Pure disk I/O — safe on any thread;
    /// feed the result to ApplyDetected on the UI thread.</summary>
    public (string? exe, ModState? state) DetectExistingInstall()
    {
        try
        {
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                MaxRecursionDepth = 5,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };
            var addon = Directory.EnumerateFiles(Game.InstallDir, "renodx-*.addon*", options).FirstOrDefault();
            if (addon is null) return (null, null);
            // the addon's own folder is the deploy dir — pin the exe there so selection,
            // toggle and settings all operate on the real install location
            var dir = Path.GetDirectoryName(addon)!;
            var exe = Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => { try { return new FileInfo(f).Length; } catch { return 0L; } })
                .FirstOrDefault();
            return (exe, AddonService.GetState(dir, exe));
        }
        catch (Exception ex)
        {
            Log.Warn($"detect existing {Name}: {ex.Message}");
            return (null, null);
        }
    }
}
