using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using RenoDXLauncher.Localization;
using RenoDXLauncher.Models;
using RenoDXLauncher.Services;

namespace RenoDXLauncher;

/// <summary>
/// Headless mode: everything the UI does, driven from the command line. Exists so the whole
/// pipeline (detection → catalog → install → toggle → settings → verification) can be exercised
/// and diagnosed without a window — for automation, for bug reports, and because a GUI is a
/// terrible place to test from.
/// </summary>
public static class Cli
{
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int processId);
    [DllImport("kernel32.dll")] private static extern bool AllocConsole();
    [DllImport("kernel32.dll")] private static extern bool FreeConsole();
    private const int AttachParentProcess = -1;

    /// <summary>True when the process was started with CLI arguments (so no window should open).</summary>
    public static bool IsCliInvocation(string[] args) => args.Length > 0 && !args[0].StartsWith('-') is false || args.Length > 0;

    public static async Task<int> RunAsync(string[] args)
    {
        // A WinExe has no console of its own. When the caller redirected stdout (a pipe or a file)
        // the handle is already valid — touching it would break the redirection. Only when the
        // output is NOT redirected do we attach to the caller's console (or allocate one when the
        // exe was double-clicked), and then re-open the streams: after AttachConsole the runtime's
        // cached handles still point at the old, invalid ones and writes vanish silently.
        bool attached = false;
        if (Console.IsOutputRedirected)
        {
            AttachConsole(AttachParentProcess); // for stderr progress, harmless if it fails
        }
        else
        {
            attached = AttachConsole(AttachParentProcess);
            if (!attached) AllocConsole();
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        }
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        try
        {
            return await DispatchAsync(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(L.T("Cli_Error_Unhandled", ex.Message));
            return 1;
        }
        finally
        {
            Console.Out.Flush();
            Console.Error.Flush();
            // give the parent shell its prompt back on its own line
            if (attached) Console.Out.Write(Environment.NewLine);
            Console.Out.Flush();
            FreeConsole();
        }
    }

    private static async Task<int> DispatchAsync(string[] args)
    {
        var cmd = args[0].TrimStart('-').ToLowerInvariant();
        var rest = args.Skip(1).ToArray();
        bool json = args.Contains("--json");

        return cmd switch
        {
            "list" or "ls" => await ListAsync(json),
            "check" or "updates" => await CheckAsync(json),
            "verify" => await VerifyAsync(rest.FirstOrDefault()),
            "exe" or "exes" => await ExesAsync(rest.FirstOrDefault()),
            "add" or "adicionar" => await AddFolderAsync(rest.FirstOrDefault()),
            "settings" => await SettingsAsync(rest.FirstOrDefault()),
            "set" => await SetAsync(rest),
            "profile" => await ProfileAsync(rest),
            "install" => await InstallAsync(rest.FirstOrDefault()),
            "enable" => await ToggleAsync(rest.FirstOrDefault(), true),
            "disable" => await ToggleAsync(rest.FirstOrDefault(), false),
            "doctor" => await DoctorAsync(),
            "help" or "h" or "?" => Help(),
            _ => Unknown(cmd),
        };
    }

    /// <summary>Width of the syntax column in the help table.</summary>
    private const int HelpSyntaxWidth = 26;

    private static int Help()
    {
        var game = L.T("Cli_Arg_Game");
        var folder = L.T("Cli_Arg_Folder");
        var kv = L.T("Cli_Arg_KeyValue");

        Console.WriteLine(L.T("Cli_Help_Header", L.T("App_Name")));
        Console.WriteLine();
        HelpRow("list [--json]", L.T("Cli_Help_List"));
        HelpRow("check [--json]", L.T("Cli_Help_Check"));
        HelpRow($"verify [{game}]", L.T("Cli_Help_Verify"));
        HelpRow($"exe {game}", L.T("Cli_Help_Exe"));
        HelpRow($"add {folder}", L.T("Cli_Help_Add"));
        HelpRow($"settings {game}", L.T("Cli_Help_Settings"));
        HelpRow($"set {game} {kv}…", L.T("Cli_Help_Set"));
        HelpRow("profile [--peak N] [--game N] [--ui N]", L.T("Cli_Help_Profile"));
        HelpRow($"install {game}", L.T("Cli_Help_Install"));
        HelpRow($"enable {game}", L.T("Cli_Help_Enable"));
        HelpRow($"disable {game}", L.T("Cli_Help_Disable"));
        HelpRow("doctor", L.T("Cli_Help_Doctor"));
        Console.WriteLine();
        Console.WriteLine(L.T("Cli_Help_Match", game));
        Console.WriteLine("  RenoDXLauncher.exe set \"dying light\" ToneMapPeakNits=1300");
        return 0;
    }

    /// <summary>One help line. The syntax column is assembled from literal command names, so no
    /// translation can rename a command — only the description and the metavariables are localized.</summary>
    private static void HelpRow(string syntax, string description)
    {
        if (syntax.Length > HelpSyntaxWidth)
        {
            Console.WriteLine($"  {syntax}");
            Console.WriteLine($"  {new string(' ', HelpSyntaxWidth)} {description}");
        }
        else
        {
            Console.WriteLine($"  {syntax.PadRight(HelpSyntaxWidth)} {description}");
        }
    }

    private static int Unknown(string cmd)
    {
        Console.Error.WriteLine(L.T("Cli_Error_UnknownCommand", cmd));
        Help();
        return 2;
    }

    // ---------- shared loading ----------

    private record Ctx(List<GameInfo> Games, List<CatalogEntry> Catalog, LauncherConfig Config,
        ManifestService Manifest, RhiManifestService Rhi);

    private static async Task<Ctx> LoadAsync(bool quiet = false)
    {
        if (!quiet) Console.Error.WriteLine(L.T("Cli_Loading"));
        var catalog = await new CatalogService().LoadAsync();
        var rhi = new RhiManifestService();
        await rhi.LoadAsync();
        var known = catalog.SelectMany(e => e.NormalizedAliases).ToHashSet(StringComparer.Ordinal);
        bool Known(string n) => known.Contains(MatchService.Normalize(n))
            || known.Contains(MatchService.Normalize(MatchService.StripEditionSuffix(n)));
        var games = await StoreScanners.ScanAllAsync(Known);
        var config = LauncherConfig.Load();
        foreach (var dir in config.ManualGameDirs.Where(Directory.Exists))
            games.Add(FolderGameResolver.Resolve(dir, catalog));
        return new Ctx(games, catalog, config, new ManifestService(), rhi);
    }

    /// <summary>Resolve a game by substring, reporting ambiguity instead of guessing.</summary>
    private static GameInfo? Resolve(Ctx ctx, string? query, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(query)) { error = L.T("Cli_Error_GameRequired"); return null; }
        var hits = ctx.Games
            .Where(g => g.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (hits.Count == 0)
        {
            error = L.T("Cli_Error_GameNotFound", query);
            return null;
        }
        if (hits.Count > 1)
        {
            var exact = hits.FirstOrDefault(g => g.Name.Equals(query, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
            error = L.T("Cli_Error_GameAmbiguous", query, string.Join(", ", hits.Take(6).Select(g => g.Name)));
            return null;
        }
        return hits[0];
    }

    /// <summary>Deploy dir + state for a game (pinned exe, else existing install, else best guess).</summary>
    private static (string? exe, ModState? state) StateOf(Ctx ctx, GameInfo game)
    {
        var key = $"{game.Store}_{game.AppId ?? game.InstallDir}";
        if (ctx.Config.PinnedExes.TryGetValue(key, out var pinned) && File.Exists(pinned))
            return (pinned, AddonService.GetState(Path.GetDirectoryName(pinned)!, pinned));

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true, RecurseSubdirectories = true,
            MaxRecursionDepth = 5, AttributesToSkip = FileAttributes.ReparsePoint,
        };
        try
        {
            var addon = Directory.EnumerateFiles(game.InstallDir, "renodx-*.addon*", options).FirstOrDefault();
            if (addon != null)
            {
                var dir = Path.GetDirectoryName(addon)!;
                var exe = Directory.GetFiles(dir, "*.exe").OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
                return (exe, AddonService.GetState(dir, exe));
            }
        }
        catch { }

        var cand = ExeLocator.FindCandidates(game, ctx.Rhi.InstallSubdir(game.Name)).FirstOrDefault();
        return cand is null ? (null, null) : (cand, AddonService.GetState(Path.GetDirectoryName(cand)!, cand));
    }

    // ---------- commands ----------

    /// <summary>Human-readable form of a raw status token. The token itself stays untranslated:
    /// it goes into --json and drives the "installed" count, so scripts must not see it move.</summary>
    private static string StatusLabel(string status) => status switch
    {
        "disponivel" => L.T("Cli_Status_Available"),
        "ativado" => L.T("Cli_Status_Enabled"),
        "desativado" => L.T("Cli_Status_Disabled"),
        _ => L.T("Cli_Status_NoMod"),
    };

    private static async Task<int> ListAsync(bool json)
    {
        var ctx = await LoadAsync(json);
        var rows = ctx.Games.Select(g =>
        {
            var mod = MatchService.FindMatch(g, ctx.Catalog);
            var (_, state) = StateOf(ctx, g);
            return new
            {
                name = g.Name,
                store = g.Store.ToString(),
                mod = mod?.Slug,
                modName = mod?.GameName,
                status = state?.AddonPath is null ? (mod is null ? "sem-mod" : "disponivel")
                       : state.AddonEnabled ? "ativado" : "desativado",
                dir = state?.TargetDir,
            };
        }).ToList();

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        Console.WriteLine($"{L.T("Cli_List_Col_Game"),-42} {L.T("Cli_List_Col_Store"),-10} " +
            $"{L.T("Cli_List_Col_Status"),-11} {L.T("Cli_List_Col_Mod")}");
        Console.WriteLine(new string('-', 92));
        foreach (var r in rows)
            Console.WriteLine($"{Trunc(r.name, 42),-42} {r.store,-10} {StatusLabel(r.status),-11} {r.mod ?? "-"}");
        Console.WriteLine();
        Console.WriteLine(L.T("Cli_List_Summary", rows.Count, rows.Count(r => r.mod != null),
            rows.Count(r => r.status is "ativado" or "desativado")));
        return 0;
    }

    /// <summary>Machine-readable verdict for --json. Deliberately not localized: scripts parse it,
    /// so it must not change when the interface language changes.</summary>
    private static string RawVerdict(bool? newer) => newer switch
    {
        true => "ATUALIZAÇÃO DISPONÍVEL",
        false => "atualizado",
        _ => "não verificável",
    };

    private static string VerdictLabel(bool? newer) => newer switch
    {
        true => L.T("Cli_Check_UpdateAvailable"),
        false => L.T("Cli_Check_UpToDate"),
        _ => L.T("Cli_Check_Unknown"),
    };

    private static async Task<int> CheckAsync(bool json)
    {
        var ctx = await LoadAsync(json);
        var results = new List<(string name, bool? newer)>();
        foreach (var g in ctx.Games)
        {
            var mod = MatchService.FindMatch(g, ctx.Catalog);
            if (mod?.DownloadUrl is null) continue;
            var (_, state) = StateOf(ctx, g);
            if (state?.AddonPath is null) continue;
            results.Add((g.Name, await AddonService.IsUpdateAvailableAsync(mod, state)));
        }
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                results.Select(r => new { game = r.name, status = RawVerdict(r.newer) }),
                new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        foreach (var (name, newer) in results)
            Console.WriteLine($"{Trunc(name, 46),-46} {VerdictLabel(newer)}");
        Console.WriteLine();
        Console.WriteLine(L.T("Cli_Check_Summary", results.Count, results.Count(r => r.newer == true)));
        return 0;
    }

    private static async Task<int> VerifyAsync(string? query)
    {
        var ctx = await LoadAsync();
        var targets = new List<GameInfo>();
        if (string.IsNullOrWhiteSpace(query)) targets.AddRange(ctx.Games);
        else
        {
            var g = Resolve(ctx, query, out var err);
            if (g is null) { Console.Error.WriteLine(err); return 1; }
            targets.Add(g);
        }
        int found = 0;
        foreach (var g in targets)
        {
            var (_, state) = StateOf(ctx, g);
            if (state?.AddonPath is null) continue;
            found++;
            var report = ReShadeLogService.Check(state.TargetDir);
            Console.WriteLine($"{g.Name}\n  {report.Message}");
            if (report.LastRun is { } lr)
            {
                var stamp = lr.ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture);
                Console.WriteLine("  " + L.T("Cli_Verify_LastRun", stamp));
            }
        }
        if (found == 0) Console.WriteLine(L.T("Cli_Verify_NothingInstalled"));
        return 0;
    }

    /// <summary>Which exe the app would deploy to, and why — the ranking is a heuristic and
    /// gets this wrong on odd layouts, so it has to be inspectable without opening the GUI.</summary>
    private static async Task<int> ExesAsync(string? query)
    {
        var ctx = await LoadAsync();
        var game = Resolve(ctx, query, out var err);
        if (game is null) { Console.Error.WriteLine(err); return 1; }

        var cands = ExeLocator.FindCandidates(game, ctx.Rhi.InstallSubdir(game.Name));
        Console.WriteLine($"{game.Name} — {game.InstallDir}\n");
        if (cands.Count == 0) { Console.WriteLine("  " + L.T("Cli_Exe_NoCandidates")); return 1; }

        for (int i = 0; i < cands.Count; i++)
        {
            var pe = PeUtils.Inspect(cands[i], readImports: false);
            var bits = pe is null ? "  ?  " : pe.Is64Bit ? "64-bit" : "32-bit";
            long size = 0;
            try { size = new FileInfo(cands[i]).Length; } catch { }
            var rel = cands[i].StartsWith(game.InstallDir, StringComparison.OrdinalIgnoreCase)
                ? cands[i][game.InstallDir.Length..].TrimStart('\\', '/')
                : cands[i];
            Console.WriteLine($"  {(i == 0 ? "→" : " ")} {bits} {size / 1048576.0,8:0.0} MB  {rel}");
        }
        return 0;
    }

    /// <summary>Register a game folder the store scanners cannot see (a game installed by hand).
    /// The folder is only remembered — nothing inside it is read or written until the user asks
    /// for an install.</summary>
    private static async Task<int> AddFolderAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            Console.Error.WriteLine(L.T("Cli_Usage", $"add {L.T("Cli_Arg_Folder")}"));
            return 2;
        }
        var dir = Path.GetFullPath(path.Trim().Trim('"')).TrimEnd('\\', '/');
        if (!Directory.Exists(dir)) { Console.Error.WriteLine(L.T("Cli_Add_FolderMissing", dir)); return 1; }

        var config = LauncherConfig.Load();
        bool already = config.ManualGameDirs.Contains(dir, StringComparer.OrdinalIgnoreCase);
        if (!already)
        {
            config.ManualGameDirs.Add(dir);
            config.Save();
        }

        var catalog = await new CatalogService().LoadAsync();
        var game = FolderGameResolver.Resolve(dir, catalog);
        var mod = MatchService.FindMatch(game, catalog);

        Console.WriteLine(L.T(already ? "Cli_Add_AlreadyRegistered" : "Cli_Add_Registered", dir));
        Console.WriteLine(L.T("Cli_Add_RecognizedAs", game.Name));
        if (mod is null)
        {
            Console.WriteLine(L.T("Cli_Add_NoModMatch"));
            Console.WriteLine(L.T("Cli_Add_NamesTried",
                string.Join(" | ", FolderGameResolver.CandidateNames(dir, ExeLocator.FindCandidates(game, null).FirstOrDefault()))));
            return 1;
        }
        Console.WriteLine(L.T("Cli_Add_ModFound", mod.GameName, mod.Slug, mod.Maintainer));
        var download = mod.DownloadUrl != null ? L.T("Cli_Mod_DirectDownload") : L.T("Cli_Mod_NoDirectDownload");
        var maturity = mod.Working ? L.T("Cli_Mod_Stable") : L.T("Cli_Mod_Wip");
        Console.WriteLine($"     {download} · {maturity}");
        var exe = ExeLocator.FindCandidates(game, new RhiManifestService().InstallSubdir(game.Name)).FirstOrDefault();
        Console.WriteLine(L.T("Cli_Target", exe ?? L.T("Cli_Exe_NoneFound")));
        return 0;
    }

    private static async Task<int> SettingsAsync(string? query)
    {
        var ctx = await LoadAsync();
        var game = Resolve(ctx, query, out var err);
        if (game is null) { Console.Error.WriteLine(err); return 1; }
        var mod = MatchService.FindMatch(game, ctx.Catalog);
        var (_, state) = StateOf(ctx, game);
        if (state?.AddonPath is null) { Console.Error.WriteLine(L.T("Cli_Error_ModNotInstalled")); return 1; }
        var defs = ctx.Manifest.GetSettings(mod?.Slug);
        if (defs is null) { Console.Error.WriteLine(L.T("Cli_Error_NoSettings", mod?.Slug ?? "?")); return 1; }

        Console.WriteLine($"{game.Name} — {state.IniPath}\n");
        foreach (var v in SettingsService.Read(state.IniPath, defs))
        {
            var current = v.Current?.ToString("0.####", CultureInfo.InvariantCulture) ?? L.T("Cli_Settings_Default");
            var def = v.Def.Default?.ToString("0.####", CultureInfo.InvariantCulture) ?? "-";
            var defCol = L.T("Cli_Settings_DefaultColumn", def);
            Console.WriteLine($"  {v.IniKeyCasing,-32} {current,-12} {defCol,-16} {v.Def.Label}");
        }
        return 0;
    }

    private static async Task<int> SetAsync(string[] rest)
    {
        var kv = L.T("Cli_Arg_KeyValue");
        if (rest.Length < 2)
        {
            Console.Error.WriteLine(L.T("Cli_Usage", $"set {L.T("Cli_Arg_Game")} {kv} [{kv}…]"));
            return 2;
        }
        var ctx = await LoadAsync();
        var game = Resolve(ctx, rest[0], out var err);
        if (game is null) { Console.Error.WriteLine(err); return 1; }
        var mod = MatchService.FindMatch(game, ctx.Catalog);
        var (_, state) = StateOf(ctx, game);
        if (state?.AddonPath is null) { Console.Error.WriteLine(L.T("Cli_Error_ModNotInstalled")); return 1; }
        var defs = ctx.Manifest.GetSettings(mod?.Slug);
        if (defs is null) { Console.Error.WriteLine(L.T("Cli_Error_NoSettings", mod?.Slug ?? "?")); return 1; }

        var changes = new List<(SettingDef, double)>();
        foreach (var pair in rest.Skip(1).Where(a => a.Contains('=')))
        {
            var i = pair.IndexOf('=');
            var key = pair[..i];
            var raw = pair[(i + 1)..];
            var def = defs.FirstOrDefault(d => d.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (def is null) { Console.Error.WriteLine(L.T("Cli_Set_UnknownKey", key)); return 1; }
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var val))
            { Console.Error.WriteLine(L.T("Cli_Set_InvalidValue", raw)); return 1; }
            changes.Add((def, val));
        }
        if (changes.Count == 0) { Console.Error.WriteLine(L.T("Cli_Set_NothingToWrite", kv)); return 2; }

        // writing into a real game's config is irreversible from here — always name the exact
        // target first, and let --dry-run show what would change without touching anything
        Console.WriteLine(L.T("Cli_Target", $"{game.Name}  ->  {state.IniPath}"));
        bool dry = rest.Any(a => a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase));
        var current = SettingsService.Read(state.IniPath, defs).ToDictionary(v => v.Def.Key, v => v.Current);
        foreach (var (d, v) in changes)
        {
            var before = current.GetValueOrDefault(d.Key)?.ToString("0.####", CultureInfo.InvariantCulture)
                ?? L.T("Cli_Settings_Default");
            Console.WriteLine($"  {d.Key}: {before} -> {v.ToString("0.####", CultureInfo.InvariantCulture)}");
        }
        if (dry) { Console.WriteLine(L.T("Cli_Set_DryRun")); return 0; }
        SettingsService.Write(state.IniPath, changes);
        Console.WriteLine(L.T("Cli_Set_Written"));
        return 0;
    }

    private static Task<int> ProfileAsync(string[] rest)
    {
        var cfg = LauncherConfig.Load();
        double? Get(string flag)
        {
            var i = Array.FindIndex(rest, a => a.Equals("--" + flag, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < rest.Length
                && double.TryParse(rest[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
        }
        var peak = Get("peak"); var game = Get("game"); var ui = Get("ui");
        if (peak is null && game is null && ui is null)
        {
            Console.WriteLine(L.T("Cli_Profile_Current",
                L.T("Cli_Profile_Nits", cfg.PeakNits, cfg.GameNits, cfg.UiNits),
                L.T(cfg.ApplyProfileOnInstall ? "Common_Yes" : "Common_No")));
            return Task.FromResult(0);
        }
        if (peak is { } p) cfg.PeakNits = p;
        if (game is { } g) cfg.GameNits = g;
        if (ui is { } u) cfg.UiNits = u;
        cfg.Save();
        Console.WriteLine(L.T("Cli_Profile_Saved",
            L.T("Cli_Profile_Nits", cfg.PeakNits, cfg.GameNits, cfg.UiNits)));
        return Task.FromResult(0);
    }

    private static async Task<int> InstallAsync(string? query)
    {
        var ctx = await LoadAsync();
        var game = Resolve(ctx, query, out var err);
        if (game is null) { Console.Error.WriteLine(err); return 1; }
        var mod = MatchService.FindMatch(game, ctx.Catalog);
        if (mod?.DownloadUrl is null) { Console.Error.WriteLine(L.T("Cli_Install_NoDirectDownload")); return 1; }

        var ac = AntiCheatScanner.Detect(game.InstallDir, null);
        if (ac != null)
        {
            Console.Error.WriteLine(L.T("Cli_Install_AntiCheat_Abort", ac));
            Console.Error.WriteLine(L.T("Cli_Install_AntiCheat_UseGui"));
            return 3;
        }

        var (exe, _) = StateOf(ctx, game);
        if (exe is null) { Console.Error.WriteLine(L.T("Cli_Error_ExeNotFound", L.T("Cli_Arg_Game"))); return 1; }
        var dir = Path.GetDirectoryName(exe)!;
        var progress = new Progress<string>(Console.WriteLine);
        var deploy = await new ReShadeService().DeployAsync(dir, exe, ctx.Rhi.GraphicsApi(game.Name),
            ctx.Rhi.DllNameOverride(game.Name), progress);
        if (!deploy.Success) { Console.Error.WriteLine(deploy.Message); return 1; }
        await AddonService.DownloadAddonAsync(mod, dir, progress);
        Console.WriteLine(L.T("Cli_Install_Done", mod.Slug, dir));
        return 0;
    }

    private static async Task<int> ToggleAsync(string? query, bool enable)
    {
        var ctx = await LoadAsync();
        var game = Resolve(ctx, query, out var err);
        if (game is null) { Console.Error.WriteLine(err); return 1; }
        var (_, state) = StateOf(ctx, game);
        if (state?.AddonPath is null) { Console.Error.WriteLine(L.T("Cli_Error_ModNotInstalled")); return 1; }
        AddonService.SetEnabled(state, enable);
        Console.WriteLine(L.T(enable ? "Cli_Toggle_Enabled" : "Cli_Toggle_Disabled", game.Name));
        return 0;
    }

    private static async Task<int> DoctorAsync()
    {
        Console.WriteLine(L.T("Cli_Doctor_Header", L.T("App_Name")));
        Console.WriteLine();
        Console.WriteLine($"{L.T("Cli_Doctor_Label_Data"),-12}{AppPaths.DataDir}");
        Console.WriteLine($"{L.T("Cli_Doctor_Label_Log"),-12}{Log.LogPath}");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var catalog = await new CatalogService().LoadAsync();
        Console.WriteLine();
        Console.WriteLine($"{L.T("Cli_Doctor_Label_Catalog"),-12}" +
            L.T("Cli_Doctor_Catalog_Summary", catalog.Count, sw.ElapsedMilliseconds,
                catalog.Count(e => e.DownloadUrl != null), catalog.Count(e => e.SteamAppId != null)));

        Console.WriteLine();
        Console.WriteLine(L.T("Cli_Doctor_Label_Scanners"));
        void Time(string name, Func<List<GameInfo>> f)
        {
            var s = System.Diagnostics.Stopwatch.StartNew();
            var n = f().Count;
            Console.WriteLine($"  {name,-12} {L.T("Cli_Doctor_ScannerRow", n, s.ElapsedMilliseconds)}");
        }
        Time("Steam", StoreScanners.ScanSteam);
        Time("Epic", StoreScanners.ScanEpic);
        Time("GOG", StoreScanners.ScanGog);
        Time("Xbox", StoreScanners.ScanXbox);
        Time("Ubisoft", StoreScanners.ScanUbisoft);
        Time("EA", StoreScanners.ScanEa);
        Time("Battle.net", StoreScanners.ScanBattleNet);
        Time("Rockstar", StoreScanners.ScanRockstar);
        var known = catalog.SelectMany(e => e.NormalizedAliases).ToHashSet(StringComparer.Ordinal);
        Time(L.T("Cli_Doctor_Scanner_Folders"), () => StoreScanners.ScanGameFolders(n =>
            known.Contains(MatchService.Normalize(n))
            || known.Contains(MatchService.Normalize(MatchService.StripEditionSuffix(n)))));

        var reshadeDir = Path.Combine(AppPaths.DataDir, "reshade");
        Console.WriteLine();
        Console.WriteLine(L.T("Cli_Doctor_ReShadeCache", Directory.Exists(reshadeDir)
            ? string.Join(", ", Directory.GetDirectories(reshadeDir).Select(Path.GetFileName))
            : L.T("Cli_Doctor_ReShadeCache_None")));

        var cfg = LauncherConfig.Load();
        Console.WriteLine($"{L.T("Cli_Doctor_Label_Profile"),-12}" +
            L.T("Cli_Profile_Nits", cfg.PeakNits, cfg.GameNits, cfg.UiNits));
        Console.WriteLine(L.T("Cli_Doctor_Pins", cfg.PinnedExes.Count, cfg.ManualGameDirs.Count));
        return 0;
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
}
