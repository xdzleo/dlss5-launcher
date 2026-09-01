using System.IO;
using RenoDXLauncher.Services;

// Roda a mesma sequencia de checagens que MainViewModel.BuildDlss5Chain faz, contra uma pasta de
// jogo de verdade, e diz elo por elo qual passa. Sem isto a unica forma de saber por que o
// interruptor nao vira era abrir a interface e olhar -- e foi exatamente o passo que faltou nas
// tres correcoes anteriores.
//
//     dotnet run --project tests/ChainProbe -- "<pasta do jogo>" [caminho do exe]

var dir = args.Length > 0 ? args[0] : null;
if (dir is null || !Directory.Exists(dir))
{
    Console.Error.WriteLine("uso: ChainProbe <pasta do jogo> [exe]");
    return 1;
}

var exe = args.Length > 1 ? args[1]
        : Directory.EnumerateFiles(dir, "*.exe").OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();

Console.WriteLine($"pasta : {dir}");
Console.WriteLine($"exe   : {exe ?? "(nenhum)"}");
Console.WriteLine();

var ini = Path.Combine(dir, "ReShade.ini");
var det = NeuralUpliftService.Detect(dir, dir, null);

// --- as mesmas variaveis, na mesma ordem que a view model calcula ---
var addon = NeuralUpliftService.DeployedGenericAddon(dir);
var early = false;
if (File.Exists(ini) && addon is not null)
{
    var list = new IniFile(ini).Get("ADDON", "LoadFromDllMain", ignoreCase: true) ?? "";
    early = list.Split(',').Any(e => e.Trim().Equals(Path.GetFileName(addon), StringComparison.OrdinalIgnoreCase));
}
var iniHost64 = Path.Combine(dir, FeederService.Host64Dir, "ReShade.ini");
if (!early && File.Exists(iniHost64))
{
    var list = new IniFile(iniHost64).Get("ADDON", "LoadFromDllMain", ignoreCase: true) ?? "";
    early = list.Split(',').Any(e => e.Trim().Equals("renodx-dlss5.addon64", StringComparison.OrdinalIgnoreCase));
}

var host64 = Path.Combine(dir, FeederService.Host64Dir);
var noHost64 = Directory.Exists(host64);
var addonNoHost64 = noHost64 && File.Exists(Path.Combine(host64, "renodx-dlss5.addon64"));
var runtimeNoHost64 = noHost64 && File.Exists(Path.Combine(host64, NeuralUpliftService.RuntimeFile));

var bits64 = exe is null || PeUtils.Inspect(exe, readImports: false)?.Is64Bit != false;
var camadaVk = VulkanLayerService.IsRegistered(dir, bits64);

var feederActive = FeederService.IsDeployed(dir);
var bridgeActive = NeuralUpliftService.BridgeDeployed(dir);
var alcancaD3d12 = Dlss5Installer.ReachesD3D12(exe);
var temDlssNativo = det.HasDlss && !feederActive;
var pedePonte = temDlssNativo && !alcancaD3d12;
var pedeFeeder = !temDlssNativo && FeederService.Applies(exe, temDlssNativo, alcancaD3d12);
var rrEsperado = NeuralUpliftService.TemRuntimeLocal(dir);
var neuralApplied = NeuralUpliftService.IsApplied(dir, ini, null);

var elos = new List<(string Nome, bool Ok, string Porque)>
{
    ("ReShade",      det.ReShadeDllName is not null || camadaVk,
                     $"proxy={det.ReShadeDllName ?? "nenhum"}  camadaVulkan={camadaVk}  jogo64={bits64}"),
    ("Addon",        det.AddonSupportsNr || addonNoHost64,
                     $"AddonSupportsNr={det.AddonSupportsNr}  noHost64={addonNoHost64}"),
    ("Neural",       det.RuntimeDeployed || runtimeNoHost64,
                     $"RuntimeDeployed={det.RuntimeDeployed}  noHost64={runtimeNoHost64}"),
    ("RayReconstr",  !rrEsperado || File.Exists(Path.Combine(dir, NeuralUpliftService.RayReconstructionFile)),
                     $"esperado={rrEsperado}"),
    ("EarlyLoad",    early || addon is null,
                     $"early={early}  addonNaPasta={addon ?? "nenhum"}"),
    ("Switch",       neuralApplied,
                     $"IsApplied={neuralApplied}  ini={ini}"),
};
if (pedePonte || bridgeActive) elos.Add(("Bridge", bridgeActive, $"pede={pedePonte}"));
if (pedeFeeder || feederActive) elos.Add(("Feeder", feederActive, $"pede={pedeFeeder}  ativo={feederActive}"));

var todosOk = true;
foreach (var (nome, ok, porque) in elos)
{
    Console.WriteLine($"  [{(ok ? "OK  " : "FALHA")}] {nome,-13} {porque}");
    if (!ok) todosOk = false;
}
Console.WriteLine();
Console.WriteLine($"  Dlss5Ready = {todosOk}   -> o interruptor {(todosOk ? "MOSTRA LIGADO" : "continua dizendo 'instalar'")}");
return todosOk ? 0 : 2;
