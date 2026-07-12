using System.Globalization;
using System.IO;
using RenoDXLauncher.Models;

namespace RenoDXLauncher.Services;

/// <summary>
/// Reads/writes RenoDX settings in the game's ReShade.ini.
/// Per-preset values live in [renodx-preset1] (the section every mod loads at boot);
/// is_global settings live in [renodx]. Keys are case-sensitive on the mod side, so the
/// exact casing comes from (1) what's already in the ini, then (2) the embedded per-mod
/// manifest generated from renodx source.
/// </summary>
public class SettingsService
{
    public const string PresetSection = "renodx-preset1";
    public const string GlobalSection = "renodx";

    public record SettingValue(SettingDef Def, double? Current, string IniKeyCasing);

    /// <summary>Resolve the effective key casing: existing ini key wins, else manifest key.</summary>
    private static string ResolveKey(IniFile ini, string section, string manifestKey)
    {
        foreach (var kv in ini.GetSection(section))
            if (string.Equals(kv.Key, manifestKey, StringComparison.OrdinalIgnoreCase))
                return kv.Key;
        return manifestKey;
    }

    public static List<SettingValue> Read(string iniPath, IReadOnlyList<SettingDef> defs)
    {
        var ini = new IniFile(iniPath);
        var result = new List<SettingValue>(defs.Count);
        foreach (var def in defs)
        {
            var section = def.IsGlobal ? GlobalSection : PresetSection;
            var key = ResolveKey(ini, section, def.Key);
            var raw = ini.Get(section, key);
            double? current = null;
            if (raw != null && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                current = v;
            result.Add(new SettingValue(def, current, key));
        }
        return result;
    }

    /// <summary>Write changed values. Refuses while the game is running (the overlay
    /// rewrites the whole section on any change and ReShade drops writes on mtime conflicts).</summary>
    public static void Write(string iniPath, IEnumerable<(SettingDef Def, double Value)> changes)
    {
        var dir = Path.GetDirectoryName(iniPath)!;
        if (AddonService.IsGameRunning(dir))
            throw new InvalidOperationException(
                "O jogo está aberto — as configurações seriam sobrescritas pelo overlay. Feche o jogo primeiro.");
        var ini = new IniFile(iniPath);
        foreach (var (def, value) in changes)
        {
            var section = def.IsGlobal ? GlobalSection : PresetSection;
            var key = ResolveKey(ini, section, def.Key);
            string text = def.Type == "float"
                ? value.ToString("0.####", CultureInfo.InvariantCulture)
                : ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture);
            ini.Set(section, key, text);
        }
        ini.Save();
    }

    public static void Reset(string iniPath, IEnumerable<SettingDef> defs)
    {
        var dir = Path.GetDirectoryName(iniPath)!;
        if (AddonService.IsGameRunning(dir))
            throw new InvalidOperationException("O jogo está aberto — feche-o antes de resetar.");
        var ini = new IniFile(iniPath);
        foreach (var def in defs)
        {
            var section = def.IsGlobal ? GlobalSection : PresetSection;
            ini.RemoveKey(section, ResolveKey(ini, section, def.Key));
        }
        ini.Save();
    }

    /// <summary>Apply the user's display profile (peak/game/UI nits) using the mod's real keys.</summary>
    public static void ApplyDisplayProfile(string iniPath, IReadOnlyList<SettingDef> defs, LauncherConfig cfg)
    {
        var changes = new List<(SettingDef, double)>();
        foreach (var def in defs)
        {
            var k = def.Key.ToLowerInvariant();
            if (k == "tonemappeaknits") changes.Add((def, cfg.PeakNits));
            else if (k == "tonemapgamenits") changes.Add((def, cfg.GameNits));
            else if (k == "tonemapuinits") changes.Add((def, cfg.UiNits));
        }
        if (changes.Count > 0) Write(iniPath, changes);
    }
}
