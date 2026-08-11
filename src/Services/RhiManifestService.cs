using System.IO;
using System.Net.Http;
using System.Text.Json;
using RenoDXLauncher.Models;

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
    private readonly Dictionary<string, List<ModNote>> _rich = new();
    private readonly Dictionary<string, string> _reshadeVersion = new();
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

        // The manifest carries 64 top-level keys and this service used to read five. Everything
        // below is per-game guidance that already existed in the file the app downloads.
        FillNotes(root, "gameNotes", "Nota do índice", null);
        FillNotes(root, "reshadeGameInfo", "Sobre o ReShade neste jogo", "ANTES DE INSTALAR");
        FillNotes(root, "lumaGameNotes", "Nota (mod Luma)", null);
        FillNotes(root, "dxvkGameNotes", "Nota (DXVK)", null);

        // installWarnings is keyed by game and then by mod family (renodx / reshade / luma)
        if (root.TryGetProperty("installWarnings", out var warn) && warn.ValueKind == JsonValueKind.Object)
            foreach (var p in warn.EnumerateObject())
                foreach (var sub in p.Value.EnumerateObject())
                    if (sub.Value.GetString() is { Length: > 0 } text)
                        Add(p.Name, new ModNote(NoteSource.Rhi, NoteKind.Warning,
                            $"Aviso de instalação ({sub.Name})",
                            AdviceService.StripSymbols(FirstParagraph(text)), null,
                            RestParagraphs(text), "ANTES DE INSTALAR"));

        // A game pinned to an older ReShade: installing the current build silently breaks the mod.
        if (root.TryGetProperty("legacyReShadeVersions", out var legacy) && legacy.ValueKind == JsonValueKind.Object)
            foreach (var p in legacy.EnumerateObject())
                if (p.Value.GetString() is { Length: > 0 } ver)
                {
                    _reshadeVersion[MatchService.Normalize(p.Name)] = ver;
                    Add(p.Name, new ModNote(NoteSource.Rhi, NoteKind.Warning, "Versão do ReShade",
                        $"Este jogo exige o ReShade {ver} — versões mais novas podem não funcionar com o mod.",
                        null, null, "ANTES DE INSTALAR"));
                }

        // The mod is not distributed as a snapshot: the user has to get it from the linked page.
        if (root.TryGetProperty("forceExternalOnly", out var ext) && ext.ValueKind == JsonValueKind.Object)
            foreach (var p in ext.EnumerateObject())
            {
                var url = p.Value.TryGetProperty("url", out var u) ? u.GetString() : null;
                var label = p.Value.TryGetProperty("label", out var l) ? l.GetString() : null;
                if (url is { Length: > 0 })
                    Add(p.Name, new ModNote(NoteSource.Rhi, NoteKind.Step, "Download do mod",
                        "Este mod não é distribuído pelo snapshot automático — baixe na página do autor.",
                        new[] { new NoteLink(label is { Length: > 0 } ? label : "Abrir a página do mod", url) },
                        null, "ANTES DE INSTALAR"));
            }

        // Values the index says to force in ReShade.ini for this game.
        if (root.TryGetProperty("renodxIniOverrides", out var ini) && ini.ValueKind == JsonValueKind.Object)
            foreach (var p in ini.EnumerateObject())
            {
                var lines = p.Value.EnumerateObject()
                    .Select(kv => $"{kv.Name}={kv.Value.ToString()}")
                    .ToList();
                if (lines.Count > 0)
                    Add(p.Name, new ModNote(NoteSource.Rhi, NoteKind.Step, "Valores recomendados",
                        "O índice do RenoDX recomenda estes valores para este jogo:", null,
                        string.Join("\n", lines), "OVERLAY RENODX (Home)"));
            }

        FillBitsNote(root, "thirtyTwoBitGames", 32);
        FillBitsNote(root, "sixtyFourBitGames", 64);
    }

    private void Add(string gameName, ModNote note)
    {
        var key = MatchService.Normalize(gameName);
        if (!_rich.TryGetValue(key, out var list)) _rich[key] = list = new List<ModNote>();
        list.Add(note);
    }

    /// <summary>Read the {notes, notesUrl, notesUrlLabel} shape the manifest uses. Several notes
    /// end in ":" precisely because the link that completes the sentence lives in notesUrl —
    /// dropping it produced dangling text like "Additional information available below:".</summary>
    private void FillNotes(JsonElement root, string prop, string title, string? location)
    {
        if (!root.TryGetProperty(prop, out var el) || el.ValueKind != JsonValueKind.Object) return;
        foreach (var p in el.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.Object) continue;
            if (!p.Value.TryGetProperty("notes", out var n) || n.GetString() is not { Length: > 0 } text)
                continue;
            List<NoteLink>? links = null;
            if (p.Value.TryGetProperty("notesUrl", out var u) && u.GetString() is { Length: > 0 } url)
            {
                var label = p.Value.TryGetProperty("notesUrlLabel", out var lb)
                    && lb.GetString() is { Length: > 0 } lv ? lv : "Abrir";
                links = new List<NoteLink> { new(label, url) };
            }
            Add(p.Name, new ModNote(NoteSource.Rhi,
                text.Contains("⚠") || text.Contains("required", StringComparison.OrdinalIgnoreCase)
                    ? NoteKind.Warning : NoteKind.Info,
                title, AdviceService.StripSymbols(FirstParagraph(text)), links,
                RestParagraphs(text), location));
        }
    }

    private void FillBitsNote(JsonElement root, string prop, int bits)
    {
        if (!root.TryGetProperty(prop, out var el) || el.ValueKind != JsonValueKind.Array) return;
        foreach (var g in el.EnumerateArray())
            if (g.GetString() is { Length: > 0 } name)
                Add(name, new ModNote(NoteSource.Rhi, NoteKind.Info, "Arquitetura",
                    $"Este jogo usa a build {bits}-bit do mod."));
    }

    /// <summary>Config blocks inside a note (dgVoodoo.conf, ini snippets) are separated by a blank
    /// line — keep the prose readable and the block verbatim.</summary>
    private static string FirstParagraph(string text)
    {
        var norm = text.Replace("\r\n", "\n");
        int split = norm.IndexOf("\n\n", StringComparison.Ordinal);
        return (split > 0 ? norm[..split] : norm).Trim();
    }

    private static string? RestParagraphs(string text)
    {
        var norm = text.Replace("\r\n", "\n");
        int split = norm.IndexOf("\n\n", StringComparison.Ordinal);
        if (split <= 0) return null;
        var rest = norm[(split + 2)..].Trim();
        return rest.Length == 0 ? null : rest;
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

    /// <summary>Every piece of curated guidance for this game, with links and blocks intact.</summary>
    public IReadOnlyList<ModNote> GameNotes(string gameName) =>
        _rich.GetValueOrDefault(MatchService.Normalize(gameName)) ?? (IReadOnlyList<ModNote>)Array.Empty<ModNote>();

    /// <summary>ReShade version this game is pinned to, when it is not the current one.</summary>
    public string? RequiredReShadeVersion(string gameName) =>
        _reshadeVersion.GetValueOrDefault(MatchService.Normalize(gameName));
}
