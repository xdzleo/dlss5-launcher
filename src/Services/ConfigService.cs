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

    /// <summary>Idioma da interface, como tag BCP-47 ("pt-BR", "en"). Vazio ou ausente
    /// significa "seguir o Windows", que e o padrao e o que quase todo mundo quer.</summary>
    public string? Language { get; set; }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static LauncherConfig Load()
    {
        try
        {
            if (File.Exists(AppPaths.ConfigPath))
                return JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(AppPaths.ConfigPath)) ?? new();
        }
        catch (Exception ex)
        {
            // corrupt config: keep a copy instead of silently discarding the user's settings
            Log.Warn($"config load FALHOU (mantendo backup): {ex.Message}");
            try { File.Copy(AppPaths.ConfigPath, AppPaths.ConfigPath + ".corrupt", overwrite: true); }
            catch { }
        }
        return new LauncherConfig();
    }

    private static readonly object SaveGate = new();

    /// <summary>Atomic save: write to a temp file then swap, so a crash/full disk mid-write
    /// can never leave a truncated config.json (which Load would silently reset to defaults,
    /// losing the user's nits profile and pinned exes).</summary>
    public void Save()
    {
        try
        {
            lock (SaveGate)
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                var json = JsonSerializer.Serialize(this, JsonOpts);
                var temp = AppPaths.ConfigPath + ".tmp";
                File.WriteAllText(temp, json);
                if (File.Exists(AppPaths.ConfigPath)) File.Replace(temp, AppPaths.ConfigPath, null);
                else File.Move(temp, AppPaths.ConfigPath);
            }
        }
        catch (Exception ex) { Log.Warn($"config save: {ex.Message}"); }
    }
}
