using System.IO;
using System.Net.Http;
using RenoDXLauncher.Localization;
using RenoDXLauncher.Models;

namespace RenoDXLauncher.Services;

/// <summary>
/// The DLSSFIX addon: fixes DLSS Frame Generation flickering when RenoDX is upgrading the
/// game's buffers from SDR to HDR.
///
/// Why it is needed: the mod converts the buffers to HDR, but the game keeps telling Streamline
/// its color buffers are SDR, so Frame Generation interpolates with SDR math over HDR data. The
/// addon hooks slDLSSSetOptions and forces colorBuffersHDR/useAutoExposure (see renodx
/// src/utils/dlss/streamline_v2.hpp).
///
/// It must NOT be applied blindly: on a game whose buffers really are SDR, forcing
/// colorBuffersHDR would lie to DLSS in the opposite direction and hurt upscaling. So the app
/// only offers it when the mod does upgrade to HDR AND the game ships DLSS/Streamline.
/// </summary>
public static class DlssFixService
{
    public const string AddonUrl =
        "https://github.com/clshortfuse/renodx/releases/download/snapshot/renodx-dlssfix.addon64";
    public const string AddonFile = "renodx-dlssfix.addon64";
    private const string Section = "RENODX-DLSSFIX";

    public record Detection(bool HasFrameGen, string? DlssPath, string? StreamlinePath)
    {
        /// <summary>Game ships the Frame Generation runtime — the case this fix exists for.</summary>
        public bool Relevant => HasFrameGen || StreamlinePath != null;
    }

    /// <summary>Look for the DLSS / Streamline runtime the game ships.</summary>
    public static Detection Detect(string installDir)
    {
        string? dlss = null, streamline = null;
        bool frameGen = false;
        try
        {
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                MaxRecursionDepth = 4,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };
            foreach (var f in Directory.EnumerateFiles(installDir, "*.dll", options))
            {
                var name = Path.GetFileName(f).ToLowerInvariant();
                if (name == "nvngx_dlssg.dll") frameGen = true;                 // Frame Generation
                else if (name == "nvngx_dlss.dll") dlss ??= f;                  // upscaler
                else if (name == "sl.interposer.dll") streamline ??= f;         // Streamline
            }
        }
        catch (Exception ex) { Log.Warn($"dlss detect {installDir}: {ex.Message}"); }
        return new Detection(frameGen, dlss, streamline);
    }

    /// <summary>Only worth offering when the mod upgrades SDR to HDR (that is what confuses
    /// Frame Generation) and the game actually has the DLSS FG runtime.</summary>
    public static bool ShouldOffer(CatalogEntry? mod, Detection detection)
    {
        if (mod is null || !detection.Relevant) return false;
        // generic engine mods are the SDR->HDR upgrade path; dedicated mods for native-HDR
        // games do not lie about the buffer format, so the fix would be a no-op there
        return mod.Kind is ModKind.UnrealEngine or ModKind.UnityEngine;
    }

    public static bool IsInstalled(string targetDir) =>
        File.Exists(Path.Combine(targetDir, AddonFile));

    /// <summary>Download the addon and register it in ReShade.ini with the paths it needs.</summary>
    public static async Task ApplyAsync(string targetDir, Detection detection,
        IProgress<string>? progress = null)
    {
        if (AddonService.IsGameRunning(targetDir))
            throw new InvalidOperationException(L.T("Error_GameRunning"));

        var target = Path.Combine(targetDir, AddonFile);
        if (!File.Exists(target))
        {
            progress?.Report(L.T("Install_DlssFix_Downloading"));
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("RenoDXLauncher/1.0");
            var bytes = await http.GetByteArrayAsync(AddonUrl);
            if (bytes.Length < 4096 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
                throw new InvalidOperationException(L.T("Error_DlssFix_DownloadCorrupt"));
            await File.WriteAllBytesAsync(target, bytes);
        }

        var ini = new IniFile(Path.Combine(targetDir, "ReShade.ini"));
        // LoadFromDllMain is a comma-separated list: append, never clobber another addon
        var current = ini.Get("ADDON", "LoadFromDllMain", ignoreCase: true) ?? "";
        var entries = current.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (!entries.Contains(AddonFile, StringComparer.OrdinalIgnoreCase))
        {
            entries.Add(AddonFile);
            ini.Set("ADDON", "LoadFromDllMain", string.Join(",", entries));
        }
        if (detection.DlssPath != null) ini.Set(Section, "DLSSPath", detection.DlssPath);
        if (detection.StreamlinePath != null) ini.Set(Section, "StreamlinePath", detection.StreamlinePath);
        ini.Save();
        progress?.Report(L.T("Install_DlssFix_Applied"));
    }

    /// <summary>Undo: remove the addon file and its ReShade.ini entries.</summary>
    public static void Remove(string targetDir)
    {
        if (AddonService.IsGameRunning(targetDir))
            throw new InvalidOperationException(L.T("Error_GameRunning"));

        var target = Path.Combine(targetDir, AddonFile);
        if (File.Exists(target)) File.Delete(target);

        var iniPath = Path.Combine(targetDir, "ReShade.ini");
        if (!File.Exists(iniPath)) return;
        var ini = new IniFile(iniPath);
        var current = ini.Get("ADDON", "LoadFromDllMain", ignoreCase: true);
        if (current != null)
        {
            var kept = current.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(e => !e.Equals(AddonFile, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (kept.Count > 0) ini.Set("ADDON", "LoadFromDllMain", string.Join(",", kept));
            else ini.RemoveKey("ADDON", "LoadFromDllMain");
        }
        ini.RemoveKey(Section, "DLSSPath");
        ini.RemoveKey(Section, "StreamlinePath");
        ini.Save();
    }
}
