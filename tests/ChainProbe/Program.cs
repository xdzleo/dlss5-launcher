using System.IO;
using RenoDXLauncher.Services;

// Roda a mesma sequencia de checagens que MainViewModel.BuildDlss5Chain faz, contra uma pasta de
// jogo de verdade, e diz elo por elo qual passa. Sem isto a unica forma de saber por que o
// interruptor nao vira era abrir a interface e olhar -- e foi exatamente o passo que faltou nas
// tres correcoes anteriores.
//
//     dotnet run --project tests/ChainProbe -- "<pasta do jogo>" [caminho do exe]

// --luzes recebe PASTAS DE BIBLIOTECA (nao de jogo) e diz de que cor cada bolinha de DLSS 5
// nasceria na abertura do launcher, antes de qualquer clique.
if (args.Contains("--luzes"))
{
    ChainProbe.Luzes.Run(args.Where(a => !a.StartsWith("--")).ToArray());
    return 0;
}

// --indice mostra qual build o indice escolhe para cada tipo de placa. Fecha a ponta de cima da
// mesma pergunta que --runtime fecha embaixo: em RTX 40 tem de sair um `.SF`.
if (args.Contains("--indice"))
{
    var idx = new DlssIndexService();
    await idx.LoadAsync();
    // Por ARQUITETURA, e nao por "e Blackwell?": e assim que a escolha acontece desde a 1.72.
    // O build da NVIDIA so tem kernels sm_120, entao numa RTX 20/30/40 ele nao pode sair aqui.
    foreach (var (sm, placa) in new (int?, string)[]
             { (120, "RTX 50"), (89, "RTX 40"), (86, "RTX 30"), (75, "RTX 20"), (null, "desconhecida") })
    {
        var lista = idx.NeuralCandidates(sm);
        Console.WriteLine($"  {placa,-12} (sm_{sm?.ToString() ?? "?"}) -> {string.Join(" > ", lista.Select(e => e.Version))}");
    }
    return 0;
}

// --ponte baixa a ponte de DX11 para a biblioteca. Existe porque a URL dela ja quebrou uma vez
// em silencio (o projeto foi renomeado e o asset junto), e o unico jeito de notar era abrir um
// jogo DX11 com DLSS proprio e ver a cadeia vermelha.
if (args.Contains("--ponte"))
{
    var antes = File.Exists(NeuralUpliftService.LibraryBridge);
    Console.WriteLine($"  ja na biblioteca: {antes}");
    try
    {
        var veio = await NeuralUpliftService.FetchBridgeAsync(new Progress<string>(s => Console.WriteLine("  " + s)));
        var ok = File.Exists(NeuralUpliftService.LibraryBridge);
        Console.WriteLine($"  baixou={veio}  existe={ok}"
                          + (ok ? $"  ({new FileInfo(NeuralUpliftService.LibraryBridge).Length:N0} bytes)" : ""));
        return ok ? 0 : 2;
    }
    catch (Exception ex) { Console.WriteLine($"  FALHOU: {ex.Message}"); return 2; }
}

// --sm <nvngx_dlssnr.dll...> diz para QUAIS PLACAS cada build tem kernel, lendo os registros
// fatbin de dentro do arquivo. E a resposta para "instalei e nada acontece" numa RTX 40: o build
// da NVIDIA so tem sm_120, e ninguem — nem o addon, nem o jogo, nem o log — diz isso.
if (args.Contains("--sm"))
{
    var placa = NeuralUpliftService.ProbeHost().GpuName;
    var meuSm = CudaFatbin.SmDoNome(placa);
    Console.WriteLine($"  esta placa : {placa ?? "?"}  ->  {(meuSm is { } s ? $"sm_{s} ({CudaFatbin.Rotulo(s)})" : "sem sm conhecido")}");
    foreach (var dll in args.Where(a => !a.StartsWith("--")))
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var archs = CudaFatbin.Arquiteturas(dll);
        sw.Stop();
        var lista = archs.Count == 0 ? "(nao consegui ler)"
                  : string.Join(", ", archs.OrderBy(a => a).Select(a => $"sm_{a} {CudaFatbin.Rotulo(a)}"));
        var veredito = archs.Count == 0 ? "?" : meuSm is null ? "?" : archs.Contains(meuSm.Value) ? "RODA" : "NAO RODA";
        Console.WriteLine($"  [{veredito,-8}] {Path.GetFileName(dll),-32} {sw.ElapsedMilliseconds,5} ms  {lista}");
        Console.WriteLine($"             {dll}");
    }
    return 0;
}

// --runtime <caminho do nvngx_dlssnr.dll> <versao> <url> diz se este build seria ACEITO.
// Existe porque um hash fixado no codigo que nao bate e um bug invisivel aqui e fatal la: em
// RTX 40 o launcher baixava 111 MB do build certo e recusava na ultima linha.
// --achar-runtime roda a busca automatica do runtime neural e diz de onde ele veio. Existe para
// provar, sem abrir o launcher, que a busca acha a copia que outra ferramenta de DLSS ja trouxe
// para o disco — e que ela recusa a copia sem os kernels desta placa.
if (args.Contains("--achar-runtime"))
{
    var biblioteca = NeuralUpliftService.LibraryRuntime;
    var guardado = biblioteca + ".probe-bak";
    var tinha = File.Exists(biblioteca);
    if (tinha) File.Move(biblioteca, guardado, overwrite: true);
    try
    {
        var achado = NeuralUpliftService.AutoDiscoverRuntime([], new Progress<string>(Console.WriteLine));
        Console.WriteLine(achado is null
            ? "  nada encontrado no disco"
            : $"  achado em: {achado}");
        if (achado is not null)
        {
            var archs = CudaFatbin.Arquiteturas(achado);
            Console.WriteLine($"  arquiteturas: {string.Join(", ", archs.Select(a => $"sm_{a} {CudaFatbin.Rotulo(a)}"))}");
        }
    }
    finally
    {
        try { if (File.Exists(biblioteca)) File.Delete(biblioteca); } catch { }
        if (tinha) { try { File.Move(guardado, biblioteca, overwrite: true); } catch { } }
    }
    return 0;
}

if (args.Contains("--runtime"))
{
    var livres = args.Where(a => !a.StartsWith("--")).ToArray();
    if (livres.Length < 3) { Console.Error.WriteLine("uso: --runtime <dll> <versao> <url>"); return 1; }
    var (arq, versao, url) = (livres[0], livres[1], livres[2]);

    var assinado = DlssRuntimeService.IsGenuine(arq, out var porqueAssin);
    Console.WriteLine($"  assinatura NVIDIA : {(assinado ? "OK" : "NAO")}  ({porqueAssin})");

    var entrada = new DlssIndexService.Entry(DlssIndexService.KindNeural, versao, url);
    var aceito = NeuralUpliftService.BuildDaComunidadeConfiavel(entrada, arq, out var porque);
    Console.WriteLine($"  build da comunidade: {(aceito ? "ACEITO" : "recusado")}"
                      + (aceito ? "" : $"  -> {porque}"));
    Console.WriteLine($"  => o launcher {(assinado || aceito ? "INSTALA" : "RECUSA")} este runtime");
    return assinado || aceito ? 0 : 2;
}

// --capa "<nome>" [mais nomes...] tenta baixar a capa PELO NOME, como um jogo sem appid.
// Sem isto a unica forma de saber se a busca acerta seria abrir o launcher e olhar os cards.
if (args.Contains("--capa"))
{
    foreach (var nome in args.Where(a => !a.StartsWith("--")))
    {
        var g = new RenoDXLauncher.Models.GameInfo
        {
            Name = nome,
            InstallDir = Path.GetTempPath(),
            Store = RenoDXLauncher.Models.GameStore.Folder,
        };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var capa = await CoverService.GetCoverAsync(g, null);
        sw.Stop();
        var tam = capa is not null && File.Exists(capa) ? $"{new FileInfo(capa).Length / 1024} KB" : "-";
        Console.WriteLine($"  {(capa is null ? "SEM CAPA" : "capa    "),-9} {sw.ElapsedMilliseconds,5} ms  {tam,-8} {nome}");
        if (capa is not null) Console.WriteLine($"      {capa}");
    }
    return 0;
}
var dir = args.Length > 0 ? args[0] : null;
if (dir is null || !Directory.Exists(dir))
{
    Console.Error.WriteLine("uso: ChainProbe <pasta do jogo> [exe]");
    return 1;
}

var exe = args.Length > 1 && !args[1].StartsWith("--") ? args[1]
        : Directory.EnumerateFiles(dir, "*.exe").OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();

// --timing mede o custo de cada peca do carregamento de detalhe, em vez de checar a cadeia.
if (args.Contains("--timing"))
{
    ChainProbe.Timing.Run(dir, exe);
    return 0;
}

// --enrich mede o que BackgroundEnrichAsync paga POR JOGO para descobrir se ha instalacao ali.
// E o numero que decide se essa varredura pode rodar em todo jogo da lista ou so em alguns --
// e as bolinhas de DLSS 5 dependem de ela rodar em todos.
if (args.Contains("--enrich"))
{
    var opts = new EnumerationOptions
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = true,
        MaxRecursionDepth = 5,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var achou = Directory.EnumerateFiles(dir, "renodx-*.addon*", opts).FirstOrDefault();
    sw.Stop();
    Console.WriteLine($"{sw.ElapsedMilliseconds,6} ms  {(achou is null ? "nada" : "achou")}  {Path.GetFileName(dir)}");
    return 0;
}

// --pasta pergunta ao resolvedor o que ele acha que esta pasta e. Responde "por que este jogo
// apareceu na lista com esse nome" sem ter de deduzir.
if (args.Contains("--pasta"))
{
    var g = RenoDXLauncher.Services.FolderGameResolver.Resolve(dir, []);
    Console.WriteLine($"  nome     : {g.Name}");
    Console.WriteLine($"  store    : {g.Store}");
    Console.WriteLine($"  pasta    : {g.InstallDir}");
    Console.WriteLine($"  exeHint  : {g.ExeHint ?? "(nenhum)"}");
    Console.WriteLine($"  candidatos: {string.Join(" | ", RenoDXLauncher.Services.FolderGameResolver.CandidateNames(dir))}");
    return 0;
}

// --conflitos diz o que MAIS esta na pasta. E a resposta para "instalei e nao acontece nada"
// quando a cadeia inteira esta verde: outro mod sentado na vaga que o nosso ReShade precisa.
if (args.Contains("--conflitos"))
{
    var lista = ConflictScanner.Scan(dir, exe);
    if (lista.Count == 0) { Console.WriteLine("  (nada disputando espaco)"); return 0; }
    foreach (var c in lista)
        Console.WriteLine($"  [{c.Grau,-8}] {c.Arquivo,-32} {c.Ferramenta,-16} "
                          + $"{(c.PodeAfastar ? "afastavel" : "-")}\n     {c.Porque}");
    return 0;
}

// --api lista TODOS os executaveis da pasta com a API de cada um e a rota que cada um pediria.
// E o que responde "este jogo tem mais de uma API?" sem abrir o jogo -- e, no caso do Baldur's
// Gate 3, se os dois executaveis carregam o mesmo proxy (que seria carga dupla do ReShade).
if (args.Contains("--api"))
{
    foreach (var f in Directory.EnumerateFiles(dir, "*.exe").OrderByDescending(f => new FileInfo(f).Length))
    {
        var pe = PeUtils.Inspect(f);
        // Como a tela: so o que da sinal positivo de API entra na lista.
        var api = Dlss5Installer.ApiDoExe(f, exigirEvidencia: true);
        if (api == Dlss5Installer.GraficosApi.Desconhecida)
        {
            // Os imports interessam MAIS quando o exe e recusado: e a unica forma de saber se a
            // recusa esta certa (utilitario) ou se o jogo resolve a API de um jeito que a nossa
            // deteccao nao ve -- que foi o caso do Hitman: Blood Money.
            var im = PeUtils.Inspect(f)?.Imports ?? [];
            Console.WriteLine($"  {Path.GetFileName(f),-34} (recusado: sem evidencia de API)");
            Console.WriteLine($"     dgVoodoo.Applies={DgVoodooService.Applies(f)}  "
                              + $"32b={PeUtils.Inspect(f, readImports: false)?.Is64Bit == false}");
            Console.WriteLine($"     imports ({im.Count}): {string.Join(", ", im.Take(18))}");
            continue;
        }
        var d3d12 = Dlss5Installer.ReachesD3D12(f);
        var r = Dlss5Installer.Rotear(dir, f, NeuralUpliftService.Detect(dir, dir, null).HasDlss, d3d12);
        var interessa = pe?.Imports
            .Where(i => i.StartsWith("d3d", StringComparison.OrdinalIgnoreCase)
                        || i.Equals("dxgi.dll", StringComparison.OrdinalIgnoreCase)
                        || i.StartsWith("vulkan", StringComparison.OrdinalIgnoreCase))
            .ToArray() ?? [];
        Console.WriteLine($"  {Path.GetFileName(f),-34} {Dlss5Installer.ApiLabel(api),-7} "
                          + $"{(pe?.Is64Bit == false ? "32b" : "64b")}  "
                          + $"rota={(r.Ponte ? "Ponte" : r.OptiScaler ? "Opti" : r.Feeder ? "Feeder" : "direta")}");
        Console.WriteLine($"     imports graficos: {(interessa.Length > 0 ? string.Join(", ", interessa) : "(nenhum)")}");
    }
    // A rota da PASTA: a uniao do que todos os executaveis pedem. E o que o instalador cobre
    // agora, para o usuario nao ter de escolher API nenhuma.
    var uniao = Dlss5Installer.RotearPasta(dir, exe, NeuralUpliftService.Detect(dir, dir, null).HasDlss);
    Console.WriteLine($"  --> pasta: Ponte={uniao.Ponte}  Feeder={uniao.Feeder}  Opti={uniao.OptiScaler}"
                      + $"  multi-API={(uniao.Ponte && uniao.Feeder ? "SIM" : "nao")}");
    return 0;
}

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
// Espelha o launcher: a anulacao por Feeder presente saiu (o marcador .renodx-ours ja
// impede a deteccao de contar os runtimes que o launcher copiou).
var temDlssNativo = det.HasDlss;
// A mesma funcao que o launcher e o instalador usam -- esta sonda tinha a propria copia da
// regra, que e exatamente como o bug do Baldur's Gate 3 passou despercebido aqui.
var rota = Dlss5Installer.Rotear(dir, exe, temDlssNativo, alcancaD3d12);
var pedePonte = rota.Ponte && !feederActive;
var pedeFeeder = rota.Feeder && !bridgeActive;
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
// Espelha o launcher: sem tradutor, um jogo D3D9 nao tem onde o pass rodar -- e a cadeia ficava
// toda verde mesmo assim.
if (exe is not null && DgVoodooService.Applies(exe)
    && PeUtils.Inspect(exe, readImports: false)?.Is64Bit == false)
{
    var temTradutor = DxvkService.IsDeployed(dir) || DgVoodooService.IsDeployed(dir);
    elos.Add(("Tradutor", temTradutor,
              $"dxvk={DxvkService.IsDeployed(dir)}  dgvoodoo={DgVoodooService.IsDeployed(dir)}"));
}
// Direct3D 10: so o DXVK traduz (d3d10core.dll -> Vulkan); sem ele o jogo fecha ao criar o device.
else if (DxvkService.AppliesD3d10(exe))
{
    elos.Add(("TradutorDX10", DxvkService.IsDeployedD3d10(dir),
              $"dxvk-d3d10={DxvkService.IsDeployedD3d10(dir)}  (DXVK {DxvkService.D3d10Version}: d3d10+d3d10_1+d3d10core+d3d11+dxgi, ProductName DXVK)"));
}

// O portao que decide se o CARD de DLSS 5 sequer aparece na tela. E anterior a cadeia: se ele
// fecha, os elos nem sao desenhados, e o sintoma e "o card nao aparece neste jogo".
Console.WriteLine();
Console.WriteLine("  --- o card de DLSS 5 aparece? ---");
var feederServe = !temDlssNativo
                  && FeederService.Applies(exe, temDlssNativo, alcancaD3d12)
                  && (det.AddonSupportsNr || det.GenericAddonInLibrary);
Console.WriteLine($"    Offerable            = {det.Offerable}");
Console.WriteLine($"    temDlssNativo        = {temDlssNativo}   (HasDlss={det.HasDlss}, feeder={feederActive})");
Console.WriteLine($"    FeederService.Applies= {FeederService.Applies(exe, temDlssNativo, alcancaD3d12)}");
Console.WriteLine($"    AddonSupportsNr      = {det.AddonSupportsNr}");
Console.WriteLine($"    GenericAddonInLibrary= {det.GenericAddonInLibrary}");
Console.WriteLine($"    feederServe          = {feederServe}");
Console.WriteLine($"    -> card {(det.Offerable || feederServe ? "APARECE" : "NAO APARECE")}");
Console.WriteLine();

var todosOk = true;
foreach (var (nome, ok, porque) in elos)
{
    Console.WriteLine($"  [{(ok ? "OK  " : "FALHA")}] {nome,-13} {porque}");
    if (!ok) todosOk = false;
}
Console.WriteLine();
Console.WriteLine($"  Dlss5Ready = {todosOk}   -> o interruptor {(todosOk ? "MOSTRA LIGADO" : "continua dizendo 'instalar'")}");
return todosOk ? 0 : 2;
