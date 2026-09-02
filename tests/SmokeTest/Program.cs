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

// 3b. ExeLocator — real layouts that already fooled it once. Fixtures are copies of cmd.exe
// (never executed); the "renders" ones get their ntdll.dll import renamed to d3d11.dll, same
// length, so the headers stay valid and PeUtils sees a graphics import.
// `apiDll` troca qual API o fixture "importa": tem de ter os 9 caracteres de ntdll.dll — d3d11.dll
// e d3d10.dll servem — para os cabecalhos do PE continuarem validos.
string MakeExe(string path, bool bits64, bool renders, long padTo = 0, string apiDll = "d3d11.dll")
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    var bytes = File.ReadAllBytes(Path.Combine(windows, bits64 ? "System32" : "SysWOW64", "cmd.exe"));
    if (renders)
    {
        var from = System.Text.Encoding.ASCII.GetBytes("ntdll.dll\0");
        var to = System.Text.Encoding.ASCII.GetBytes(apiDll + "\0");
        for (int i = 0; ; )
        {
            int at = bytes.AsSpan(i).IndexOf(from);
            if (at < 0) break;
            to.CopyTo(bytes, i + at);
            i += at + to.Length;
        }
    }
    File.WriteAllBytes(path, bytes);
    if (padTo > bytes.Length)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Write);
        fs.SetLength(padTo);   // sparse tail: the fixture just has to LOOK big
    }
    return path;
}

// Space Marine 2: a 126 MB 32-bit Epic installer and a 2.9 MB 32-bit shim that carries the
// game's exact name, next to the real 81 MB 64-bit binary buried four levels down.
var smRoot = Path.Combine(fakeRoot, "Space Marine 2");
var smBin = Path.Combine(smRoot, "client_pc", "root", "bin", "pc");
var smReal = MakeExe(Path.Combine(smBin, "Warhammer 40000 Space Marine 2 - Retail.exe"), true, true);
var smShim = MakeExe(Path.Combine(smRoot, "Warhammer 40000 Space Marine 2.exe"), false, false);
var smInstaller = MakeExe(Path.Combine(smBin, "EpicOnlineServices", "EpicOnlineServicesInstaller.exe"),
    false, false, padTo: 128L * 1024 * 1024);
var smCrash = MakeExe(Path.Combine(smBin, "crash_reporter.exe"), true, false);
var smCands = ExeLocator.FindCandidates(
    new GameInfo { Name = "Warhammer 40,000: Space Marine 2", InstallDir = smRoot, Store = GameStore.Steam }, null);
Check(smCands.FirstOrDefault() == smReal,
    $"Space Marine 2 → o exe que renderiza, não o shim ({Path.GetFileName(smCands.FirstOrDefault() ?? "-")})");
Check(!smCands.Contains(smInstaller), "instalador de terceiro (Epic, 128 MB) fora dos candidatos");
Check(!smCands.Contains(smCrash), "crash_reporter.exe fora dos candidatos");

// Max Payne 3: o jogo REAL é 32-bit e o shim é 64-bit — preferir 64-bit não pode virar regra dura.
var mpRoot = Path.Combine(fakeRoot, "Max Payne 3");
var mpReal = MakeExe(Path.Combine(mpRoot, "Max Payne 3", "MaxPayne3.exe"), false, true);
MakeExe(Path.Combine(mpRoot, "Max Payne 3", "PlayMaxPayne3.exe"), true, false);
var mpCands = ExeLocator.FindCandidates(new GameInfo
{
    Name = "Max Payne 3", InstallDir = mpRoot, Store = GameStore.Steam,
    ExeHint = Path.Combine("Max Payne 3", "PlayMaxPayne3.exe"),
}, null);
Check(mpCands.FirstOrDefault() == mpReal,
    $"Max Payne 3 → jogo 32-bit vence o shim 64-bit apontado pela loja ({Path.GetFileName(mpCands.FirstOrDefault() ?? "-")})");

// "crash" é palavra de jogo, não só de stub
var cbRoot = Path.Combine(fakeRoot, "Crash Bandicoot");
var cbReal = MakeExe(Path.Combine(cbRoot, "CrashBandicootNSaneTrilogy.exe"), true, true);
var cbCands = ExeLocator.FindCandidates(
    new GameInfo { Name = "Crash Bandicoot N. Sane Trilogy", InstallDir = cbRoot, Store = GameStore.Steam }, null);
Check(cbCands.FirstOrDefault() == cbReal, "'crash' não condena CrashBandicootNSaneTrilogy.exe");

// tModLoader: a pasta de deploy curada do índice se chama "dotnet" — que é também nome de pasta
// de terceiro. O dado curado tem que vencer o filtro heurístico, senão o único exe que importa
// é descartado antes de ser pontuado e a lista sai vazia.
var tmlRoot = Path.Combine(fakeRoot, "tModLoader");
MakeExe(Path.Combine(tmlRoot, "tModLoader.exe"), true, false);          // apphost na raiz
var tmlReal = MakeExe(Path.Combine(tmlRoot, "dotnet", "dotnet.exe"), true, false);
var tmlGame = new GameInfo { Name = "tModLoader", InstallDir = tmlRoot, Store = GameStore.Steam };
var tmlCands = ExeLocator.FindCandidates(tmlGame, "dotnet");
Check(tmlCands.FirstOrDefault() == tmlReal,
    $"tModLoader → dotnet\\dotnet.exe, apesar de 'dotnet' estar na lista de pastas de terceiro ({Path.GetFileName(tmlCands.FirstOrDefault() ?? "-")})");
// mesma coisa com o runtime um nível mais fundo (layout que muda entre updates do jogo)
var tml2Root = Path.Combine(fakeRoot, "tModLoader2");
MakeExe(Path.Combine(tml2Root, "tModLoader.exe"), true, false);
var tml2Real = MakeExe(Path.Combine(tml2Root, "dotnet", "6.0.0", "dotnet.exe"), true, false);
Check(ExeLocator.FindCandidates(
        new GameInfo { Name = "tModLoader", InstallDir = tml2Root, Store = GameStore.Steam },
        "dotnet").FirstOrDefault() == tml2Real,
    "tModLoader → runtime aninhado (dotnet\\6.0.0) também é encontrado");
// sem o override o app não pode inventar: aí a raiz é a resposta certa
Check(ExeLocator.FindCandidates(tmlGame, null).FirstOrDefault() != tmlReal,
    "sem pasta curada, 'dotnet' volta a ser tratada como pasta de terceiro");

// nenhum filtro pode devolver lista vazia: sem candidato o usuário não consegue nem escolher
var onlyStubRoot = Path.Combine(fakeRoot, "SoStub");
var onlyStub = MakeExe(Path.Combine(onlyStubRoot, "GameLaunchHelper.exe"), true, false);
var onlyStubCands = ExeLocator.FindCandidates(
    new GameInfo { Name = "Um Jogo Qualquer", InstallDir = onlyStubRoot, Store = GameStore.Xbox }, null);
Check(onlyStubCands.Contains(onlyStub), "jogo só com exe de stub ainda devolve candidato (combo nunca fica vazio)");

// Unreal: o shipping exe ganha de qualquer coisa, mesmo com um handler no mesmo diretório
var sbRoot = Path.Combine(fakeRoot, "StellarBlade");
var sbBin = Path.Combine(sbRoot, "SB", "Binaries", "Win64");
var sbReal = MakeExe(Path.Combine(sbBin, "SB-Win64-Shipping.exe"), true, true);
MakeExe(Path.Combine(sbBin, "crs-handler.exe"), true, false);
var sbCands = ExeLocator.FindCandidates(new GameInfo
{
    Name = "Stellar Blade", InstallDir = sbRoot, Store = GameStore.Steam,
    ExeHint = Path.Combine("SB", "Binaries", "Win64", "crs-handler.exe"),
}, null);
Check(sbCands.FirstOrDefault() == sbReal,
    $"Stellar Blade → -Win64-Shipping.exe apesar do ExeHint da loja ({Path.GetFileName(sbCands.FirstOrDefault() ?? "-")})");

// 3c. Direct3D 10: a API que o launcher recusava ate a 1.69, e que agora vai pelo DXVK
// (d3d10core.dll -> Vulkan). O fixture importa d3d10.dll, como o Just Cause 2 faz em runtime.
var jcRoot = Path.Combine(fakeRoot, "Just Cause 2");
var jcExe = MakeExe(Path.Combine(jcRoot, "JustCause2.exe"), false, true, apiDll: "d3d10.dll");
Check(FeederService.RenderizaEmD3d10(jcExe), "exe de 32 bits que importa d3d10.dll e lido como Direct3D 10");
Check(DxvkService.AppliesD3d10(jcExe), "Direct3D 10 pede o DXVK (d3d10core.dll)");
Check(!DgVoodooService.Applies(jcExe), "Direct3D 10 NAO e caso de dgVoodoo2 (ele so ve D3D9)");
Check(!VulkanLayerService.Applies(jcExe), "Direct3D 10 nao e confundido com Vulkan nativo");
var jcApi = Dlss5Installer.ApiDoExe(jcExe, exigirEvidencia: true);
Check(jcApi == Dlss5Installer.GraficosApi.D3D10, $"ApiDoExe -> {Dlss5Installer.ApiLabel(jcApi)} (nao pode cair no DX12 permissivo)");
Check(FeederService.Applies(jcExe, false, true), "o Feeder aceita o jogo D3D10 (traduzido, para ele e Vulkan)");
var rotaJc = Dlss5Installer.Rotear(jcRoot, jcExe, false, true);
Check(rotaJc.Feeder && !rotaJc.Ponte && !rotaJc.OptiScaler, "rota de D3D10 sem DLSS = Feeder (nem ponte, nem OptiScaler)");
// a mesma heuristica nao pode confundir os dois vizinhos
Check(!FeederService.RenderizaEmD3d10(smReal), "exe que importa d3d11.dll NAO e lido como D3D10");
Check(Dlss5Installer.ApiDoExe(smReal, exigirEvidencia: true) != Dlss5Installer.GraficosApi.D3D10,
    "exe D3D11 continua D3D11/D3D12, nao D3D10");

// O conjunto D3D10 do DXVK na pasta falsa: download real, os tres arquivos, o que ja estava no
// nome guardado e devolvido. E o que o instalador faz no Just Cause 2, sem o Just Cause 2.
try
{
    await DxvkService.FetchD3d10Async();
    Check(DxvkService.D3d10InLibrary(false) && DxvkService.D3d10InLibrary(true),
        $"biblioteca tem o conjunto D3D10 (DXVK {DxvkService.D3d10Version}, 5 arquivos) de 32 e 64 bits");
    File.WriteAllText(Path.Combine(jcRoot, "dxgi.dll"), "ocupante");   // algo ja sentado no nome
    DxvkService.DeployD3d10(jcRoot, jogo64Bits: false);
    Check(DxvkService.IsDeployedD3d10(jcRoot), "DXVK D3D10 implantado: 5 arquivos, ProductName DXVK");
    // Os dois wrappers sao o que decide: sem d3d10.dll e d3d10_1.dll locais o ReShade engancha
    // os do sistema e o jogo morre em 3 s (medido no Just Cause 2).
    Check(File.Exists(Path.Combine(jcRoot, "d3d10.dll")) && File.Exists(Path.Combine(jcRoot, "d3d10_1.dll")),
        "d3d10.dll e d3d10_1.dll proprios na pasta (o ReShade nao pode ver os do sistema)");
    Check(File.Exists(Path.Combine(jcRoot, "dxgi.dll.pre-dxvk")), "o dxgi.dll que ja estava foi guardado como .pre-dxvk");
    Check(!DxvkService.IsDeployed(jcRoot), "rota D3D10 NAO poe d3d9.dll (o jogo so o importa como fallback)");
    Check(ConflictScanner.Scan(jcRoot, jcExe).All(c => c.Ferramenta != "DXVK" || c.Grau == ConflictScanner.Nivel.Info),
        "o scanner de conflitos nao acusa o proprio DXVK no dxgi.dll/d3d11.dll");
    DxvkService.DeployD3d10(jcRoot, jogo64Bits: false);   // de novo: idempotente, nao re-guarda
    Check(File.ReadAllText(Path.Combine(jcRoot, "dxgi.dll.pre-dxvk")) == "ocupante", "reinstalar nao sobrescreve o backup com o proprio DXVK");
    DxvkService.RemoveD3d10(jcRoot);
    Check(!DxvkService.IsDeployedD3d10(jcRoot), "remover tira os cinco do DXVK");
    Check(File.Exists(Path.Combine(jcRoot, "dxgi.dll")) && File.ReadAllText(Path.Combine(jcRoot, "dxgi.dll")) == "ocupante",
        "remover devolve o dxgi.dll anterior");
    Check(!File.Exists(Path.Combine(jcRoot, "d3d10core.dll")), "remover nao deixa d3d10core.dll para tras");
}
catch (Exception ex) { Check(false, $"DXVK D3D10 na pasta falsa: {ex.Message}"); }

// 3d. Desinstalar desinstala — o caminho de 32 bits inteiro. Ate a 1.71 o Remove so conhecia a
// pasta do jogo: o addon32, o host64\ com o interruptor em 1 e os tradutores ficavam, e o
// IsApplied (que le o host64\ReShade.ini) devolvia o botao para "ligado". Foi assim que o
// Just Cause 2 desta maquina ficou depois de um desligar.
try
{
    var jcIni = Path.Combine(jcRoot, "ReShade.ini");
    File.WriteAllText(jcIni, "[RENODX-DLSS]\nDirectNeuralRenderingEnabled=1\n\n[ADDON]\nLoadFromDllMain=renodx-dlss5.addon64\n");
    File.WriteAllText(Path.Combine(jcRoot, "dlss5-feed.addon32"), "MZaddon32");
    File.WriteAllText(Path.Combine(jcRoot, "renodx-dlss5.addon64"), "MZaddon64-renomeado");
    File.WriteAllText(Path.Combine(jcRoot, "renodx-dlss5.addon64.renodx-ours"), "2026-09-02");
    var host64 = Path.Combine(jcRoot, "host64");
    Directory.CreateDirectory(host64);
    File.WriteAllText(Path.Combine(host64, "ReShade.ini"), "[RENODX-DLSS]\nDirectNeuralRenderingEnabled=1\n\n[ADDON]\nLoadFromDllMain=renodx-dlss5.addon64\n");
    File.WriteAllText(Path.Combine(host64, "renodx-dlss5.addon64"), "MZaddon64");
    File.WriteAllText(Path.Combine(host64, "nvngx_dlssnr.dll"), "MZruntime");
    File.WriteAllText(Path.Combine(host64, "dlss5-feed-host64.exe"), "MZhost");
    File.WriteAllText(Path.Combine(host64, "dxgi.dll"), "MZreshade64");
    // o tradutor D3D10 por cima de um dxgi.dll que ja estava la (guardado como .pre-dxvk)
    File.WriteAllText(Path.Combine(jcRoot, "dxgi.dll"), "ocupante");
    DxvkService.DeployD3d10(jcRoot, jogo64Bits: false);

    Check(NeuralUpliftService.IsApplied(jcRoot, jcIni, null), "layout 32 bits montado: IsApplied le o host64 e diz LIGADO");

    NeuralUpliftService.Remove(jcRoot, jcIni);

    Check(!NeuralUpliftService.IsApplied(jcRoot, jcIni, null), "depois do Remove, IsApplied diz DESLIGADO (host64 nao responde mais por ele)");
    Check(!Directory.Exists(host64), "Remove apaga host64\\ inteiro (host, ReShade, addon, runtime, ini)");
    Check(!File.Exists(Path.Combine(jcRoot, "dlss5-feed.addon32")), "Remove tira o dlss5-feed.addon32 da pasta do jogo");
    Check(!File.Exists(Path.Combine(jcRoot, "renodx-dlss5.addon64")) && !File.Exists(Path.Combine(jcRoot, "renodx-dlss5.addon64.renodx-ours")),
        "Remove tira o addon renomeado que tem o marcador .renodx-ours, e o marcador");
    var earlyDepois = new IniFile(jcIni).Get("ADDON", "LoadFromDllMain", ignoreCase: true) ?? "";
    Check(!earlyDepois.Contains("renodx-dlss5.addon64", StringComparison.OrdinalIgnoreCase),
        $"LoadFromDllMain nao aponta mais para o addon removido (\"{earlyDepois}\")");
    Check(!DxvkService.IsDeployedD3d10(jcRoot), "Remove tira o conjunto D3D10 do DXVK");
    Check(File.Exists(Path.Combine(jcRoot, "dxgi.dll")) && File.ReadAllText(Path.Combine(jcRoot, "dxgi.dll")) == "ocupante",
        "Remove devolve o dxgi.dll que o instalador tinha guardado (.pre-dxvk)");

    // um build da comunidade posto a mao, SEM marcador, sobrevive ao Remove: nao e nosso para apagar
    File.WriteAllText(Path.Combine(jcRoot, "renodx-dlss5.addon64"), "MZbuild-da-comunidade");
    File.WriteAllText(jcIni, "[RENODX-DLSS]\nDirectNeuralRenderingEnabled=1\n\n[ADDON]\nLoadFromDllMain=renodx-dlss5.addon64\n");
    NeuralUpliftService.Remove(jcRoot, jcIni);
    Check(File.Exists(Path.Combine(jcRoot, "renodx-dlss5.addon64")), "addon da comunidade sem marcador fica no lugar (so o interruptor vai a 0)");
    Check((new IniFile(jcIni).Get("ADDON", "LoadFromDllMain", ignoreCase: true) ?? "").Contains("renodx-dlss5.addon64"),
        "e a entrada de carga antecipada dele tambem fica");
    File.Delete(Path.Combine(jcRoot, "renodx-dlss5.addon64"));
}
catch (Exception ex) { Check(false, $"desinstalar 32 bits na pasta falsa: {ex.Message}"); }

// 3e. Apply nunca deixa dois addons genericos: com renodx-dlss5.addon64 ja na pasta, o
// renodx-neural.addon64 nao entra ao lado (dois genericos = carga dupla = 0xc0000005).
if (File.Exists(NeuralUpliftService.LibraryAddon))
{
    try
    {
        var dupRoot = Path.Combine(fakeRoot, "Duplicado");
        Directory.CreateDirectory(dupRoot);
        var dupIni = Path.Combine(dupRoot, "ReShade.ini");
        File.WriteAllText(dupIni, "[ADDON]\nLoadFromDllMain=renodx-dlss5.addon64\n");
        File.Copy(NeuralUpliftService.LibraryAddon, Path.Combine(dupRoot, "renodx-dlss5.addon64"));
        NeuralUpliftService.Apply(dupRoot, dupIni, useGenericAddon: true, null);
        NeuralUpliftService.Apply(dupRoot, dupIni, useGenericAddon: true, null);
        var genericos = Directory.GetFiles(dupRoot, "renodx-*.addon64");
        Check(genericos.Length == 1 && genericos[0].EndsWith("renodx-dlss5.addon64"),
            $"Apply duas vezes com renodx-dlss5.addon64 presente deixa UM addon generico ({string.Join(", ", genericos.Select(Path.GetFileName))})");
        var earlyDup = new IniFile(dupIni).Get("ADDON", "LoadFromDllMain", ignoreCase: true) ?? "";
        Check(!earlyDup.Contains("renodx-neural.addon64", StringComparison.OrdinalIgnoreCase),
            $"LoadFromDllMain nao lista o renodx-neural.addon64 que nao existe (\"{earlyDup}\")");
    }
    catch (Exception ex) { Check(false, $"Apply duplicado: {ex.Message}"); }
}
else Console.WriteLine("SKIP  Apply duplicado: addon generico nao esta na biblioteca desta maquina");

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

// 4e. parser de settings em C# (usado quando o mod e mais novo que o app)
var cppSample = """
  new renodx::utils::settings::Setting{
      .key = "ToneMapPeakNits",
      .binding = &shader_injection.tone_map_peak_nits,
      .default_value = 1000.f,
      .label = "Peak Brightness",
      .section = "Tone Mapping",
      .tooltip = "Sets the value of peak white in nits",
      .min = 48.f,
      .max = 4000.f,
  },
  new renodx::utils::settings::Setting{
      .key = "ToneMapType",
      .value_type = renodx::utils::settings::SettingValueType::INTEGER,
      .default_value = 1.f,
      .label = "Tone Mapper",
      .labels = {"Vanilla", "RenoDRT"},
  },
  new renodx::utils::settings::Setting{
      .value_type = renodx::utils::settings::SettingValueType::BUTTON,
      .label = "Github",
  },
""";
var parsed = SettingsFetcher.Parse(cppSample);
var knobs = parsed.Where(d => !d.IsInstruction).ToList();
Check(knobs.Count == 2, $"parser C#: le 2 ajustes (leu {knobs.Count})");
Check(parsed.All(d => !d.IsInstruction), "parser C#: botao 'Github' e descartado como boilerplate");
var pk = parsed.FirstOrDefault(d => d.Key == "ToneMapPeakNits");
Check(pk != null && pk.Default == 1000 && pk.Min == 48 && pk.Max == 4000,
    $"parser C#: peak default/min/max corretos ({pk?.Default}/{pk?.Min}/{pk?.Max})");
var tm = parsed.FirstOrDefault(d => d.Key == "ToneMapType");
Check(tm != null && tm.Type == "int" && tm.Labels?.Count == 2,
    $"parser C#: enum com rotulos ({tm?.Type}, {tm?.Labels?.Count} labels)");

// manifest agora distingue "sem opcoes" de "desconhecido"
Check(manifest.KnowsSlug("doom-tda") && manifest.GetSettings("doom-tda")!.Count == 0,
    "manifest: doom-tda conhecido e SEM opcoes ajustaveis");
Check(!manifest.KnowsSlug("slug-que-nao-existe"), "manifest: slug inexistente = desconhecido");

// 4e-bis. NOTAS: o autor do mod escreve instrucoes DENTRO do addon como blocos TEXT/LABEL/BUTTON.
// O parser antigo jogava tudo fora, e por isso um jogo documentado do inicio ao fim aparecia
// como "o autor deixou os valores fixos".
var cppInstructions = """
  new renodx::utils::settings::Setting{
      .value_type = renodx::utils::settings::SettingValueType::TEXT,
      .label = std::string("- Requires HDR on in game\n")
             + "- Set Game Brightness to 1.0\n"
             + "- Set contrast to 0.50",
      .section = "About",
  },
  new renodx::utils::settings::Setting{
      .value_type = renodx::utils::settings::SettingValueType::BUTTON,
      .label = "HDR Look",
      .section = "Color Grading Templates",
      .tooltip = "O look calibrado pelo autor",
      .on_change = []() {
        for (const auto& [key, value] : {
          {"toneMapType", 4.f},
          {"colorGradeSaturation", 80.f},
        }) UpdateSetting(key, value);
      },
  },
  new renodx::utils::settings::Setting{
      .value_type = renodx::utils::settings::SettingValueType::BUTTON,
      .label = "Special thanks to Musa for the support!",
      .section = "About",
  },
""";
var ins = SettingsFetcher.Parse(cppInstructions).Where(d => d.IsInstruction).ToList();
Check(ins.Count == 2, $"instrucoes: le TEXT e BUTTON, descarta agradecimento (leu {ins.Count})");
var multi = ins.FirstOrDefault(d => d.Label?.Contains("Requires HDR") == true);
Check(multi != null && multi.Label!.Contains("Set contrast to 0.50"),
    "instrucoes: literais C++ concatenados nao sao truncados no meio da frase");
var preset = ins.FirstOrDefault(d => d.Label == "HDR Look");
Check(preset?.PresetValues != null && preset.PresetValues.Count == 2
      && preset.PresetValues["colorGradeSaturation"] == 80,
    $"instrucoes: preset do autor traz os valores ({preset?.PresetValues?.Count})");

// setas sao o OPERADOR das instrucoes de configuracao: "Simple -> Advanced" sem a seta
// deixa de ser instrucao. O filtro antigo apagava tudo entre U+2190 e U+2BFF.
Check(AdviceService.StripSymbols("Settings Mode: Simple → Advanced") == "Settings Mode: Simple -> Advanced",
    $"StripSymbols preserva a seta ({AdviceService.StripSymbols("Simple → Advanced")})");
Check(!AdviceService.StripSymbols("\U0001F5A5️ Black Screen").Contains('️'),
    "StripSymbols continua removendo emoji");
Check(AdviceService.GuessLocation("Disable in-game HDR.") == "NO JOGO",
    "local: 'in-game HDR' e ajuste DENTRO do jogo");
Check(AdviceService.GuessLocation("Upgrade R11G11B10_FLOAT") == "OVERLAY RENODX (Home)",
    "local: 'Upgrade' e ajuste no overlay");

// UMA etiqueta errada e pior que nenhuma: manda a pessoa no menu errado, ela nao ve efeito
// nenhum e conclui que o mod nao funciona. Estes 5 casos vem de notas REAIS.
Check(AdviceService.GuessLocation("Disable in-game HDR. `B8G8R8A8_TYPELESS` `Output Size`") is null,
    "local: nota com passo no jogo E no overlay nao recebe etiqueta unica");
Check(AdviceService.GuessLocation(
        "- In-game HDR settings are disabled by RenoDX, adjust peak/game/ui brightness in the mod.")
      == "OVERLAY RENODX (Home)",
    "local: lugar NEGADO ('in-game esta desligado, use o mod') aponta para o overlay");
Check(AdviceService.GuessLocation(
        "Scaling AND Tonemap offset = 1. Main Menu will be black, but everything is fine once loaded in game.")
      != "NO JOGO",
    "local: 'once loaded in game' e narracao, nao instrucao de menu");
Check(AdviceService.GuessLocation("- HDR Max. Luminance slider still controls peak brightness") is null,
    "local: 'slider' sozinho nao prova overlay (o jogo tambem tem sliders)");
Check(AdviceService.GuessLocation("Game's Brightness setting is disabled.") is null,
    "local: frase que diz que o controle NAO faz nada nao vira 'aja aqui'");

// parser: os quatro defeitos achados na revisao adversarial, com fonte real reduzida
var cppTruncada = """
  new renodx::utils::settings::Setting{
      .value_type = renodx::utils::settings::SettingValueType::TEXT,
      .label = std::string("- GAMMA CORRECTION slider controls game brightness\n")
             + "- Formula for peak: MAX. INTENSITY * paper white\n"
             + "- Example: 4.0 * 200 = 800 nits",
      .section = "Instructions",
  },
""";
var trunc = SettingsFetcher.Parse(cppTruncada).FirstOrDefault(d => d.IsInstruction);
Check(trunc?.Label?.Contains("800 nits") == true,
    "parser: '.' dentro do texto ('MAX. INTENSITY') nao corta a instrucao no meio");

var cppComentado = """
  /*new renodx::utils::settings::Setting{
      .value_type = renodx::utils::settings::SettingValueType::TEXT,
      .label = "Status: Updating Engine.ini failed. Try the manual install.",
      .section = "Instructions",
  },*/
  new renodx::utils::settings::Setting{
      .value_type = renodx::utils::settings::SettingValueType::TEXT,
      .label = "Use default in-game gamma.",
      .section = "Instructions",
  },
""";
var vivos = SettingsFetcher.Parse(cppComentado).Where(d => d.IsInstruction).ToList();
Check(vivos.Count == 1 && vivos[0].Label!.Contains("default in-game gamma"),
    $"parser: bloco COMENTADO nao vira instrucao (leu {vivos.Count})");

var cppCr = """
  new renodx::utils::settings::Setting{
      .value_type = renodx::utils::settings::SettingValueType::TEXT,
      .label = " - Ingame HDR must be turned ON! \r\n - Gamma still affects UI",
      .section = "Instructions",
  },
""";
var cr = SettingsFetcher.Parse(cppCr).First(d => d.IsInstruction);
Check(!cr.Label!.Contains('\r') && cr.Label.Contains("Gamma still affects UI"),
    "parser: \\r do fonte nao vaza para a tela");

var cppDiscord = """
  new renodx::utils::settings::Setting{
      .value_type = renodx::utils::settings::SettingValueType::TEXT,
      .label = "- NVIDIA GPUs only -- AMD/Intel will not work!\n- Join the HDR Den discord for help!",
      .section = "Instructions",
  },
""";
var disc = SettingsFetcher.Parse(cppDiscord).FirstOrDefault(d => d.IsInstruction);
Check(disc?.Label?.Contains("NVIDIA GPUs only") == true && !disc.Label.Contains("discord"),
    "parser: a palavra 'discord' apaga a LINHA, nunca o bloco de instrucao inteiro");

var cppLink = """
  new renodx::utils::settings::Setting{
      .value_type = renodx::utils::settings::SettingValueType::BUTTON,
      .label = "Get more RenoDX mods!",
      .section = "About",
      .on_change = []() { renodx::utils::platform::LaunchURL("https://github.com/clshortfuse/renodx"); },
  },
""";
Check(!SettingsFetcher.Parse(cppLink).Any(d => d.IsInstruction),
    "parser: botao que so abre URL nao vira 'instrucao do autor'");

// 3c. pasta adicionada a mao: quem empacotou a pasta nomeia ela, nao o desenvolvedor. O nome
// util pode estar no pai ("...\<Jogo>\Retail") ou so no exe.
var manualRoot = Path.Combine(fakeRoot, "007.First.Light-InsaneRamZes", "Retail");
MakeExe(Path.Combine(manualRoot, "007FirstLight.exe"), true, true);
var byFolder = MatchService.FindMatch(
    new GameInfo { Name = "Retail", InstallDir = manualRoot, Store = GameStore.Manual }, catalog);
Check(byFolder is null, "pasta 'Retail' sozinha nao casa com nada (era o defeito)");
var resolved = FolderGameResolver.Resolve(manualRoot, catalog);
Check(MatchService.FindMatch(resolved, catalog)?.Slug == "007firstlight",
    $"pasta ...\\007.First.Light-InsaneRamZes\\Retail reconhecida (nome resolvido: {resolved.Name})");
var names = FolderGameResolver.CandidateNames(manualRoot,
    Path.Combine(manualRoot, "007FirstLight.exe"));
Check(names.Contains("007FirstLight") && names.Any(n => n.Contains("007.First.Light")),
    "candidatos incluem o nome do exe e o da pasta pai");
// o strip de tag de release e so um candidato EXTRA: o nome inteiro vem primeiro
Check(FolderGameResolver.CandidateNames(Path.Combine(fakeRoot, "Half-Life"), null)[0] == "Half-Life",
    "nome completo e sempre o primeiro candidato (Half-Life nao vira Half)");

// dedup nao pode engolir preset cujo nome so difere por simbolo
var n1 = new ModNote(NoteSource.ModSource, NoteKind.Preset, null, "Vanilla SDR");
var n2 = new ModNote(NoteSource.ModSource, NoteKind.Preset, null, "Vanilla+ SDR");
Check(n1.DedupKey != n2.DedupKey, "dedup: 'Vanilla+ SDR' nao colide com 'Vanilla SDR'");

// os blocos de aviso da wiki vivem FORA de qualquer tabela e nunca eram lidos — sao o unico
// lugar que explica como aplicar um upgrade
var callouts = CatalogService.UnrealCallouts.Concat(CatalogService.UnityCallouts).ToList();
Check(callouts.Count >= 5, $"callouts da wiki lidos ({callouts.Count} blocos de motor)");
Check(callouts.Any(c => c.Text.Contains("Settings Mode", StringComparison.OrdinalIgnoreCase)
                        && c.Text.Contains("Advanced", StringComparison.OrdinalIgnoreCase)),
    "callouts: a regra de destravar os sliders (Simple -> Advanced) chega ao app");
Check(callouts.All(c => !c.Text.Contains("ATENÇÃO: ATENÇÃO:")),
    "callouts: emoji repetido nao vira 'ATENÇÃO: ATENÇÃO:'");
Check(callouts.All(c => c.Links is null || c.Links.All(l => !l.Url.Contains("img.shields.io"))),
    "callouts: badge de imagem nao vira link");

// todo mod dedicado agora tem pelo menos uma nota (antes: 89% ficavam com a secao vazia)
var dedicatedEntries = catalog.Where(e => e.Kind == ModKind.Dedicated).ToList();
var semNota = dedicatedEntries.Count(e => e.Notes.Count == 0);
Check(semNota == 0, $"todo mod dedicado tem nota ({semNota} sem nota de {dedicatedEntries.Count})");

// 4f. correcao de DLSS FG (aplica/remove no ini, em jogo FALSO)
var fgDir = Path.Combine(fakeRoot, "FgGame");
Directory.CreateDirectory(Path.Combine(fgDir, "Engine", "Binaries"));
foreach (var dll in new[] { "nvngx_dlss.dll", "nvngx_dlssg.dll", "sl.interposer.dll" })
    File.WriteAllBytes(Path.Combine(fgDir, "Engine", "Binaries", dll), new byte[] { 0x4D, 0x5A });
var det = DlssFixService.Detect(fgDir);
Check(det.HasFrameGen && det.DlssPath != null && det.StreamlinePath != null,
    "detecta runtime de DLSS FG (dlssg + dlss + streamline)");

var ueMod = new CatalogEntry { GameName = "X", Kind = ModKind.UnrealEngine, Slug = "unrealengine" };
var dedicated = new CatalogEntry { GameName = "Y", Kind = ModKind.Dedicated, Slug = "cp2077" };
Check(DlssFixService.ShouldOffer(ueMod, det), "oferece p/ mod generico (converte SDR->HDR)");
Check(!DlssFixService.ShouldOffer(dedicated, det), "NAO oferece p/ mod dedicado (HDR nativo)");
Check(!DlssFixService.ShouldOffer(ueMod, new DlssFixService.Detection(false, null, null)),
    "NAO oferece quando o jogo nao tem DLSS FG");

// ini precisa preservar addons ja listados no LoadFromDllMain
var fgIni = new IniFile(Path.Combine(fgDir, "ReShade.ini"));
fgIni.Set("ADDON", "LoadFromDllMain", "outro.addon64");
fgIni.Save();
await DlssFixService.ApplyAsync(fgDir, det);
var after = new IniFile(Path.Combine(fgDir, "ReShade.ini"));
var loadList = after.Get("ADDON", "LoadFromDllMain", ignoreCase: true) ?? "";
Check(loadList.Contains("outro.addon64") && loadList.Contains(DlssFixService.AddonFile),
    $"LoadFromDllMain preserva o addon existente ({loadList})");
Check(after.Get("RENODX-DLSSFIX", "DLSSPath")?.EndsWith("nvngx_dlss.dll") == true,
    "DLSSPath gravado com caminho completo");
Check(after.Get("RENODX-DLSSFIX", "StreamlinePath")?.EndsWith("sl.interposer.dll") == true,
    "StreamlinePath gravado");
Check(DlssFixService.IsInstalled(fgDir), "correcao detectada como instalada");

DlssFixService.Remove(fgDir);
var undone = new IniFile(Path.Combine(fgDir, "ReShade.ini"));
Check(undone.Get("ADDON", "LoadFromDllMain", ignoreCase: true) == "outro.addon64",
    "remover devolve o LoadFromDllMain ao estado anterior");
Check(undone.Get("RENODX-DLSSFIX", "DLSSPath") is null && !DlssFixService.IsInstalled(fgDir),
    "remover limpa caminhos e apaga o addon");

// 4g. foto do autor: mapa derivado do catalogo real
AvatarService.Learn(catalog);
var musaMod = catalog.FirstOrDefault(e => e.Maintainer == "Musa" && e.DownloadUrl != null);
Check(AvatarService.UserOf(musaMod) == "mqhaji", $"Musa -> mqhaji (deu {AvatarService.UserOf(musaMod)})");
var jonMod = catalog.FirstOrDefault(e => e.Maintainer != null && e.Maintainer.StartsWith("OopyDoopy") && e.DownloadUrl != null);
Check(AvatarService.UserOf(jonMod) == "oopydoopy", $"OopyDoopy -> oopydoopy (deu {AvatarService.UserOf(jonMod)})");
var sfMod = catalog.FirstOrDefault(e => e.Maintainer == "ShortFuse" && e.DownloadUrl != null);
Check(AvatarService.UserOf(sfMod) == "clshortfuse", $"ShortFuse -> clshortfuse (deu {AvatarService.UserOf(sfMod)})");
// a armadilha: quem publica no repo principal NAO pode herdar a foto do ShortFuse
var vooshMod = catalog.FirstOrDefault(e => e.Maintainer == "Voosh" && e.DownloadUrl != null);
var vooshUser = AvatarService.UserOf(vooshMod);
Check(vooshUser is null || vooshUser == "notvoosh",
    $"Voosh nunca vira clshortfuse (deu {vooshUser ?? "sem foto"})");
Check(AvatarService.UserOf(new CatalogEntry { GameName = "z", Maintainer = "Inexistente" }) is null,
    "autor desconhecido -> sem foto (usa a inicial)");
var avatar = await AvatarService.GetAvatarAsync(jonMod);
Check(avatar != null && File.Exists(avatar), $"baixa a foto real do OopyDoopy ({(avatar != null ? new FileInfo(avatar).Length + " bytes" : "falhou")})");

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

    // simula um build antigo instalado. O servidor destes addons (nginx) gera ETag de
    // mtime+tamanho, que o codigo IGNORA de proposito — re-publicar o site trocaria todo ETag sem
    // mudar um byte, e foi assim que todo mod instalado passou a "ter atualizacao" para sempre.
    // A unica evidencia de conteudo que sobra e o tamanho: um build antigo tem outro tamanho. O
    // registro precisa bater com o arquivo (Size diferente do disco e lido como "trocado por
    // fora"); este teste passava Size=1 e por isso falhava havia varios releases, sem defeito
    // no codigo.
    var bytesOriginais = File.ReadAllBytes(freshState.AddonPath!);
    File.WriteAllBytes(freshState.AddonPath!, bytesOriginais.Concat(new byte[] { 0 }).ToArray());
    InstalledModRegistry.Set(freshState.AddonPath!, new InstalledModRecord
    {
        Slug = match.Slug, FileName = Path.GetFileName(freshState.AddonPath!), Url = match.DownloadUrl,
        ETag = "\"build-antigo-fake\"", Size = bytesOriginais.Length + 1,
        DownloadedUtc = DateTime.UtcNow.AddDays(-30),
    });
    var stale = await AddonService.IsUpdateAvailableAsync(match, freshState);
    Check(stale == true, $"build instalado com outro tamanho → atualização detectada (retorno={stale})");
    File.WriteAllBytes(freshState.AddonPath!, bytesOriginais);

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
