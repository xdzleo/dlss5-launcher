using System.IO;
using System.Net.Http;
using RenoDXLauncher.Models;

namespace RenoDXLauncher.Services;

/// <summary>
/// Cover art: Steam local librarycache first, then Steam CDN library_600x900
/// (works for any game with a known Steam appid, whatever store it's installed from).
/// Cached in %LocalAppData%\RenoDXLauncher\covers.
/// </summary>
public class CoverService
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient();
        c.DefaultRequestHeaders.UserAgent.ParseAdd("RenoDXLauncher/1.0");
        c.Timeout = TimeSpan.FromSeconds(20);
        return c;
    }

    public static async Task<string?> GetCoverAsync(GameInfo game, int? steamAppIdHint)
    {
        try
        {
            if (game.LocalCoverPath != null && File.Exists(game.LocalCoverPath))
                return game.LocalCoverPath;

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
                $"https://shared.steamstatic.com/store_item_assets/steam/apps/{appId}/library_600x900.jpg",
                $"https://steamcdn-a.akamaihd.net/steam/apps/{appId}/library_600x900_2x.jpg",
                $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg",
            })
            {
                try
                {
                    var bytes = await Http.GetByteArrayAsync(url);
                    if (bytes.Length < 500) continue;
                    await File.WriteAllBytesAsync(cached, bytes);
                    return cached;
                }
                catch { /* try next url */ }
            }
            await File.WriteAllBytesAsync(miss, Array.Empty<byte>());
        }
        catch (Exception ex) { Log.Warn($"cover {game.Name}: {ex.Message}"); }
        return null;
    }
}
