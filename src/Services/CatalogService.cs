using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using RenoDXLauncher.Models;

namespace RenoDXLauncher.Services;

/// <summary>
/// The RenoDX mod catalog. Two layered sources:
///  1. PRIMARY: https://clshortfuse.github.io/renodx/games-index.json (machine-readable,
///     main-repo mods only, includes steam_appid for exact matching).
///  2. AUGMENTER: the wiki Mods.md (complete human catalog — fork-hosted mods, Nexus-only
///     mods, the Unreal/Unity generic-mod game lists, status and notes).
/// Merged per slug by matching titles; wiki "Deprecated" section becomes a deny-list so
/// games-index can't resurrect dead mods as working.
/// </summary>
public partial class CatalogService
{
    public const string GamesIndexUrl = "https://clshortfuse.github.io/renodx/games-index.json";
    public const string GamesIndexBase = "https://clshortfuse.github.io/renodx/";
    public const string ModsMdUrl = "https://raw.githubusercontent.com/wiki/clshortfuse/renodx/Mods.md";
    private const string UnrealAddonUrl = "https://clshortfuse.github.io/renodx/renodx-unrealengine.addon64";
    private const string UnityAddon64Url = "https://notvoosh.github.io/renodx-unity/renodx-unityengine.addon64";
    private const string UnityAddon32Url = "https://notvoosh.github.io/renodx-unity/renodx-unityengine.addon32";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(12);

    public async Task<List<CatalogEntry>> LoadAsync(bool forceRefresh = false)
    {
        var mdTask = FetchCachedAsync("Mods.md", ModsMdUrl, "Mods.fallback.md", forceRefresh);
        var idxTask = FetchCachedAsync("games-index.json", GamesIndexUrl, "games-index.fallback.json", forceRefresh);
        var md = await mdTask;
        var deprecatedSlugs = ExtractDeprecatedSlugs(md);
        var entries = ParseModsMd(md);
        foreach (var e in entries)
            SeedAliases(e);
        MergeGamesIndex(entries, await idxTask, deprecatedSlugs);
        return entries;
    }

    private static void SeedAliases(CatalogEntry e)
    {
        e.NormalizedName = MatchService.Normalize(e.GameName);
        e.NormalizedAliases.Add(e.NormalizedName);
        // combined rows ("Shadow Warrior (2013) · Shadow Warrior 2", "A & B") answer to each part
        foreach (var part in e.GameName.Split('·'))
        {
            var p = MatchService.Normalize(part);
            if (p.Length >= 4) e.NormalizedAliases.Add(p);
        }
        var strippedEdition = MatchService.Normalize(MatchService.StripEditionSuffix(e.GameName));
        if (strippedEdition.Length >= 4) e.NormalizedAliases.Add(strippedEdition);
        e.NormalizedAliases.Remove("");
    }

    /// <summary>Download with a disk cache (TTL 12h) and an embedded-resource fallback.</summary>
    private static async Task<string> FetchCachedAsync(string cacheName, string url, string fallbackResource, bool force)
    {
        var cachePath = Path.Combine(AppPaths.DataDir, cacheName);
        var fresh = File.Exists(cachePath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath) < CacheTtl;
        if (!force && fresh) return await File.ReadAllTextAsync(cachePath);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("RenoDXLauncher/1.0");
            var text = await http.GetStringAsync(url);
            Directory.CreateDirectory(AppPaths.DataDir);
            await File.WriteAllTextAsync(cachePath, text);
            return text;
        }
        catch (Exception ex)
        {
            Log.Warn($"catalog fetch {url}: {ex.Message}");
            if (File.Exists(cachePath)) return await File.ReadAllTextAsync(cachePath);
            return ReadEmbedded(fallbackResource);
        }
    }

    internal static string ReadEmbedded(string suffix)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().First(n => n.EndsWith(suffix));
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // ---------- games-index.json ----------

    private static void MergeGamesIndex(List<CatalogEntry> entries, string json, HashSet<string> deprecatedSlugs)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var bySlug = entries
                .Where(e => e.Slug != null && e.Kind == ModKind.Dedicated)
                .GroupBy(e => e.Slug!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            foreach (var game in doc.RootElement.GetProperty("games").EnumerateArray())
            {
                var title = game.GetProperty("title").GetString();
                if (title is null) continue;
                int? steamAppId = game.TryGetProperty("steam_appid", out var sa) && sa.ValueKind == JsonValueKind.Number
                    ? sa.GetInt32() : null;
                var aliases = new List<string>();
                if (game.TryGetProperty("aliases", out var al) && al.ValueKind == JsonValueKind.Array)
                    foreach (var a in al.EnumerateArray())
                        if (a.GetString() is { Length: > 0 } s) aliases.Add(s);

                if (!game.TryGetProperty("mods", out var mods) || mods.GetArrayLength() == 0) continue;
                // prefer the primary (non-"Alternate") variant
                JsonElement mod = mods[0];
                foreach (var m in mods.EnumerateArray())
                    if (!m.TryGetProperty("variant", out var v) || v.ValueKind == JsonValueKind.Null)
                    { mod = m; break; }
                var slug = mod.GetProperty("id").GetString();
                if (slug is null) continue;

                string? url = null;
                int bits = 0;
                if (mod.TryGetProperty("artifacts", out var arts) && arts.GetArrayLength() > 0)
                {
                    JsonElement best = arts[0];
                    foreach (var a in arts.EnumerateArray())
                        if (a.TryGetProperty("arch", out var arch) && arch.GetString() == "x64") best = a;
                    var name = best.GetProperty("name").GetString();
                    if (name != null)
                    {
                        url = GamesIndexBase + name;
                        bits = name.EndsWith(".addon32", StringComparison.OrdinalIgnoreCase) ? 32 : 64;
                    }
                }

                var normTitle = MatchService.Normalize(title);
                var wikiSiblings = bySlug.GetValueOrDefault(slug);
                var target = wikiSiblings?.FirstOrDefault(e => e.NormalizedAliases.Contains(normTitle));
                if (target != null)
                {
                    // enrich the wiki entry whose NAME matches this games-index game
                    target.SteamAppId ??= steamAppId;
                    if (url != null && target.DownloadUrl is null)
                    {
                        target.DownloadUrl = url;
                        target.AddonBits = bits;
                    }
                    foreach (var a in aliases) target.NormalizedAliases.Add(MatchService.Normalize(a));
                    target.NormalizedAliases.Remove("");
                }
                else if (url != null)
                {
                    // index title unknown to the wiki (or shares a slug under another name):
                    // add it as its own entry so the game stays matchable
                    var entry = new CatalogEntry
                    {
                        GameName = title,
                        Kind = ModKind.Dedicated,
                        Slug = slug,
                        DownloadUrl = url,
                        AddonBits = bits,
                        SteamAppId = steamAppId,
                        Working = !deprecatedSlugs.Contains(slug),
                        Note = deprecatedSlugs.Contains(slug)
                            ? "Marcado como DESCONTINUADO na wiki do RenoDX — pode não funcionar mais."
                            : null,
                        Maintainer = wikiSiblings?.FirstOrDefault()?.Maintainer,
                        NexusUrl = wikiSiblings?.FirstOrDefault()?.NexusUrl,
                    };
                    SeedAliases(entry);
                    foreach (var a in aliases) entry.NormalizedAliases.Add(MatchService.Normalize(a));
                    entry.NormalizedAliases.Remove("");
                    entries.Add(entry);
                }
            }
        }
        catch (Exception ex) { Log.Warn($"games-index parse: {ex.Message}"); }
    }

    // ---------- Mods.md ----------

    [GeneratedRegex(@"\]\((https?://[^)\s""]+)")]
    private static partial Regex LinkTargetRegex();

    [GeneratedRegex(@"renodx-([\w.\-]+)\.addon(32|64)")]
    private static partial Regex AddonFileRegex();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex MdLinkRegex();

    [GeneratedRegex(@"\(#\s+""([^""]+)""\)")]
    private static partial Regex HoverNoteRegex();

    [GeneratedRegex(@"\b32[\s-]?bits?\b", RegexOptions.IgnoreCase)]
    private static partial Regex ThirtyTwoBitRegex();

    private static bool IsWorkingStatus(string status) =>
        status.Contains("white_check_mark") || status.Contains('✅');

    /// <summary>Slugs listed under the wiki's "# Deprecated mods" tail section.</summary>
    public static HashSet<string> ExtractDeprecatedSlugs(string md)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int start = md.IndexOf("# Deprecated", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return result;
        foreach (Match m in AddonFileRegex().Matches(md[start..]))
            result.Add(m.Groups[1].Value);
        return result;
    }

    public static List<CatalogEntry> ParseModsMd(string md)
    {
        var entries = new List<CatalogEntry>();
        int cut = md.IndexOf("# Related Mods", StringComparison.OrdinalIgnoreCase);
        if (cut > 0) md = md[..cut];

        int ueStart = md.IndexOf("### Unreal Engine", StringComparison.OrdinalIgnoreCase);
        int unityStart = md.IndexOf("### Unity Engine", StringComparison.OrdinalIgnoreCase);
        string mainPart = ueStart > 0 ? md[..ueStart] : md;
        string uePart = ueStart > 0 ? md[ueStart..(unityStart > ueStart ? unityStart : md.Length)] : "";
        string unityPart = unityStart > 0 ? md[unityStart..] : "";

        foreach (var cells in TableRows(mainPart, minCells: 4))
            AddDedicated(entries, cells);
        foreach (var cells in TableRows(uePart, minCells: 2))
            AddGeneric(entries, cells, ModKind.UnrealEngine);
        foreach (var cells in TableRows(unityPart, minCells: 2))
            AddGeneric(entries, cells, ModKind.UnityEngine);
        return entries;
    }

    private static void AddDedicated(List<CatalogEntry> entries, string[] cells)
    {
        var name = CleanName(cells[0]);
        if (name.Length == 0) return;
        var links = cells[2];
        string? download = null;
        string? slug = null;
        int bits = 0;
        var allUrls = LinkTargetRegex().Matches(links).Select(m => m.Groups[1].Value).ToList();
        foreach (var url in allUrls)
        {
            var fm = AddonFileRegex().Match(url);
            if (fm.Success && url.EndsWith(".addon" + fm.Groups[2].Value, StringComparison.OrdinalIgnoreCase))
            {
                download = url;
                slug = fm.Groups[1].Value;
                bits = int.Parse(fm.Groups[2].Value);
                break;
            }
        }
        string? nexus = allUrls.FirstOrDefault(u => u.Contains("nexusmods.com", StringComparison.OrdinalIgnoreCase));
        // rows with neither a direct download nor a Nexus page (Discord/discussion-only mods)
        // are kept — the InfoUrl gives the user somewhere to go
        string? info = allUrls.FirstOrDefault(u =>
            !u.Contains("img.shields.io") && u != download && u != nexus);
        var status = cells[3];
        entries.Add(new CatalogEntry
        {
            GameName = name,
            Kind = ModKind.Dedicated,
            Maintainer = CleanName(cells[1]),
            DownloadUrl = download,
            Slug = slug,
            AddonBits = bits,
            NexusUrl = nexus,
            InfoUrl = info,
            Working = IsWorkingStatus(status),
            Note = HoverNoteRegex().Match(status) is { Success: true } h ? h.Groups[1].Value : null,
        });
    }

    private static void AddGeneric(List<CatalogEntry> entries, string[] cells, ModKind kind)
    {
        var name = CleanName(cells[0]);
        if (name.Length == 0) return;
        var status = cells.Length > 1 ? cells[1] : "";
        var note = cells.Length > 2 ? CleanNote(cells[2]) : null;
        bool is32 = note != null && ThirtyTwoBitRegex().IsMatch(note);
        entries.Add(new CatalogEntry
        {
            GameName = name,
            Kind = kind,
            Maintainer = kind == ModKind.UnrealEngine ? "Mod genérico Unreal" : "Mod genérico Unity",
            DownloadUrl = kind == ModKind.UnrealEngine ? UnrealAddonUrl : (is32 ? UnityAddon32Url : UnityAddon64Url),
            Slug = kind == ModKind.UnrealEngine ? "unrealengine" : "unityengine",
            AddonBits = kind == ModKind.UnrealEngine ? 64 : (is32 ? 32 : 64),
            Working = IsWorkingStatus(status),
            Note = note,
        });
    }

    private static IEnumerable<string[]> TableRows(string part, int minCells)
    {
        foreach (var raw in part.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (!line.StartsWith('|')) continue;
            if (line.Contains("---")) continue; // separator row
            var cells = line.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (cells.Length < minCells) continue;
            if (cells[0].Equals("Name", StringComparison.OrdinalIgnoreCase)) continue;
            yield return cells;
        }
    }

    private static string CleanName(string cell)
    {
        var s = MdLinkRegex().Replace(cell, m => m.Groups[1].Value);
        s = Regex.Replace(s, @"!\[[^\]]*\]", "");
        s = Regex.Replace(s, @":[a-z_0-9]+:", "");
        s = WebUtility.HtmlDecode(s);
        return s.Trim();
    }

    private static string? CleanNote(string cell)
    {
        var s = cell.Replace("<br>", "\n").Replace("</details>", "");
        s = MdLinkRegex().Replace(s, m => m.Groups[1].Value);
        s = Regex.Replace(s, @":[a-z_0-9]+:", "");
        s = WebUtility.HtmlDecode(s).Trim();
        return s.Length == 0 ? null : s;
    }
}
