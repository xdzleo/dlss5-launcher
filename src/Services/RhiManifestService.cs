using System.IO;
using System.Net.Http;
using System.Text.Json;
using RenoDXLauncher.Localization;
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
    /// <summary>Id da nota "pagina do mod". Codigo compara por este valor, nunca pelo titulo.</summary>
    public const string ModPageNoteId = "rhi:mod-page";

    private const string Url = "https://raw.githubusercontent.com/RankFTW/RHI/main/manifest.json";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(3);

    private readonly Dictionary<string, string> _installPath = new();
    private readonly Dictionary<string, string> _api = new();
    private readonly Dictionary<string, string> _dllName = new();
    private readonly Dictionary<string, string> _notes = new();
    private readonly Dictionary<string, List<ModNote>> _rich = new();
    private readonly Dictionary<string, string> _reshadeVersion = new();
    private readonly HashSet<string> _nativeHdr = new();
    private readonly HashSet<string> _dlssSkip = new();

    public async Task LoadAsync()
    {
        var cachePath = Path.Combine(AppPaths.DataDir, "rhi_manifest.json");

        // Cada corpo e parseado ANTES de valer alguma coisa: o baixado so vira cache se parseou,
        // e um cache que deixou de parsear e apagado. Antes o download era gravado sem olhar, e
        // um manifesto que quebrava o Parse (ou uma pagina de proxy com 200) ficava tres dias
        // servindo um servico pela metade — e o snapshot embutido, que existe para isso, nunca
        // era consultado, porque so entrava quando NAO havia corpo nenhum.
        if (File.Exists(cachePath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath) < CacheTtl)
        {
            if (await TryParseFileAsync(cachePath, "cache")) return;
            TryDelete(cachePath);
        }

        string? json = null;
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("RenoDXLauncher/1.0");
            json = await http.GetStringAsync(Url);
        }
        catch (Exception ex) { Log.Warn($"RHI manifest fetch: {ex.Message}"); }

        if (json is not null && TryParse(json, "download"))
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                await File.WriteAllTextAsync(cachePath, json);
            }
            catch (Exception ex) { Log.Warn($"RHI manifest cache: {ex.Message}"); }
            return;
        }

        // Sem rede ou com corpo imprestavel: cache vencido, e por ultimo o snapshot embutido.
        if (File.Exists(cachePath))
        {
            if (await TryParseFileAsync(cachePath, "cache vencido")) return;
            TryDelete(cachePath);
        }
        Log.Warn("RHI manifest: usando o snapshot embutido");
        try { TryParse(CatalogService.ReadEmbedded("rhi_manifest.fallback.json"), "embutido"); }
        catch (Exception ex) { Log.Warn($"RHI manifest embedded: {ex.Message}"); }
    }

    private async Task<bool> TryParseFileAsync(string path, string origem)
    {
        try { return TryParse(await File.ReadAllTextAsync(path), origem); }
        catch (Exception ex) { Log.Warn($"RHI manifest read ({origem}): {ex.Message}"); return false; }
    }

    /// <summary>Parse do zero. Um corpo que falha no meio nao deixa metade: as tabelas voltam
    /// vazias para o proximo candidato preencher.</summary>
    private bool TryParse(string json, string origem)
    {
        Clear();
        try
        {
            Parse(json);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"RHI manifest parse ({origem}): {ex.Message}");
            Clear();
            return false;
        }
    }

    private void Clear()
    {
        _installPath.Clear();
        _api.Clear();
        _dllName.Clear();
        _notes.Clear();
        _rich.Clear();
        _reshadeVersion.Clear();
        _nativeHdr.Clear();
        _dlssSkip.Clear();
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) { Log.Warn($"RHI manifest delete {path}: {ex.Message}"); }
    }

    /// <summary>String ou null — nunca lanca. GetString lanca em qualquer outro ValueKind, e um
    /// unico valor fora do formato esperado derrubava o manifesto inteiro.</summary>
    private static string? Str(JsonElement e) =>
        e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    /// <summary>Propriedade de um elemento que PODE nao ser objeto. TryGetProperty lanca num
    /// valor que nao e objeto; aqui vira "nao tem".</summary>
    private static bool TryProp(JsonElement e, string name, out JsonElement value)
    {
        if (e.ValueKind == JsonValueKind.Object) return e.TryGetProperty(name, out value);
        value = default;
        return false;
    }

    private void Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("raiz do manifesto nao e um objeto");
        FillMap(root, "installPathOverrides", _installPath);
        FillMap(root, "graphicsApiOverrides", _api);
        if (root.TryGetProperty("dllNameOverrides", out var dll) && dll.ValueKind == JsonValueKind.Object)
            foreach (var p in dll.EnumerateObject())
                if (TryProp(p.Value, "reshade", out var r) && Str(r) is { Length: > 0 } v)
                    _dllName[MatchService.Normalize(p.Name)] = v;
        if (root.TryGetProperty("gameNotes", out var notes) && notes.ValueKind == JsonValueKind.Object)
            foreach (var p in notes.EnumerateObject())
                if (TryProp(p.Value, "notes", out var n) && Str(n) is { Length: > 0 } v)
                    _notes[MatchService.Normalize(p.Name)] = v;
        if (root.TryGetProperty("nativeHdrGames", out var native) && native.ValueKind == JsonValueKind.Array)
            foreach (var g in native.EnumerateArray())
                if (Str(g) is { Length: > 0 } v)
                    _nativeHdr.Add(MatchService.Normalize(v));

        // Games the index says to keep away from DLSS entirely. Detecting a bundled runtime is not
        // the same as the game being able to use a different one — these are titles where the
        // upstream project already learned that touching the DLSS side breaks something. A list
        // someone maintains against real reports is worth more than any heuristic here.
        if (root.TryGetProperty("dlssSkipGames", out var skip) && skip.ValueKind == JsonValueKind.Array)
            foreach (var g in skip.EnumerateArray())
                if (Str(g) is { Length: > 0 } v)
                    _dlssSkip.Add(MatchService.Normalize(v));

        // The manifest carries 64 top-level keys and this service used to read five. Everything
        // below is per-game guidance that already existed in the file the app downloads.
        // The location string ("ANTES DE INSTALAR") is a sentinel MainViewModel.BuildNotes
        // compares with ==, not screen text — it stays in one language on purpose.
        FillNotes(root, "gameNotes", L.T("Install_Note_IndexTitle"), null);
        FillNotes(root, "reshadeGameInfo", L.T("Install_Note_ReShadeTitle"), "ANTES DE INSTALAR");
        FillNotes(root, "lumaGameNotes", L.T("Install_Note_LumaTitle"), null);
        FillNotes(root, "dxvkGameNotes", L.T("Install_Note_DxvkTitle"), null);

        // installWarnings is keyed by game and then by mod family (renodx / reshade / luma)
        if (root.TryGetProperty("installWarnings", out var warn) && warn.ValueKind == JsonValueKind.Object)
            foreach (var p in warn.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Object))
                foreach (var sub in p.Value.EnumerateObject())
                    if (Str(sub.Value) is { Length: > 0 } text)
                        Add(p.Name, new ModNote(NoteSource.Rhi, NoteKind.Warning,
                            // sub.Name is the mod family key (renodx / reshade / luma), kept raw
                            L.T("Install_Warning_Title", sub.Name),
                            AdviceService.StripSymbols(FirstParagraph(text)), null,
                            RestParagraphs(text), "ANTES DE INSTALAR"));

        // A game pinned to an older ReShade: installing the current build silently breaks the mod.
        if (root.TryGetProperty("legacyReShadeVersions", out var legacy) && legacy.ValueKind == JsonValueKind.Object)
            foreach (var p in legacy.EnumerateObject())
                if (Str(p.Value) is { Length: > 0 } ver)
                {
                    _reshadeVersion[MatchService.Normalize(p.Name)] = ver;
                    Add(p.Name, new ModNote(NoteSource.Rhi, NoteKind.Warning,
                        L.T("Install_ReShadeVersion_Title"),
                        L.T("Install_ReShadeVersion_Text", ver),
                        null, null, "ANTES DE INSTALAR"));
                }

        // The mod is not distributed as a snapshot: the user has to get it from the linked page.
        if (root.TryGetProperty("forceExternalOnly", out var ext) && ext.ValueKind == JsonValueKind.Object)
            foreach (var p in ext.EnumerateObject())
            {
                var url = TryProp(p.Value, "url", out var u) ? Str(u) : null;
                var label = TryProp(p.Value, "label", out var l) ? Str(l) : null;
                if (url is { Length: > 0 })
                    // Worded as an offer, not as a fact about distribution: several of these games
                    // DO have a working snapshot in the wiki, and the old wording contradicted the
                    // app's own enabled Install button. BuildNotes decides which one to show.
                    // O Id (ModPageNoteId) e o que a BuildNotes usa para reconhecer esta nota.
                    // Antes ela comparava o Title, que agora e traduzido — a comparacao teria
                    // quebrado no primeiro idioma diferente do portugues.
                    Add(p.Name, new ModNote(NoteSource.Rhi, NoteKind.Step, L.T("Install_ModPage_Title"),
                        L.T("Install_ModPage_Text"),
                        // label comes from the manifest (already English there); ours is the fallback
                        new[] { new NoteLink(label is { Length: > 0 } ? label : L.T("Install_ModPage_Link"), url) },
                        null, L.T("Install_Section_BeforeInstalling"), ModPageNoteId));
            }

        // Values the index says to force in ReShade.ini for this game.
        if (root.TryGetProperty("renodxIniOverrides", out var ini) && ini.ValueKind == JsonValueKind.Object)
            foreach (var p in ini.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Object))
            {
                var lines = p.Value.EnumerateObject()
                    .Select(kv => $"{kv.Name}={kv.Value.ToString()}")
                    .ToList();
                if (lines.Count > 0)
                    Add(p.Name, new ModNote(NoteSource.Rhi, NoteKind.Step,
                        L.T("Common_RecommendedValues"),
                        L.T("Install_IniOverrides_Text"), null,
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
            if (!p.Value.TryGetProperty("notes", out var n) || Str(n) is not { Length: > 0 } text)
                continue;
            List<NoteLink>? links = null;
            if (p.Value.TryGetProperty("notesUrl", out var u) && Str(u) is { Length: > 0 } url)
            {
                var label = p.Value.TryGetProperty("notesUrlLabel", out var lb)
                    && Str(lb) is { Length: > 0 } lv ? lv : L.T("Common_Open");
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
            if (Str(g) is { Length: > 0 } name)
                Add(name, new ModNote(NoteSource.Rhi, NoteKind.Info,
                    L.T("Install_Architecture_Title"),
                    L.T("Install_Architecture_Text", bits)));
    }

    /// <summary>Sentences describing the RHI app's OWN interface. Copied verbatim they become a
    /// lie in this app's mouth: Max Payne 3's note says the pin "has been automatically set in
    /// Overrides (RS Channel)" — a screen this launcher does not have and a thing it did not do.
    /// </summary>
    private static string StripForeignUi(string text)
    {
        var kept = System.Text.RegularExpressions.Regex
            .Split(text, @"(?<=[.!?])\s+")
            .Where(s => !System.Text.RegularExpressions.Regex.IsMatch(s,
                @"\bOverrides?\b.*\bRS Channel\b|\bRS Channel\b|has been automatically set"
                + @"|in the Overrides panel",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        return string.Join(" ", kept).Trim();
    }

    /// <summary>Config blocks inside a note (dgVoodoo.conf, ini snippets) are separated by a blank
    /// line — keep the prose readable and the block verbatim. Only text that actually LOOKS like a
    /// snippet becomes a monospaced block; prose sent there gets clipped by the no-wrap box.</summary>
    private static string FirstParagraph(string text)
    {
        var norm = StripForeignUi(text.Replace("\r\n", "\n"));
        int split = norm.IndexOf("\n\n", StringComparison.Ordinal);
        var head = (split > 0 ? norm[..split] : norm).Trim();
        // prose tail stays in the body instead of being clipped inside the code box
        var tail = split > 0 ? norm[(split + 2)..].Trim() : "";
        return tail.Length > 0 && !LooksLikeSnippet(tail) ? head + "\n\n" + tail : head;
    }

    private static string? RestParagraphs(string text)
    {
        var norm = StripForeignUi(text.Replace("\r\n", "\n"));
        int split = norm.IndexOf("\n\n", StringComparison.Ordinal);
        if (split <= 0) return null;
        var rest = norm[(split + 2)..].Trim();
        return rest.Length > 0 && LooksLikeSnippet(rest) ? rest : null;
    }

    /// <summary>Lines that must be copied character for character: ini sections, key=value pairs,
    /// launch arguments.</summary>
    private static bool LooksLikeSnippet(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return false;
        int codey = lines.Count(l => System.Text.RegularExpressions.Regex.IsMatch(l.Trim(),
            @"^\[.+\]$|^[\w.]+\s*[:=]\s*\S|^[-+]\w"));
        return codey * 2 >= lines.Length;
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

    /// <summary>The index says to leave this game's DLSS alone — no runtime swap, no neural.</summary>
    public bool SkipsDlss(string gameName) => _dlssSkip.Contains(MatchService.Normalize(gameName));

    /// <summary>Every piece of curated guidance for this game, with links and blocks intact.</summary>
    public IReadOnlyList<ModNote> GameNotes(string gameName) =>
        _rich.GetValueOrDefault(MatchService.Normalize(gameName)) ?? (IReadOnlyList<ModNote>)Array.Empty<ModNote>();

    /// <summary>ReShade version this game is pinned to, when it is not the current one.</summary>
    public string? RequiredReShadeVersion(string gameName) =>
        _reshadeVersion.GetValueOrDefault(MatchService.Normalize(gameName));
}
