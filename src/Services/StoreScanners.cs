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

    private static string? _steamPath;
    private static bool _steamPathResolved;

    /// <summary>Steam's install dir (memoized) — also where the library art cache lives.</summary>
    public static string? SteamInstallPath
    {
        get
        {
            if (_steamPathResolved) return _steamPath;
            _steamPathResolved = true;
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
                using var steamKey = baseKey.OpenSubKey(@"SOFTWARE\Valve\Steam");
                var path = steamKey?.GetValue("InstallPath") as string;
                if (path != null && Directory.Exists(path)) _steamPath = path;
            }
            catch (Exception ex) { Log.Warn($"steam path: {ex.Message}"); }
            return _steamPath;
        }
    }

    public static List<GameInfo> ScanSteam()
    {
        var games = new List<GameInfo>();
        try
        {
            var steamPath = SteamInstallPath;
            if (steamPath is null) return games;

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
            // Copia para a area do proprio app, e nao para %TEMP%: nome aleatorio em %TEMP%
            // e exatamente o padrao que regra de "arquivo suspeito" procura, e nao custa nada
            // evitar. Nome fixo porque o arquivo e apagado logo abaixo, no finally.
            Directory.CreateDirectory(AppPaths.CacheDir);
            var temp = Path.Combine(AppPaths.CacheDir, "bnet-product.db");
            File.Copy(db, temp, overwrite: true);
            string raw;
            try { raw = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(temp)); }
            finally { File.Delete(temp); }
            // O que se quer excluir sao as pastas do PROPRIO cliente (Battle.net, Agent, bna,
            // ProgramData), nao qualquer caminho que contenha essas palavras: uma biblioteca em
            // "D:\Battle.net Games\Diablo IV" ou um usuario chamado "Agent" sumiam da grade
            // porque a comparacao era por substring do caminho inteiro. Por isso a comparacao e
            // por SEGMENTO: a ultima pasta ser exatamente um dos nomes do cliente (ou uma versao
            // dele, "Battle.net.14520"), ou o caminho estar dentro da pasta de instalacao do
            // cliente. ProgramData continua excluido por segmento, porque nenhum jogo mora la.
            var clientRoots = BattleNetClientRoots(db);
            foreach (var m in WindowsPathRegex().Matches(raw).Select(m => m.Value).Distinct())
            {
                var dir = m.Replace('/', '\\').TrimEnd('\\');
                if (IsBattleNetClientDir(dir, clientRoots)) continue;
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

    private static readonly string[] BattleNetClientFolderNames = { "battle.net", "agent", "bna" };

    /// <summary>Pastas conhecidas do cliente Battle.net: a raiz do product.db em ProgramData e a
    /// instalacao do cliente (registro de desinstalacao, com os caminhos padrao como reserva).</summary>
    private static List<string> BattleNetClientRoots(string productDb)
    {
        var roots = new List<string>();
        void Add(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            var full = p.Replace('/', '\\').TrimEnd('\\');
            if (full.Length > 0 && !roots.Contains(full, StringComparer.OrdinalIgnoreCase)) roots.Add(full);
        }
        // %ProgramData%\Battle.net\Agent\product.db -> %ProgramData%\Battle.net
        Add(Path.GetDirectoryName(Path.GetDirectoryName(productDb)));
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Battle.net");
                // O InstallLocation nao pode ser aceito as cegas: quem instalou o cliente em
                // "D:\Games" (a mesma pasta que abriga a biblioteca) perderia todos os jogos
                // dali, porque tudo abaixo de uma raiz do cliente e descartado.
                var local = key?.GetValue("InstallLocation") as string;
                if (EhPastaDoClienteBattleNet(local)) Add(local);
            }
            catch { }
        }
        foreach (var env in new[] { "%ProgramFiles(x86)%", "%ProgramFiles%" })
        {
            var expanded = Environment.ExpandEnvironmentVariables(env);
            if (!expanded.StartsWith('%')) Add(Path.Combine(expanded, "Battle.net"));
        }
        return roots;
    }

    /// <summary>A pasta e mesmo a do cliente Battle.net (e nao um pai generico)? Sim quando o
    /// ultimo segmento e um dos nomes do cliente ou quando o executavel do cliente esta nela.</summary>
    private static bool EhPastaDoClienteBattleNet(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;
        var full = dir.Replace('/', '\\').TrimEnd('\\');
        var last = Path.GetFileName(full);
        if (BattleNetClientFolderNames.Any(n => last.Equals(n, StringComparison.OrdinalIgnoreCase)))
            return true;
        try
        {
            return File.Exists(Path.Combine(full, "Battle.net.exe"))
                || File.Exists(Path.Combine(full, "Battle.net Launcher.exe"));
        }
        catch { return false; }
    }

    private static bool IsBattleNetClientDir(string dir, List<string> clientRoots)
    {
        foreach (var root in clientRoots)
            if (dir.Equals(root, StringComparison.OrdinalIgnoreCase)
                || dir.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
                return true;

        var segments = dir.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s.Equals("programdata", StringComparison.OrdinalIgnoreCase))) return true;

        var last = segments.Length > 0 ? segments[^1] : "";
        foreach (var name in BattleNetClientFolderNames)
            if (last.Equals(name, StringComparison.OrdinalIgnoreCase)
                || last.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
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
            // Comparacao por CONTEUDO, e nao por igualdade.
            //
            // A chave real chama-se "Rockstar Games Social Club", e a lista dizia "Social Club":
            // `Contains` com igualdade nunca casava, e o Social Club — que e um servico de conta,
            // nao um jogo — aparecia na grade com bolinha de DLSS 5 para instalar.
            //
            // "Steam" entrou junto pelo mesmo motivo: a Rockstar cria essa subchave para apontar a
            // integracao, e ela virava um "jogo" chamado Steam.
            var skip = new[] { "launcher", "social club", "steam", "rockstar games services" };
            foreach (var sub in rockstar.GetSubKeyNames())
            {
                try
                {
                    if (skip.Any(s => sub.Contains(s, StringComparison.OrdinalIgnoreCase))) continue;
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
