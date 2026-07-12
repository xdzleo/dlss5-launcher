namespace RenoDXLauncher.Models;

public enum GameStore { Steam, Epic, Gog, Xbox, Manual }

/// <summary>An installed game found by one of the store scanners.</summary>
public class GameInfo
{
    public required string Name { get; init; }
    public required string InstallDir { get; init; }
    public required GameStore Store { get; init; }
    /// <summary>Store-specific id (Steam appid, Epic CatalogItemId, GOG gameID, Xbox package name).</summary>
    public string? AppId { get; init; }
    public int? SteamAppId { get; init; }
    /// <summary>Executable hint from store metadata (Epic LaunchExecutable, Xbox gamelaunchhelper).</summary>
    public string? ExeHint { get; init; }
    public string? LocalCoverPath { get; init; }
}

public enum ModKind { Dedicated, UnrealEngine, UnityEngine }

/// <summary>One entry of the RenoDX mod catalog (games-index.json + wiki Mods.md merged).</summary>
public class CatalogEntry
{
    public required string GameName { get; init; }
    public string NormalizedName { get; set; } = "";
    /// <summary>All normalized names this entry answers to (title variants, games-index aliases,
    /// parenthetical-stripped forms, combined-row splits).</summary>
    public HashSet<string> NormalizedAliases { get; } = new(StringComparer.Ordinal);
    public ModKind Kind { get; init; }
    public string? Maintainer { get; set; }
    /// <summary>Direct .addon64/.addon32 download URL (snapshot build), if available.</summary>
    public string? DownloadUrl { get; set; }
    /// <summary>32 or 64, from artifact arch / URL extension. 0 when unknown.</summary>
    public int AddonBits { get; set; }
    /// <summary>renodx-&lt;slug&gt;.addonXX — used to look up the settings manifest.</summary>
    public string? Slug { get; init; }
    public int? SteamAppId { get; set; }
    public string? NexusUrl { get; set; }
    /// <summary>Fallback link (discussion/Discord) for rows without direct download or Nexus page.</summary>
    public string? InfoUrl { get; set; }
    /// <summary>true = marked working; false = in progress / unknown.</summary>
    public bool Working { get; set; }
    /// <summary>Notes from the wiki (hover note or UE/Unity Notes column).</summary>
    public string? Note { get; set; }
}

/// <summary>State of ReShade + RenoDX inside one game's deploy directory.</summary>
public class ModState
{
    /// <summary>Directory that contains the game's rendering exe (where ReShade + addon live).</summary>
    public required string TargetDir { get; init; }
    public string? ExePath { get; init; }
    public bool ReShadePresent { get; set; }
    public string? ReShadeDllName { get; set; }
    public string? ReShadeVersion { get; set; }
    /// <summary>Full path of the renodx addon file (enabled or disabled variant), if present.</summary>
    public string? AddonPath { get; set; }
    public bool AddonEnabled { get; set; }
    public string IniPath => System.IO.Path.Combine(TargetDir, "ReShade.ini");
}

/// <summary>One RenoDX setting definition (from the embedded settings manifest).</summary>
public class SettingDef
{
    public required string Key { get; init; }
    /// <summary>float | int | bool</summary>
    public string Type { get; init; } = "float";
    public string? Label { get; init; }
    public string? Section { get; init; }
    public string? Tooltip { get; init; }
    public double? Default { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    /// <summary>Combo labels for int settings (index = value).</summary>
    public IReadOnlyList<string>? Labels { get; init; }
    public bool IsGlobal { get; init; }
}
