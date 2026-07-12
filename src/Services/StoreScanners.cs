using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using RenoDXLauncher.Models;

namespace RenoDXLauncher.Services;

/// <summary>
/// Installed-game detection, re-implemented from DLSS Swapper's algorithms:
/// Steam (registry + libraryfolders.vdf + appmanifest_*.acf), Epic (ProgramData .item manifests),
/// GOG (registry), Xbox (.GamingRoot + MicrosoftGame.config).
/// </summary>
public static partial class StoreScanners
{
    public static async Task<List<GameInfo>> ScanAllAsync()
    {
        var tasks = new[]
        {
            Task.Run(ScanSteam),
            Task.Run(ScanEpic),
            Task.Run(ScanGog),
            Task.Run(ScanXbox),
        };
        var results = await Task.WhenAll(tasks);
        var games = results.SelectMany(r => r).ToList();
        // de-dup by normalized install dir (a game can appear via more than one scanner)
        return games
            .GroupBy(g => Path.GetFullPath(g.InstallDir).TrimEnd('\\', '/').ToLowerInvariant())
            .Select(g => g.OrderBy(x => x.Store).First())
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    // ---------- Steam ----------

    [GeneratedRegex("\"(?<key>[^\"]+)\"\\s+\"(?<value>[^\"]*)\"")]
    private static partial Regex VdfPairRegex();

    private static Dictionary<string, string> VdfPairs(string text)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in VdfPairRegex().Matches(text))
            dict.TryAdd(m.Groups["key"].Value, m.Groups["value"].Value.Replace(@"\\", @"\"));
        return dict;
    }

    public static List<GameInfo> ScanSteam()
    {
        var games = new List<GameInfo>();
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var steamKey = baseKey.OpenSubKey(@"SOFTWARE\Valve\Steam");
            var steamPath = steamKey?.GetValue("InstallPath") as string;
            if (steamPath is null || !Directory.Exists(steamPath)) return games;

            var libVdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            var libraries = new List<string> { steamPath };
            if (File.Exists(libVdf))
            {
                foreach (Match m in VdfPairRegex().Matches(File.ReadAllText(libVdf)))
                    if (m.Groups["key"].Value.Equals("path", StringComparison.OrdinalIgnoreCase))
                        libraries.Add(m.Groups["value"].Value.Replace(@"\\", @"\"));
            }

            foreach (var lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var steamApps = Path.Combine(lib, "steamapps");
                if (!Directory.Exists(steamApps)) continue;
                foreach (var acf in Directory.GetFiles(steamApps, "appmanifest_*.acf", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        var kv = VdfPairs(File.ReadAllText(acf));
                        if (!kv.TryGetValue("name", out var name)
                            || !kv.TryGetValue("installdir", out var installDir)
                            || !kv.TryGetValue("appid", out var appId)) continue;
                        if (appId == "228980") continue; // Steamworks Common Redistributables
                        var dir = Path.Combine(steamApps, "common", installDir);
                        if (!Directory.Exists(dir)) continue;
                        games.Add(new GameInfo
                        {
                            Name = name,
                            InstallDir = dir,
                            Store = GameStore.Steam,
                            AppId = appId,
                            SteamAppId = int.TryParse(appId, out var id) ? id : null,
                            LocalCoverPath = FirstExisting(
                                Path.Combine(steamPath, "appcache", "librarycache", appId, "library_600x900.jpg"),
                                Path.Combine(steamPath, "appcache", "librarycache", $"{appId}_library_600x900.jpg")),
                        });
                    }
                    catch (Exception ex) { Log.Warn($"Steam ACF {acf}: {ex.Message}"); }
                }
            }
        }
        catch (Exception ex) { Log.Warn($"Steam scan: {ex.Message}"); }
        return games;
    }

    // ---------- Epic Games Store ----------

    public static List<GameInfo> ScanEpic()
    {
        var games = new List<GameInfo>();
        try
        {
            var manifests = Path.Combine(
                Environment.ExpandEnvironmentVariables("%ProgramData%"), "Epic", "EpicGamesLauncher", "Data", "Manifests");
            if (!Directory.Exists(manifests)) return games;
            foreach (var item in Directory.GetFiles(manifests, "*.item"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(item));
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("AppCategories", out var cats)
                        || !cats.EnumerateArray().Any(c => c.GetString() == "games")) continue;
                    var name = root.GetProperty("DisplayName").GetString();
                    var dir = root.GetProperty("InstallLocation").GetString();
                    if (name is null || dir is null || !Directory.Exists(dir)) continue;
                    string? exeHint = root.TryGetProperty("LaunchExecutable", out var le) ? le.GetString() : null;
                    games.Add(new GameInfo
                    {
                        Name = name,
                        InstallDir = dir,
                        Store = GameStore.Epic,
                        AppId = root.TryGetProperty("CatalogItemId", out var cid) ? cid.GetString() : null,
                        ExeHint = exeHint,
                    });
                }
                catch (Exception ex) { Log.Warn($"Epic manifest {item}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Log.Warn($"Epic scan: {ex.Message}"); }
        return games;
    }

    // ---------- GOG ----------

    public static List<GameInfo> ScanGog()
    {
        var games = new List<GameInfo>();
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var gogKey = baseKey.OpenSubKey(@"SOFTWARE\GOG.com\Games");
            if (gogKey is null) return games;
            foreach (var sub in gogKey.GetSubKeyNames())
            {
                try
                {
                    using var k = gogKey.OpenSubKey(sub);
                    if (k is null) continue;
                    if (k.GetValue("dependsOn") is string dep && dep.Length > 0) continue; // DLC
                    var name = k.GetValue("gameName") as string;
                    var path = k.GetValue("path") as string;
                    if (name is null || path is null || !Directory.Exists(path)) continue;
                    games.Add(new GameInfo
                    {
                        Name = name,
                        InstallDir = path,
                        Store = GameStore.Gog,
                        AppId = k.GetValue("gameID") as string ?? sub,
                    });
                }
                catch (Exception ex) { Log.Warn($"GOG key {sub}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Log.Warn($"GOG scan: {ex.Message}"); }
        return games;
    }

    // ---------- Xbox / Game Pass ----------

    public static List<GameInfo> ScanXbox()
    {
        var games = new List<GameInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                var gamingRoot = Path.Combine(drive.RootDirectory.FullName, ".GamingRoot");
                if (!File.Exists(gamingRoot)) continue;
                // UTF-16LE file: magic "RGBX" then the relative path from char offset 4, NUL-terminated.
                var bytes = File.ReadAllBytes(gamingRoot);
                if (bytes.Length < 10) continue;
                var text = Encoding.Unicode.GetString(bytes, 8, bytes.Length - 8).TrimEnd('\0');
                var root = Path.Combine(drive.RootDirectory.FullName, text.TrimStart('\\', '/'));
                if (!Directory.Exists(root)) continue;
                foreach (var gameDir in Directory.GetDirectories(root))
                {
                    try
                    {
                        var content = Path.Combine(gameDir, "Content");
                        var cfg = Path.Combine(content, "MicrosoftGame.config");
                        if (!File.Exists(cfg)) continue;
                        var xml = File.ReadAllText(cfg);
                        var nameMatch = Regex.Match(xml, @"<ShellVisuals[^>]*DefaultDisplayName\s*=\s*""([^""]+)""");
                        var name = nameMatch.Success ? nameMatch.Groups[1].Value : Path.GetFileName(gameDir);
                        games.Add(new GameInfo
                        {
                            Name = name,
                            InstallDir = content,
                            Store = GameStore.Xbox,
                            AppId = Regex.Match(xml, @"<Identity[^>]*Name\s*=\s*""([^""]+)""") is { Success: true } idm
                                ? idm.Groups[1].Value : null,
                            ExeHint = "gamelaunchhelper.exe",
                        });
                    }
                    catch (Exception ex) { Log.Warn($"Xbox game {gameDir}: {ex.Message}"); }
                }
            }
            catch (Exception ex) { Log.Warn($"Xbox drive {drive.Name}: {ex.Message}"); }
        }
        return games;
    }

    private static string? FirstExisting(params string[] paths) => paths.FirstOrDefault(File.Exists);
}

public static class Log
{
    private static readonly object Gate = new();
    public static string LogPath { get; } = Path.Combine(AppPaths.DataDir, "launcher.log");

    public static void Warn(string message) => Write("WARN", message);
    public static void Info(string message) => Write("INFO", message);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never throw */ }
    }
}
