using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using RenoDXLauncher.Models;

namespace RenoDXLauncher.Services;

/// <summary>One published change to a game's mod.</summary>
public record ModRevision(DateTime Date, string Author, string Summary, string? Url)
{
    public string DateText => Date.ToLocalTime().ToString("dd/MM/yyyy");
    /// <summary>Conventional-commit noise ("feat(dyinglight): ") removed for reading.</summary>
    public string CleanSummary => Regex.Replace(Summary, @"^\w+(\([^)]*\))?!?:\s*", "").Trim();
}

/// <summary>
/// Version history of a mod. Each RenoDX mod is a folder in its maintainer's repo
/// (src/games/&lt;slug&gt;), so the commits touching that folder ARE the mod's changelog —
/// there is no other published version list. The maintainer's fork is derived from the
/// artifact URL the catalog already gives us.
/// </summary>
public static partial class ModHistoryService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(12);

    [GeneratedRegex(@"^https://([\w.-]+)\.github\.io/([\w.-]+)/", RegexOptions.IgnoreCase)]
    private static partial Regex PagesRegex();

    [GeneratedRegex(@"^https://github\.com/([\w.-]+)/([\w.-]+)/releases/", RegexOptions.IgnoreCase)]
    private static partial Regex ReleaseRegex();

    /// <summary>owner/repo that builds this addon, from its download URL.</summary>
    public static (string owner, string repo)? RepoOf(CatalogEntry entry)
    {
        if (entry.DownloadUrl is null) return null;
        if (PagesRegex().Match(entry.DownloadUrl) is { Success: true } p)
            return (p.Groups[1].Value, p.Groups[2].Value);
        if (ReleaseRegex().Match(entry.DownloadUrl) is { Success: true } r)
            return (r.Groups[1].Value, r.Groups[2].Value);
        return null;
    }

    public static string? WebUrl(CatalogEntry entry)
    {
        if (RepoOf(entry) is not var (owner, repo) || entry.Slug is null) return null;
        return $"https://github.com/{owner}/{repo}/commits/main/src/games/{entry.Slug}";
    }

    /// <summary>Commits that touched this mod's folder, newest first. Cached on disk because
    /// the anonymous GitHub API allows only 60 requests an hour.</summary>
    public static async Task<IReadOnlyList<ModRevision>> GetAsync(CatalogEntry entry)
    {
        if (entry.Slug is null || RepoOf(entry) is not var (owner, repo))
            return Array.Empty<ModRevision>();

        var cachePath = Path.Combine(AppPaths.DataDir, "history", $"{owner}_{repo}_{entry.Slug}.json");
        try
        {
            if (File.Exists(cachePath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath) < CacheTtl)
                return Parse(await File.ReadAllTextAsync(cachePath));

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("RenoDXLauncher/1.0");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            var url = $"https://api.github.com/repos/{owner}/{repo}/commits" +
                      $"?path=src/games/{entry.Slug}&per_page=40";
            var json = await http.GetStringAsync(url);

            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            await File.WriteAllTextAsync(cachePath, json);
            return Parse(json);
        }
        catch (Exception ex)
        {
            Log.Warn($"history {entry.Slug}: {ex.Message}");
            // rate-limited or offline: an expired cache is still better than nothing
            try
            {
                if (File.Exists(cachePath)) return Parse(await File.ReadAllTextAsync(cachePath));
            }
            catch { }
            return Array.Empty<ModRevision>();
        }
    }

    private static IReadOnlyList<ModRevision> Parse(string json)
    {
        var list = new List<ModRevision>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var c in doc.RootElement.EnumerateArray())
            {
                var commit = c.GetProperty("commit");
                var author = commit.GetProperty("author");
                if (!author.TryGetProperty("date", out var d) || !d.TryGetDateTime(out var date)) continue;
                var name = author.TryGetProperty("name", out var n) ? n.GetString() ?? "?" : "?";
                var msg = commit.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                var first = msg.Split('\n')[0].Trim();
                var web = c.TryGetProperty("html_url", out var h) ? h.GetString() : null;
                list.Add(new ModRevision(date, name, first, web));
            }
        }
        catch (Exception ex) { Log.Warn($"history parse: {ex.Message}"); }
        // the API returns newest-first, but a fork's history can be out of order after rebases
        return list.OrderByDescending(r => r.Date).ToList();
    }
}
