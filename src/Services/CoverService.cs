using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using RenoDXLauncher.Models;

namespace RenoDXLauncher.Services;

/// <summary>
/// Cover art, local-first. Modern Steam stores the library art as
/// <c>librarycache/&lt;appid&gt;/&lt;hash&gt;/library_capsule.jpg</c> (older builds used flat
/// <c>&lt;appid&gt;_library_600x900.jpg</c>); Xbox/Game Pass ships its own images inside the game
/// folder, named by MicrosoftGame.config's ShellVisuals. Only when nothing local exists do we
/// hit the Steam CDN — which 404s for plenty of newer appids.
/// </summary>
public static partial class CoverService
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("RenoDXLauncher/1.0");
        return c;
    }

    [GeneratedRegex(@"<ShellVisuals[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ShellVisualsRegex();

    public static async Task<string?> GetCoverAsync(GameInfo game, int? steamAppIdHint)
    {
        try
        {
            if (game.LocalCoverPath != null && File.Exists(game.LocalCoverPath))
                return game.LocalCoverPath;

            if (FindLocalCover(game, steamAppIdHint) is { } local) return local;

            var appId = game.SteamAppId ?? steamAppIdHint;
            if (appId is null) return null;

            Directory.CreateDirectory(AppPaths.CoversDir);
            var cached = Path.Combine(AppPaths.CoversDir, $"steam_{appId}.jpg");
            if (File.Exists(cached)) return cached;
            var miss = cached + ".miss";
            if (File.Exists(miss) && DateTime.UtcNow - File.GetLastWriteTimeUtc(miss) < TimeSpan.FromDays(7))
                return null;

            foreach (var url in new[]
            {
                $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{appId}/library_600x900.jpg",
                $"https://shared.steamstatic.com/store_item_assets/steam/apps/{appId}/library_600x900.jpg",
                $"https://steamcdn-a.akamaihd.net/steam/apps/{appId}/library_600x900_2x.jpg",
                $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg",
            })
            {
                try
                {
                    var bytes = await Http.GetByteArrayAsync(url);
                    if (bytes.Length < 500) continue;
                    // write then move: a torn file would be cached forever as a broken image
                    var temp = cached + ".tmp";
                    await File.WriteAllBytesAsync(temp, bytes);
                    File.Move(temp, cached, overwrite: true);
                    return cached;
                }
                catch { /* try next url */ }
            }
            await File.WriteAllBytesAsync(miss, Array.Empty<byte>());
        }
        catch (Exception ex) { Log.Warn($"cover {game.Name}: {ex.Message}"); }
        return null;
    }

    /// <summary>Cover already on disk, put there by the store itself.</summary>
    private static string? FindLocalCover(GameInfo game, int? steamAppIdHint)
    {
        var appId = game.SteamAppId ?? steamAppIdHint;
        if (appId is not null && StoreScanners.SteamInstallPath is { } steam)
        {
            var root = Path.Combine(steam, "appcache", "librarycache");
            // modern layout: librarycache/<appid>/<hash>/library_capsule.jpg
            var appFolder = Path.Combine(root, appId.Value.ToString());
            if (Directory.Exists(appFolder))
            {
                foreach (var name in new[] { "library_capsule.jpg", "library_600x900.jpg", "library_capsule.png" })
                {
                    var hit = SafeFind(appFolder, name);
                    if (hit != null) return hit;
                }
            }
            // legacy flat layout
            var flat = Path.Combine(root, $"{appId}_library_600x900.jpg");
            if (File.Exists(flat)) return flat;
        }

        if (game.Store == GameStore.Xbox) return FindXboxCover(game.InstallDir);
        return null;
    }

    /// <summary>Xbox/Game Pass ships art inside the game folder; MicrosoftGame.config names it.
    /// Prefer the biggest/most pictorial one — a cropped splash reads far better than initials.</summary>
    private static string? FindXboxCover(string installDir)
    {
        try
        {
            if (!Directory.Exists(installDir)) return null;
            var names = new List<string>();
            var cfg = Path.Combine(installDir, "MicrosoftGame.config");
            if (File.Exists(cfg))
            {
                var m = ShellVisualsRegex().Match(File.ReadAllText(cfg));
                if (m.Success)
                {
                    // 480x480 art crops best into a portrait tile; the 16:9 splash is the fallback
                    foreach (var attr in new[]
                             { "Square480x480Logo", "SplashScreenImage", "Square150x150Logo", "StoreLogo" })
                    {
                        var am = Regex.Match(m.Value, attr + @"\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase);
                        if (am.Success) names.Add(am.Groups[1].Value);
                    }
                }
            }
            // common names shipped by Xbox titles, used when the config lists none
            names.AddRange(new[] { "SplashScreen.png", "background_launcher.png", "WideLogo.png", "Logo.png", "StoreLogo.png" });

            foreach (var n in names)
            {
                var p = Path.Combine(installDir, n.Replace('/', '\\'));
                if (File.Exists(p)) return p;
            }
        }
        catch (Exception ex) { Log.Warn($"xbox cover {installDir}: {ex.Message}"); }
        return null;
    }

    private static string? SafeFind(string folder, string fileName)
    {
        try
        {
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                MaxRecursionDepth = 2,
            };
            return Directory.EnumerateFiles(folder, fileName, options).FirstOrDefault();
        }
        catch { return null; }
    }
}
