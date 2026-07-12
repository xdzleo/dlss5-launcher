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
    /// <param name="knownGameName">Optional predicate: does this folder name match a catalog
    /// game? Enables the generic disk scan (standalone installs outside any launcher).</param>
    public static async Task<List<GameInfo>> ScanAllAsync(Func<string, bool>? knownGameName = null)
    {
        var tasks = new List<Task<List<GameInfo>>>
        {
            Task.Run(ScanSteam),
            Task.Run(ScanEpic),
            Task.Run(ScanGog),
            Task.Run(ScanXbox),
            Task.Run(ScanUbisoft),
            Task.Run(ScanEa),
            Task.Run(ScanBattleNet),
            Task.Run(ScanRockstar),
        };
        if (knownGameName != null)
            tasks.Add(Task.Run(() => ScanGameFolders(knownGameName)));
        var results = await Task.WhenAll(tasks);
        var games = results.SelectMany(r => r).ToList();
        // de-dup by normalized install dir (a game can appear via more than one scanner);
        // store order = trust order, so the launcher-native entry wins over the folder scan
        return games
            .GroupBy(g => Path.GetFullPath(g.InstallDir).TrimEnd('\\', '/').ToLowerInvariant())
            .Select(g => g.OrderBy(x => x.Store).First())
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    // ---------- Steam ----------

    // VDF strings use backslash escapes (\" \\), so the value must consume escaped pairs
    // as units — a title containing quotes would otherwise corrupt every following pair.
    [GeneratedRegex("\"(?<key>(?:[^\"\\\\]|\\\\.)+)\"\\s+\"(?<value>(?:[^\"\\\\]|\\\\.)*)\"")]
    private static partial Regex VdfPairRegex();

    private static string VdfUnescape(string s) =>
        s.Replace("\\\\", "\\").Replace("\\\"", "\"");

    private static Dictionary<string, string> VdfPairs(string text)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in VdfPairRegex().Matches(text))
            dict.TryAdd(VdfUnescape(m.Groups["key"].Value), VdfUnescape(m.Groups["value"].Value));
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
                        libraries.Add(VdfUnescape(m.Groups["value"].Value));
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

    // ---------- Ubisoft Connect ----------

    public static List<GameInfo> ScanUbisoft()
    {
        var games = new List<GameInfo>();
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var installs = baseKey.OpenSubKey(@"SOFTWARE\Ubisoft\Launcher\Installs");
            if (installs is null) return games;
            foreach (var sub in installs.GetSubKeyNames())
            {
                try
                {
                    if (!long.TryParse(sub, out _)) continue; // only numeric installIds are games
                    using var k = installs.OpenSubKey(sub);
                    var dir = (k?.GetValue("InstallDir") as string)?.Replace('/', '\\').TrimEnd('\\');
                    if (dir is null || !Directory.Exists(dir)) continue;
                    // launcher metadata blob is a heavy parse; the install folder name IS the title
                    games.Add(new GameInfo
                    {
                        Name = Path.GetFileName(dir),
                        InstallDir = dir,
                        Store = GameStore.Ubisoft,
                        AppId = sub,
                    });
                }
                catch (Exception ex) { Log.Warn($"Ubisoft install {sub}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Log.Warn($"Ubisoft scan: {ex.Message}"); }
        return games;
    }

    // ---------- EA App ----------

    public static List<GameInfo> ScanEa()
    {
        var games = new List<GameInfo>();
        var roots = new (RegistryHive hive, RegistryView view)[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.CurrentUser, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Registry64),
        };
        foreach (var (hive, view) in roots)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var sub in uninstall.GetSubKeyNames())
                {
                    try
                    {
                        using var k = uninstall.OpenSubKey(sub);
                        // an entry is an EA App game iff its uninstaller is EA's Cleanup.exe
                        if (k?.GetValue("UninstallString") is not string us
                            || !us.Contains("EAInstaller", StringComparison.OrdinalIgnoreCase)
                            || !us.Contains("Cleanup.exe", StringComparison.OrdinalIgnoreCase)) continue;
                        var name = k.GetValue("DisplayName") as string;
                        var dir = (k.GetValue("InstallLocation") as string)?.TrimEnd('\\');
                        if (name is null || dir is null || !Directory.Exists(dir)) continue;
                        games.Add(new GameInfo
                        {
                            Name = name,
                            InstallDir = dir,
                            Store = GameStore.EA,
                            AppId = sub,
                        });
                    }
                    catch (Exception ex) { Log.Warn($"EA uninstall {sub}: {ex.Message}"); }
                }
            }
            catch (Exception ex) { Log.Warn($"EA scan {hive}/{view}: {ex.Message}"); }
        }
        return games;
    }

    // ---------- Battle.net ----------

    [GeneratedRegex(@"[A-Za-z]:[\\/][^\x00-\x1f""|?*<>]{2,150}")]
    private static partial Regex WindowsPathRegex();

    public static List<GameInfo> ScanBattleNet()
    {
        var games = new List<GameInfo>();
        try
        {
            var db = Path.Combine(
                Environment.ExpandEnvironmentVariables("%ProgramData%"), "Battle.net", "Agent", "product.db");
            if (!File.Exists(db)) return games;
            // product.db is protobuf; install paths are plain length-prefixed UTF-8 strings
            // inside it, so a raw string sweep avoids a protobuf dependency
            var temp = Path.Combine(Path.GetTempPath(), $"renodx_bnet_{Guid.NewGuid():N}.db");
            File.Copy(db, temp, overwrite: true);
            string raw;
            try { raw = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(temp)); }
            finally { File.Delete(temp); }
            var skip = new[] { "battle.net", "agent", "\\bna\\", "programdata" };
            foreach (var m in WindowsPathRegex().Matches(raw).Select(m => m.Value).Distinct())
            {
                var dir = m.Replace('/', '\\').TrimEnd('\\');
                if (skip.Any(s => dir.Contains(s, StringComparison.OrdinalIgnoreCase))) continue;
                if (!Directory.Exists(dir)) continue;
                var name = Path.GetFileName(dir);
                if (name.Length < 3) continue;
                games.Add(new GameInfo
                {
                    Name = name,
                    InstallDir = dir,
                    Store = GameStore.BattleNet,
                });
            }
        }
        catch (Exception ex) { Log.Warn($"Battle.net scan: {ex.Message}"); }
        return games;
    }

    // ---------- Rockstar Games ----------

    public static List<GameInfo> ScanRockstar()
    {
        var games = new List<GameInfo>();
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var rockstar = baseKey.OpenSubKey(@"SOFTWARE\Rockstar Games");
            if (rockstar is null) return games;
            var skip = new[] { "Launcher", "Rockstar Games Launcher", "Social Club" };
            foreach (var sub in rockstar.GetSubKeyNames())
            {
                try
                {
                    if (skip.Contains(sub, StringComparer.OrdinalIgnoreCase)) continue;
                    using var k = rockstar.OpenSubKey(sub);
                    var dir = (k?.GetValue("InstallFolder") as string)?.TrimEnd('\\');
                    if (dir is null || !Directory.Exists(dir)) continue;
                    games.Add(new GameInfo
                    {
                        Name = sub,
                        InstallDir = dir,
                        Store = GameStore.Rockstar,
                        AppId = sub,
                    });
                }
                catch (Exception ex) { Log.Warn($"Rockstar key {sub}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Log.Warn($"Rockstar scan: {ex.Message}"); }
        return games;
    }

    // ---------- Generic disk scan (standalone installs outside any launcher) ----------

    private static readonly HashSet<string> SkipRootDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "windows", "program files", "program files (x86)", "programdata", "users", "onedrive",
        "$recycle.bin", "system volume information", "recovery", "perflogs", "intel", "amd",
        "nvidia", "temp", "tmp", "drivers", "msocache", "config.msi", "inetpub", "xboxgames",
    };

    private static readonly HashSet<string> GamesDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "games", "jogos", "game", "gaming", "my games", "meus jogos",
    };

    /// <summary>Walk every fixed drive's root folders (and one level inside "Games"-style
    /// folders); a folder becomes a game when its NAME matches a catalog entry and it
    /// contains an exe. Catches manual/standalone installs with zero configuration.</summary>
    public static List<GameInfo> ScanGameFolders(Func<string, bool> knownGameName)
    {
        var games = new List<GameInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                var candidates = new List<string>();
                foreach (var dir in Directory.GetDirectories(drive.RootDirectory.FullName))
                {
                    var name = Path.GetFileName(dir);
                    if (SkipRootDirs.Contains(name)) continue;
                    if (GamesDirNames.Contains(name))
                    {
                        try { candidates.AddRange(Directory.GetDirectories(dir)); }
                        catch { }
                    }
                    else candidates.Add(dir);
                }
                foreach (var dir in candidates)
                {
                    try
                    {
                        var name = Path.GetFileName(dir);
                        if (name.Length < 3 || !knownGameName(name)) continue;
                        var options = new EnumerationOptions
                        {
                            IgnoreInaccessible = true,
                            RecurseSubdirectories = true,
                            MaxRecursionDepth = 3,
                            AttributesToSkip = FileAttributes.ReparsePoint,
                        };
                        if (!Directory.EnumerateFiles(dir, "*.exe", options).Any()) continue;
                        games.Add(new GameInfo
                        {
                            Name = name,
                            InstallDir = dir,
                            Store = GameStore.Folder,
                        });
                    }
                    catch (Exception ex) { Log.Warn($"folder scan {dir}: {ex.Message}"); }
                }
            }
            catch (Exception ex) { Log.Warn($"folder scan drive {drive.Name}: {ex.Message}"); }
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
