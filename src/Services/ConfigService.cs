using System.IO;
using System.Text.Json;

namespace RenoDXLauncher.Services;

/// <summary>Launcher config: the user's display HDR profile + per-game pinned exe choices.</summary>
public class LauncherConfig
{
    /// <summary>Display peak brightness in nits (Windows HDR Calibration clipping point).</summary>
    public double PeakNits { get; set; } = 1000;
    /// <summary>Paper white / 100% white (ITU BT.2408 reference = 203).</summary>
    public double GameNits { get; set; } = 203;
    public double UiNits { get; set; } = 203;
    /// <summary>Write the display profile into ReShade.ini right after installing a mod.</summary>
    public bool ApplyProfileOnInstall { get; set; } = true;
    /// <summary>game key (store_appid or install dir) → chosen exe path.</summary>
    public Dictionary<string, string> PinnedExes { get; set; } = new();
    /// <summary>Manually added game folders.</summary>
    public List<string> ManualGameDirs { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static LauncherConfig Load()
    {
        try
        {
            if (File.Exists(AppPaths.ConfigPath))
                return JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(AppPaths.ConfigPath)) ?? new();
        }
        catch (Exception ex) { Log.Warn($"config load: {ex.Message}"); }
        return new LauncherConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(AppPaths.ConfigPath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch (Exception ex) { Log.Warn($"config save: {ex.Message}"); }
    }
}
