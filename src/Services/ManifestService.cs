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
}

/// <summary>
/// Loads the embedded settings manifest (generated from renodx source by
/// tools/extract_settings_manifest.py): slug → list of SettingDef.
/// </summary>
public class ManifestService
{
    private readonly Dictionary<string, List<SettingDef>> _bySlug = new(StringComparer.OrdinalIgnoreCase);

    public ManifestService()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().First(n => n.EndsWith("settings_manifest.json"));
        using var stream = asm.GetManifestResourceStream(name)!;
        using var doc = JsonDocument.Parse(stream);
        foreach (var game in doc.RootElement.EnumerateObject())
        {
            var list = new List<SettingDef>();
            foreach (var s in game.Value.EnumerateArray())
            {
                var key = s.GetProperty("key").GetString();
                if (string.IsNullOrEmpty(key)) continue;
                list.Add(new SettingDef
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
                });
            }
            _bySlug[game.Name] = list;
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

    /// <summary>Add settings discovered at runtime (fetched from the maintainer's repo).</summary>
    public void Merge(string slug, IReadOnlyList<SettingDef> settings) => _bySlug[slug] = settings.ToList();
}
