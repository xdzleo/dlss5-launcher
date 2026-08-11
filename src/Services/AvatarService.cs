using System.IO;
using System.Net.Http;
using RenoDXLauncher.Models;

namespace RenoDXLauncher.Services;

/// <summary>
/// The mod author's GitHub photo.
///
/// The catalog only gives display names ("Musa", "OopyDoopy (Jon)"), never GitHub handles — but
/// the artifact URL points at the fork that builds the mod, which IS the author's account
/// (oopydoopy.github.io → oopydoopy). So the map is derived from the catalog itself.
///
/// One trap this avoids: mods built in the MAIN repo resolve to clshortfuse no matter who
/// maintains them. Showing ShortFuse's face for someone else's mod is worse than showing an
/// initial, so a non-main fork is required unless the author really is ShortFuse.
/// </summary>
public static class AvatarService
{
    private static readonly Dictionary<string, string> UserByMaintainer = new(StringComparer.OrdinalIgnoreCase);
    private const string MainRepoOwner = "clshortfuse";

    /// <summary>Learn maintainer → GitHub user from every catalog entry that has both.</summary>
    public static void Learn(IEnumerable<CatalogEntry> catalog)
    {
        // maintainer -> owner -> hits, so the most frequent fork wins
        var tally = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in catalog)
        {
            if (string.IsNullOrWhiteSpace(e.Maintainer)) continue;
            if (ModHistoryService.RepoOf(e) is not var (owner, _)) continue;
            if (!tally.TryGetValue(e.Maintainer, out var owners))
                tally[e.Maintainer] = owners = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            owners[owner] = owners.GetValueOrDefault(owner) + 1;
        }

        lock (UserByMaintainer)
        {
            UserByMaintainer.Clear();
            foreach (var (maintainer, owners) in tally)
            {
                // prefer the author's own fork; the main repo hosts everyone's mods
                var own = owners.Where(kv => !kv.Key.Equals(MainRepoOwner, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(kv => kv.Value)
                    .Select(kv => kv.Key)
                    .FirstOrDefault();
                if (own != null) UserByMaintainer[maintainer] = own;
                else if (maintainer.Contains("ShortFuse", StringComparison.OrdinalIgnoreCase))
                    UserByMaintainer[maintainer] = MainRepoOwner;
                // else: only the main repo and not ShortFuse — unknown, keep the initial
            }
        }
    }

    public static string? UserOf(CatalogEntry? mod)
    {
        if (mod?.Maintainer is not { Length: > 0 } m) return null;
        lock (UserByMaintainer)
            return UserByMaintainer.TryGetValue(m, out var user) ? user : null;
    }

    /// <summary>Local path of the author's avatar, downloading it once. Null when unknown.</summary>
    public static async Task<string?> GetAvatarAsync(CatalogEntry? mod)
    {
        var user = UserOf(mod);
        if (user is null) return null;
        try
        {
            var dir = Path.Combine(AppPaths.DataDir, "avatars");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{user}.png");
            if (File.Exists(path) && new FileInfo(path).Length > 200) return path;
            var miss = path + ".miss";
            if (File.Exists(miss) && DateTime.UtcNow - File.GetLastWriteTimeUtc(miss) < TimeSpan.FromDays(7))
                return null;

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("RenoDXLauncher/1.0");
            var bytes = await http.GetByteArrayAsync($"https://github.com/{user}.png?size=96");
            if (bytes.Length < 200)
            {
                await File.WriteAllBytesAsync(miss, Array.Empty<byte>());
                return null;
            }
            var temp = path + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes);
            File.Move(temp, path, overwrite: true);
            return path;
        }
        catch (Exception ex)
        {
            Log.Warn($"avatar {user}: {ex.Message}");
            return null;
        }
    }

    public static string? ProfileUrl(CatalogEntry? mod) =>
        UserOf(mod) is { } user ? $"https://github.com/{user}" : null;
}
