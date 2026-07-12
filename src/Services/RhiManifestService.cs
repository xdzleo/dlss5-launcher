using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace RenoDXLauncher.Services;

/// <summary>
/// Curated per-game deployment knowledge, consumed at runtime from the RHI project's
/// manifest (github.com/RankFTW/RHI — GPL-3.0, data credited in the About screen), with an
/// embedded snapshot fallback: install subdirs, graphics-API/DLL-name overrides,
/// native-HDR game list and human notes. All lookups are by normalized game name.
/// </summary>
public class RhiManifestService
{
    private const string Url = "https://raw.githubusercontent.com/RankFTW/RHI/main/manifest.json";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(3);

    private readonly Dictionary<string, string> _installPath = new();
    private readonly Dictionary<string, string> _api = new();
    private readonly Dictionary<string, string> _dllName = new();
    private readonly Dictionary<string, string> _notes = new();
    private readonly HashSet<string> _nativeHdr = new();

    public async Task LoadAsync()
    {
        string? json = null;
        var cachePath = Path.Combine(AppPaths.DataDir, "rhi_manifest.json");
        try
        {
            if (File.Exists(cachePath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath) < CacheTtl)
            {
                json = await File.ReadAllTextAsync(cachePath);
            }
            else
            {
                using var http = new HttpClient();
                http.DefaultRequestHeaders.UserAgent.ParseAdd("RenoDXLauncher/1.0");
                json = await http.GetStringAsync(Url);
                Directory.CreateDirectory(AppPaths.DataDir);
                await File.WriteAllTextAsync(cachePath, json);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"RHI manifest fetch: {ex.Message}");
            if (File.Exists(cachePath)) json = await File.ReadAllTextAsync(cachePath);
        }
        try
        {
            json ??= CatalogService.ReadEmbedded("rhi_manifest.fallback.json");
            Parse(json);
        }
        catch (Exception ex) { Log.Warn($"RHI manifest parse: {ex.Message}"); }
    }

    private void Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        FillMap(root, "installPathOverrides", _installPath);
        FillMap(root, "graphicsApiOverrides", _api);
        if (root.TryGetProperty("dllNameOverrides", out var dll) && dll.ValueKind == JsonValueKind.Object)
            foreach (var p in dll.EnumerateObject())
                if (p.Value.TryGetProperty("reshade", out var r) && r.GetString() is { Length: > 0 } v)
                    _dllName[MatchService.Normalize(p.Name)] = v;
        if (root.TryGetProperty("gameNotes", out var notes) && notes.ValueKind == JsonValueKind.Object)
            foreach (var p in notes.EnumerateObject())
                if (p.Value.TryGetProperty("notes", out var n) && n.GetString() is { Length: > 0 } v)
                    _notes[MatchService.Normalize(p.Name)] = v;
        if (root.TryGetProperty("nativeHdrGames", out var native) && native.ValueKind == JsonValueKind.Array)
            foreach (var g in native.EnumerateArray())
                if (g.GetString() is { Length: > 0 } v)
                    _nativeHdr.Add(MatchService.Normalize(v));
    }

    private static void FillMap(JsonElement root, string prop, Dictionary<string, string> map)
    {
        if (!root.TryGetProperty(prop, out var el) || el.ValueKind != JsonValueKind.Object) return;
        foreach (var p in el.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 } v)
                map[MatchService.Normalize(p.Name)] = v;
    }

    public string? InstallSubdir(string gameName) => _installPath.GetValueOrDefault(MatchService.Normalize(gameName));
    public string? GraphicsApi(string gameName) => _api.GetValueOrDefault(MatchService.Normalize(gameName));
    public string? DllNameOverride(string gameName) => _dllName.GetValueOrDefault(MatchService.Normalize(gameName));
    public string? GameNote(string gameName) => _notes.GetValueOrDefault(MatchService.Normalize(gameName));
    public bool IsNativeHdr(string gameName) => _nativeHdr.Contains(MatchService.Normalize(gameName));
}
