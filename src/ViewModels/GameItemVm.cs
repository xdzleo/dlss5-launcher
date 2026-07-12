using System.IO;
using RenoDXLauncher.Models;
using RenoDXLauncher.Services;

namespace RenoDXLauncher.ViewModels;

public enum ModBadge { None, Available, NexusOnly, Disabled, Enabled }

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
        _ => "Manual",
    };

    public string Key => $"{Game.Store}_{Game.AppId ?? Game.InstallDir}";

    public bool HasMod => Mod != null;
    public bool HasDirectDownload => Mod?.DownloadUrl != null;

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

    public ModBadge Badge =>
        _state?.AddonPath != null
            ? (_state.AddonEnabled ? ModBadge.Enabled : ModBadge.Disabled)
            : Mod is null ? ModBadge.None
            : Mod.DownloadUrl != null ? ModBadge.Available
            : ModBadge.NexusOnly;

    public string BadgeText => Badge switch
    {
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

    /// <summary>Fallback: even without a chosen exe, find an installed addon anywhere in the game dir
    /// (e.g. installed manually or by a previous run) so the grid badge is right.</summary>
    public void DetectExistingInstall()
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
            if (addon != null && TargetDir is null)
            {
                // point the state at the folder where the addon actually lives
                var dir = Path.GetDirectoryName(addon)!;
                State = AddonService.GetState(dir, null);
            }
        }
        catch (Exception ex) { Log.Warn($"detect existing {Name}: {ex.Message}"); }
    }
}
