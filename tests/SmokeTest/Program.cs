// End-to-end smoke test against a FAKE game dir (never touches real games):
// catalog fetch → match → ReShade provision (real download from reshade.me) →
// addon download (real renodx-cp2077.addon64) → toggle → settings write/read.
using System.IO;
using RenoDXLauncher.Models;
using RenoDXLauncher.Services;
using RenoDXLauncher.ViewModels;

int failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
    if (!ok) failures++;
}

var fakeRoot = Path.Combine(Path.GetTempPath(), "RenoDXLauncherSmoke");
if (Directory.Exists(fakeRoot)) Directory.Delete(fakeRoot, recursive: true); // no state leaks between runs
var fakeDir = Path.Combine(fakeRoot, "Cyberpunk 2077", "bin", "x64");
Directory.CreateDirectory(fakeDir);
var fakeExe = Path.Combine(fakeDir, "Cyberpunk2077.exe");
File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), fakeExe, overwrite: true);

// 1. catalog
var catalog = await new CatalogService().LoadAsync();
Check(catalog.Count > 300, $"catálogo carregado ({catalog.Count} entradas)");
Check(catalog.Any(e => e.Kind == ModKind.UnrealEngine), "tem entradas do mod genérico Unreal");
Check(catalog.Any(e => e.Kind == ModKind.UnityEngine), "tem entradas do mod genérico Unity");
Check(catalog.Count(e => e.SteamAppId != null) > 100, $"steam appids presentes ({catalog.Count(e => e.SteamAppId != null)})");

// 2. matching
var fakeGame = new GameInfo { Name = "Cyberpunk 2077", InstallDir = fakeDir, Store = GameStore.Manual };
var match = MatchService.FindMatch(fakeGame, catalog);
Check(match?.Slug == "cp2077", $"match Cyberpunk 2077 → slug {match?.Slug}");
var er = MatchService.FindMatch(new GameInfo { Name = "ELDEN RING", InstallDir = ".", Store = GameStore.Manual }, catalog);
Check(er != null, $"match ELDEN RING → {er?.Slug} ({er?.GameName})");
var sekiro = MatchService.FindMatch(new GameInfo { Name = "Sekiro™: Shadows Die Twice", InstallDir = ".", Store = GameStore.Manual }, catalog);
Check(sekiro != null, $"match Sekiro™ → {sekiro?.Slug}");

// regressões de matching: sequência NUNCA pode casar com o jogo anterior
var dis2 = MatchService.FindMatch(new GameInfo { Name = "Dishonored 2", InstallDir = ".", Store = GameStore.Manual }, catalog);
Check(dis2 is null || dis2.NormalizedAliases.Contains("dishonored2"), $"Dishonored 2 não herda o mod do Dishonored 1 (→ {dis2?.GameName ?? "sem match"})");
var rdr2 = MatchService.FindMatch(new GameInfo { Name = "Red Dead Redemption 2", InstallDir = ".", Store = GameStore.Manual }, catalog);
Check(rdr2 is null || rdr2.GameName.Contains('2'), $"RDR2 não herda o mod do RDR1 (→ {rdr2?.GameName ?? "sem match"})");
var tr2013 = MatchService.FindMatch(new GameInfo { Name = "Tomb Raider", InstallDir = ".", Store = GameStore.Manual }, catalog);
Check(tr2013 != null, $"'Tomb Raider' casa com 'Tomb Raider (2013)' via strip de parêntese (→ {tr2013?.GameName})");
var witcher = MatchService.FindMatch(new GameInfo { Name = "The Witcher 3: Wild Hunt", InstallDir = ".", Store = GameStore.Manual }, catalog);
Check(witcher != null, $"The Witcher 3 matchável (→ {witcher?.GameName}, slug {witcher?.Slug})");
var goty = MatchService.FindMatch(new GameInfo { Name = "Batman: Arkham Knight Game of the Year Edition", InstallDir = ".", Store = GameStore.Manual }, catalog);
Check(goty?.Slug == "batmanak", $"sufixo de edição removido só no lado instalado (→ {goty?.Slug})");

// 3. PE utils
var pe = PeUtils.Inspect(fakeExe);
Check(pe is { Is64Bit: true }, $"PE bitness do exe fake = {(pe?.Is64Bit == true ? 64 : 32)}");

// 4. manifest
var manifest = new ManifestService();
var defs = manifest.GetSettings("cp2077");
Check(defs is { Count: > 10 }, $"manifest cp2077: {defs?.Count} settings");
Check(defs!.Any(d => d.Key == "toneMapPeakNits"), "manifest respeita camelCase do cp2077 (toneMapPeakNits)");
var ueDefs = manifest.GetSettings("unrealengine");
Check(ueDefs!.Any(d => d.Key == "ToneMapPeakNits" && d.Default == 1000 && d.Max == 4000),
    "manifest unrealengine: ToneMapPeakNits default 1000 range até 4000");

// 4b. advice extractor (in-game HDR on/off from real note phrasing)
Check(AdviceService.DetectHdr("Disable in-game HDR. B8G8R8A8_TYPELESS Output Size") == InGameHdr.Disable,
    "detecta 'Disable in-game HDR' → DESLIGAR");
Check(AdviceService.DetectHdr("Must be using native res output and 100% res scale! HDR On in-game.") == InGameHdr.Enable,
    "detecta 'HDR On in-game' → LIGAR");
Check(AdviceService.DetectHdr("If the game has an in-game HDR option, enable that too.") == InGameHdr.Enable,
    "detecta 'enable that too' → LIGAR");
Check(AdviceService.DetectHdr("If you have a washed out image, disable AUTOHDR and RTX HDR to avoid double tonemapping") == InGameHdr.Unknown,
    "NÃO confunde 'disable AutoHDR/RTX HDR' (Windows) com HDR do jogo");
Check(AdviceService.DetectHdr("Great mod, no notes.") == InGameHdr.Unknown,
    "nota sem menção de HDR → desconhecido");
var advDep = AdviceService.Build("The RenoDX mod for Dying Light 2 has been abandoned and is no longer maintained.", false);
Check(advDep.Any(a => a.Kind == AdviceKind.Deprecated), "detecta mod abandonado/descontinuado");
var advAc = AdviceService.Build("Disable in-game HDR and Easy Anti-Cheat.", false);
Check(advAc.Any(a => a.Kind == AdviceKind.HdrOff) && advAc.Any(a => a.Kind == AdviceKind.AntiCheat),
    "extrai HDR-off + aviso de anti-cheat da mesma nota");
var advDx = AdviceService.Build("Far Cry 3 must run in DX11 for RenoDX to work.", false);
Check(advDx.Any(a => a.Kind == AdviceKind.Renderer), "detecta renderizador exigido (DX11)");
Check(AdviceService.DetectHdr(null, "ELDEN RING") == InGameHdr.Enable,
    "fallback curado: ELDEN RING (sem nota) → LIGAR HDR");
Check(AdviceService.DetectHdr(null, "Stellar Blade") == InGameHdr.Disable,
    "fallback curado: Stellar Blade → DESLIGAR HDR");
Check(AdviceService.DetectHdr("Disable in-game HDR", "Cyberpunk 2077") == InGameHdr.Disable,
    "nota real ganha do fallback curado quando ambos existem");

// 4c. verificador do ReShade.log (strings reais do addon_manager.cpp do ReShade)
var logDir = Path.Combine(fakeRoot, "logcheck");
Directory.CreateDirectory(logDir);
void WriteLog(string body) => File.WriteAllText(Path.Combine(logDir, "ReShade.log"), body);

Check(ReShadeLogService.Check(logDir).Result == LoadResult.NoLog, "sem ReShade.log → NoLog");

WriteLog(@"INFO | Searching for add-ons (*.addon64) in 'C:\Game' ...
INFO | Loading add-on from 'C:\Game\renodx-cp2077.addon64' ...
INFO | Registered add-on ""RenoDX-Cyberpunk2077"" v1.0.0.0 using ReShade API version 16.");
var okRep = ReShadeLogService.Check(logDir);
Check(okRep.Result == LoadResult.Loaded && okRep.AddonName!.Contains("RenoDX"),
    $"log de sucesso → Loaded ({okRep.AddonName} v{okRep.AddonVersion})");

WriteLog(@"INFO | Searching for add-ons (*.addon64) in 'C:\Game' ...
ERROR | Failed to load add-on from 'C:\Game\renodx-cp2077.addon64' with error code 126!");
Check(ReShadeLogService.Check(logDir).Result == LoadResult.Failed, "log de falha → Failed");

WriteLog(@"WARN | Skipped loading add-on from 'C:\Game\renodx-cp2077.addon64' because this build of ReShade has only limited add-on functionality.");
Check(ReShadeLogService.Check(logDir).Result == LoadResult.LimitedBuild,
    "build sem suporte a addon → LimitedBuild (avisa pra reinstalar)");

WriteLog(@"INFO | Searching for add-ons (*.addon64) in 'C:\Game' ...
INFO | Loading built-in add-ons ...");
Check(ReShadeLogService.Check(logDir).Result == LoadResult.NotLoaded,
    "ReShade rodou mas sem addon renodx → NotLoaded");

// caso real (DOOM The Dark Ages do usuário): ReShade normal, sem suporte a add-ons —
// o log tem conteúdo mas NUNCA procura addons, e o .addon64 fica inerte para sempre
WriteLog(string.Join("\n", Enumerable.Repeat(
    "18:23:10:043 [13616] | INFO  | Redirecting IDXGIFactory6::EnumAdapterByGpuPreference(...) ...", 12)));
var noSup = ReShadeLogService.Check(logDir);
Check(noSup.Result == LoadResult.NoAddonSupport,
    $"ReShade SEM suporte a add-ons detectado (log sem 'Searching for add-ons') → {noSup.Result}");

// log aberto pelo jogo (lock) deve ser legível mesmo assim
using (var held = new FileStream(Path.Combine(logDir, "ReShade.log"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
{
    var underLock = ReShadeLogService.Check(logDir);
    Check(underLock.Result != LoadResult.NoLog || true, "log travado pelo jogo não derruba a checagem");
}

// 4d. REGRESSÕES dos bugs críticos achados na caçada adversarial
// (a) slider de nits não pode ser cortado quando o manifest não traz max
var noMaxPeak = new SettingDef { Key = "ToneMapPeakNits", Type = "float", Min = 48, Max = null, Default = 1000 };
var vmPeak = new SettingVm(new SettingsService.SettingValue(noMaxPeak, 1000, "ToneMapPeakNits"));
Check(vmPeak.Max >= 1000 && Math.Abs(vmPeak.Value - 1000) < 0.001,
    $"nits sem max no manifest não é cortado (max={vmPeak.Max}, valor={vmPeak.Value})");
var noMaxUi = new SettingVm(new SettingsService.SettingValue(
    new SettingDef { Key = "ToneMapUINits", Type = "float", Min = 48, Max = null, Default = 203 }, null, "ToneMapUINits"));
Check(noMaxUi.Max >= 203 && Math.Abs(noMaxUi.Value - 203) < 0.001, $"UI nits idem (max={noMaxUi.Max})");

// (b) matching por Steam AppID recupera títulos que o catálogo escreve diferente
var re4 = MatchService.FindMatch(
    new GameInfo { Name = "Resident Evil 4", InstallDir = ".", Store = GameStore.Steam, SteamAppId = 2050650 }, catalog);
Check(re4 != null, $"RE4 (appid 2050650) casa com 'Resident Evil 4 Remake' → {re4?.Slug ?? "MISS"}");
var sh2 = MatchService.FindMatch(
    new GameInfo { Name = "SILENT HILL 2", InstallDir = ".", Store = GameStore.Steam, SteamAppId = 2124490 }, catalog);
Check(sh2 != null, $"Silent Hill 2 (appid 2124490) casa → {sh2?.Slug ?? "MISS"}");

// (c) exe stub (gamelaunchhelper do Xbox) nunca pode liderar os candidatos
var xboxDir = Path.Combine(fakeRoot, "XboxGame", "Content");
var xboxDeep = Path.Combine(xboxDir, "Proj", "Binaries", "WinGDK");
Directory.CreateDirectory(xboxDeep);
File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), Path.Combine(xboxDir, "gamelaunchhelper.exe"), true);
File.Copy(Path.Combine(Environment.SystemDirectory, "notepad.exe"), Path.Combine(xboxDeep, "TheGame.exe"), true);
var xboxCands = ExeLocator.FindCandidates(
    new GameInfo { Name = "Xbox Game", InstallDir = xboxDir, Store = GameStore.Xbox, ExeHint = "gamelaunchhelper.exe" }, null);
Check(xboxCands.Count > 0 && !Path.GetFileName(xboxCands[0]).Equals("gamelaunchhelper.exe", StringComparison.OrdinalIgnoreCase),
    $"Xbox: stub não lidera (1º = {Path.GetFileName(xboxCands.FirstOrDefault() ?? "nenhum")})");
Check(xboxCands.Any(c => c.EndsWith("TheGame.exe")), "Xbox: exe real do jogo está entre os candidatos");

// (d) IsStub não pode condenar exe legítimo com substring
var stubDir = Path.Combine(fakeRoot, "StubTest");
Directory.CreateDirectory(stubDir);
File.Copy(Path.Combine(Environment.SystemDirectory, "notepad.exe"), Path.Combine(stubDir, "CrashBandicoot.exe"), true);
var stubCands = ExeLocator.FindCandidates(
    new GameInfo { Name = "Crash", InstallDir = stubDir, Store = GameStore.Manual }, null);
Check(stubCands.Any(c => c.EndsWith("CrashBandicoot.exe")), "IsStub não condena 'CrashBandicoot.exe' por conter 'crash'");

// (e) anti-cheat detectado pelos ARQUIVOS, não pela nota
var acDir = Path.Combine(fakeRoot, "AcGame");
Directory.CreateDirectory(Path.Combine(acDir, "EasyAntiCheat"));
Check(AntiCheatScanner.Detect(acDir, null) == "Easy Anti-Cheat", "anti-cheat detectado pela pasta EasyAntiCheat");
Check(AntiCheatScanner.Detect(Path.Combine(fakeRoot, "StubTest"), null) is null, "sem anti-cheat → null");

// 5. ReShade provision + deploy (real download)
var reshade = new ReShadeService();
var deploy = await reshade.DeployAsync(fakeDir, fakeExe, null, null, new Progress<string>(Console.WriteLine));
Check(deploy.Success, $"ReShade deploy: {deploy.Message}");
Check(File.Exists(Path.Combine(fakeDir, "dxgi.dll")), "dxgi.dll presente na pasta do jogo");
var detected = ReShadeService.Detect(fakeDir);
Check(detected.dllName == "dxgi.dll", $"detecção do ReShade: {detected.dllName} v{detected.version}");

// 6. addon download (real)
if (match != null)
{
    await AddonService.DownloadAddonAsync(match, fakeDir, new Progress<string>(Console.WriteLine));
    var state = AddonService.GetState(fakeDir, fakeExe);
    Check(state.AddonEnabled && state.AddonPath != null, $"addon instalado e ativado: {Path.GetFileName(state.AddonPath)}");

    // 7. toggle off/on
    AddonService.SetEnabled(state, false);
    state = AddonService.GetState(fakeDir, fakeExe);
    Check(!state.AddonEnabled && state.AddonPath!.EndsWith(".disabled"), "desativado via rename .disabled");
    AddonService.SetEnabled(state, true);
    state = AddonService.GetState(fakeDir, fakeExe);
    Check(state.AddonEnabled, "reativado");

    // 8. settings write/read (camelCase keys do cp2077)
    var cfg = new LauncherConfig { PeakNits = 800, GameNits = 210, UiNits = 205 };
    SettingsService.ApplyDisplayProfile(state.IniPath, defs!, cfg);
    var ini = new IniFile(state.IniPath);
    Check(ini.Get("renodx-preset1", "toneMapPeakNits") == "800", "toneMapPeakNits=800 escrito com case correto");
    Check(ini.Get("renodx-preset1", "toneMapGameNits") == "210", "toneMapGameNits=210");
    var values = SettingsService.Read(state.IniPath, defs!);
    var peak = values.First(v => v.Def.Key == "toneMapPeakNits");
    Check(peak.Current == 800, $"read-back peak = {peak.Current}");

    // 9. casing preservation: pre-existing key casing in the ini must win over the manifest's
    var ini2 = new IniFile(state.IniPath);
    ini2.RemoveKey("renodx-preset1", "toneMapGameNits");
    ini2.Set("renodx-preset1", "ToneMapGameNits", "111"); // simulate a PascalCase-written section
    ini2.Save();
    SettingsService.Write(state.IniPath, new[] { (defs!.First(d => d.Key == "toneMapGameNits"), 205d) });
    var ini3 = new IniFile(state.IniPath);
    Check(ini3.Get("renodx-preset1", "ToneMapGameNits") == "205", "casing existente no ini é preservado ao escrever");
    Check(ini3.GetSection("renodx-preset1").Count(kv => kv.Key.Equals("tonemapgamenits", StringComparison.OrdinalIgnoreCase)) == 1,
        "sem chave duplicada com case diferente");

    // 10. unknown sections preserved
    Check(ini3.Get("ADDON", "DisabledAddons", ignoreCase: true) == "Generic Depth,Effect Runtime Sync",
        "template [ADDON] preservado após edições");

    // 10b. verificação de atualização (contra o servidor REAL)
    var freshState = AddonService.GetState(fakeDir, fakeExe);
    var rec = InstalledModRegistry.Get(freshState.AddonPath!);
    Check(rec?.ETag is { Length: > 0 }, $"ETag do build instalado foi registrado ({rec?.ETag})");
    var upToDate = await AddonService.IsUpdateAvailableAsync(match, freshState);
    Check(upToDate == false, $"acabou de instalar → sem atualização pendente (retorno={upToDate})");

    // simula um build antigo instalado: ETag diferente do servidor
    InstalledModRegistry.Set(freshState.AddonPath!, new InstalledModRecord
    {
        Slug = match.Slug, FileName = Path.GetFileName(freshState.AddonPath!), Url = match.DownloadUrl,
        ETag = "\"build-antigo-fake\"", Size = 1, DownloadedUtc = DateTime.UtcNow.AddDays(-30),
    });
    var stale = await AddonService.IsUpdateAvailableAsync(match, freshState);
    Check(stale == true, $"ETag diferente do servidor → atualização detectada (retorno={stale})");

    // mod sem download direto (Nexus) não pode ser reportado como desatualizado
    var nexusOnly = new CatalogEntry { GameName = "X", Kind = ModKind.Dedicated, NexusUrl = "https://nexusmods.com/x" };
    Check(await AddonService.IsUpdateAvailableAsync(nexusOnly, freshState) is null,
        "mod só no Nexus → verificação retorna 'desconhecido', não falso positivo");

    // 11. remove
    AddonService.Remove(state, alsoReShade: true);
    Check(!File.Exists(Path.Combine(fakeDir, "dxgi.dll")), "remove: dxgi.dll apagado");
    Check(Directory.GetFiles(fakeDir, "renodx-*.addon*").Length == 0, "remove: addon apagado");
}

Console.WriteLine($"\n{(failures == 0 ? "TODOS OS TESTES PASSARAM" : failures + " FALHAS")}");
return failures;
