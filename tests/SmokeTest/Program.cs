// End-to-end smoke test against a FAKE game dir (never touches real games):
// catalog fetch → match → ReShade provision (real download from reshade.me) →
// addon download (real renodx-cp2077.addon64) → toggle → settings write/read.
using System.IO;
using RenoDXLauncher.Models;
using RenoDXLauncher.Services;

int failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
    if (!ok) failures++;
}

var fakeDir = Path.Combine(Path.GetTempPath(), "RenoDXLauncherSmoke", "Cyberpunk 2077", "bin", "x64");
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

    // 11. remove
    AddonService.Remove(state, alsoReShade: true);
    Check(!File.Exists(Path.Combine(fakeDir, "dxgi.dll")), "remove: dxgi.dll apagado");
    Check(Directory.GetFiles(fakeDir, "renodx-*.addon*").Length == 0, "remove: addon apagado");
}

Console.WriteLine($"\n{(failures == 0 ? "TODOS OS TESTES PASSARAM" : failures + " FALHAS")}");
return failures;
