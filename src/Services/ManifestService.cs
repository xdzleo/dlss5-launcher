using System.IO;
using System.Reflection;
using System.Text.Json;
using RenoDXLauncher.Models;

namespace RenoDXLauncher.Services;

public static class AppPaths
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RenoDXLauncher");
    public static string CoversDir { get; } = Path.Combine(DataDir, "covers");
    public static string DownloadsDir { get; } = Path.Combine(DataDir, "downloads");
    public static string ConfigPath { get; } = Path.Combine(DataDir, "config.json");
    /// <summary>Arquivos temporarios do app. Regra do projeto: NADA e escrito em %TEMP%.
    /// Arquivo de nome aleatorio em %TEMP% e o par de atributos que as regras de "binario
    /// suspeito" dos antivirus pontuam, e nao ha razao para pagar esse custo.</summary>
    public static string CacheDir { get; } = Path.Combine(DataDir, "cache");
}

/// <summary>
/// Loads the embedded settings manifest (generated from renodx source by
/// tools/extract_settings_manifest.py): slug → list of SettingDef.
/// </summary>
public class ManifestService
{
    private readonly Dictionary<string, List<SettingDef>> _bySlug = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<SettingDef>> _instructionsBySlug = new(StringComparer.OrdinalIgnoreCase);

    public ManifestService()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().First(n => n.EndsWith("settings_manifest.json"));
        using var stream = asm.GetManifestResourceStream(name)!;
        using var doc = JsonDocument.Parse(stream);
        foreach (var game in doc.RootElement.EnumerateObject())
        {
            var list = new List<SettingDef>();
            var instructions = new List<SettingDef>();
            foreach (var s in game.Value.EnumerateArray())
            {
                var key = s.GetProperty("key").GetString() ?? "";
                bool isInstruction = s.TryGetProperty("instruction", out var ins)
                    && ins.ValueKind == JsonValueKind.True;
                // an instruction block has no key by design — that is what makes it text
                if (!isInstruction && string.IsNullOrEmpty(key)) continue;
                var def = new SettingDef
                {
                    Key = key,
                    Type = GetString(s, "type") ?? "float",
                    Label = GetString(s, "label"),
                    Section = GetString(s, "section"),
                    Tooltip = GetString(s, "tooltip"),
                    Default = GetDouble(s, "default"),
                    Min = GetDouble(s, "min"),
                    Max = GetDouble(s, "max"),
                    Labels = s.TryGetProperty("labels", out var l) && l.ValueKind == JsonValueKind.Array
                        ? l.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
                        : null,
                    IsGlobal = s.TryGetProperty("is_global", out var g) && g.ValueKind == JsonValueKind.True,
                    IsInstruction = isInstruction,
                    PresetValues = s.TryGetProperty("values", out var pv) && pv.ValueKind == JsonValueKind.Object
                        ? pv.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble())
                        : null,
                };
                if (isInstruction) instructions.Add(def); else list.Add(def);
            }
            _bySlug[game.Name] = list;
            _instructionsBySlug[game.Name] = instructions;
        }
    }

    private static string? GetString(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? GetDouble(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    /// <summary>Settings of a mod, or null when the slug is unknown to this build.
    /// An EMPTY list is meaningful: the mod exists and exposes no adjustable option.</summary>
    public IReadOnlyList<SettingDef>? GetSettings(string? slug) =>
        slug != null && _bySlug.TryGetValue(slug, out var list) ? list : null;

    /// <summary>Do we know this mod at all? Distinguishes "mod without options" from
    /// "mod newer than the embedded catalog".</summary>
    public bool KnowsSlug(string? slug) => slug != null && _bySlug.ContainsKey(slug);

    /// <summary>Instruction/preset blocks the mod's author wrote for the player. These are not
    /// knobs, so they are kept apart from GetSettings — but they are exactly the text that tells
    /// someone how to configure the game, and the app used to throw all of it away.</summary>
    public IReadOnlyList<SettingDef> GetInstructions(string? slug) =>
        slug != null && _instructionsBySlug.TryGetValue(slug, out var list)
            ? list : Array.Empty<SettingDef>();

    /// <summary>Add settings discovered at runtime (fetched from the maintainer's repo).</summary>
    public void Merge(string slug, IReadOnlyList<SettingDef> settings)
    {
        _bySlug[slug] = settings.Where(s => !s.IsInstruction).ToList();
        _instructionsBySlug[slug] = settings.Where(s => s.IsInstruction).ToList();
    }
}
