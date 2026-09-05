using System.IO;
using System.Net.Http;
using RenoDXLauncher.Localization;

namespace RenoDXLauncher.Services;

/// <summary>
/// DXVK: a segunda rota para jogo Direct3D 9, e a que o dgVoodoo2 nao cobre.
///
/// O caminho DX9 -> DLSS 5 sempre precisou de um tradutor, porque o ReShade em D3D9 para no
/// Shader Model 3 e nenhum provedor de motion vectors compila. O dgVoodoo2 resolvia isso
/// entregando D3D11 — quando funciona. Em jogo que ele derruba (Resident Evil Revelations 2 e
/// Bayonetta, os dois confirmados por bisseccao nesta maquina, com o MESMO binario que roda
/// Saints Row 2 e Bully sem queixa) nao havia rota nenhuma.
///
/// O DXVK traduz D3D9 para Vulkan em vez de D3D11, e o Revelations 2 roda com ele sem crash.
/// Isso muda o resto da cadeia: o ReShade entra como CAMADA Vulkan (nao como proxy d3d9.dll),
/// e o add-on precisa falar Vulkan — que e exatamente o que o addon32 com transporte Vulkan faz.
///
/// A escolha entre os dois nao e preferencia estetica: o dgVoodoo continua sendo o padrao,
/// porque e a rota testada em mais jogos. O DXVK entra onde ele falha.
///
/// E cobre uma API a mais, onde nao ha escolha nenhuma: Direct3D 10 (ver <see cref="D3d10Files"/>).
/// </summary>
public static class DxvkService
{
    private const string Repo = "doitsujin/dxvk";
    public const string D3d9File = "d3d9.dll";

    /// <summary>
    /// A marca que diz "foi este launcher que pos", igual a dos outros servicos. Sem ela, a
    /// desinstalacao do DLSS 5 tirava QUALQUER d3d9.dll grande da pasta — inclusive um DXVK que
    /// o proprio usuario instalou num jogo cuja rota nunca precisou de tradutor — e sem copia,
    /// porque o Deploy nunca tinha passado por ali para guardar nada.
    /// </summary>
    private const string OursSuffix = ".renodx-ours";
    private const string BackupSuffix = ".pre-dxvk";

    /// <summary>O arquivo do conjunto D3D10 que carrega a marca. Um so basta: os cinco entram e
    /// saem juntos, e o d3d10core.dll e o unico dos cinco que nenhum outro mod costuma ocupar.</summary>
    private const string D3d10MarkFile = "d3d10core.dll";

    /// <summary>
    /// Direct3D 10: a terceira API que o DXVK traduz, e a UNICA rota que existe para ela.
    ///
    /// O Feeder nao fala D3D10 — o README dele diz "D3D10 is not supported", em uma linha. O
    /// dgVoodoo2 entra como D3D9.dll e traduz D3D9: um jogo que chama D3D10CreateDevice1 nunca
    /// passa por ele. Ate a 1.69 o launcher recusava esses jogos com uma mensagem propria, e foi
    /// o Just Cause 2 que ensinou isso, do jeito caro: instalacao inteira, coerente, e o jogo
    /// fechando ao criar o device.
    ///
    /// A versao NAO e a mais nova, e isso foi medido — tres experimentos no Just Cause 2, na
    /// mesma maquina, com a mesma cadeia (ReShade 6.8 na camada Vulkan, addon32 com transporte
    /// Vulkan, host64):
    ///
    ///   1. DXVK 3.1, com o que a release atual traz para D3D10: d3d10core + d3d11 + dxgi. SEM o
    ///      ReShade o jogo roda — o d3d10.dll do Windows resolve o d3d10core.dll na pasta do jogo
    ///      (d3d10core nao esta em KnownDLLs) e dali tudo e DXVK, conferido pela lista de modulos
    ///      do processo. COM o ReShade o jogo morre 3 s depois de abrir, com ou sem o addon do
    ///      Feeder. O ReShade.log diz por que: o jogo carrega o d3d10_1.dll e o d3d10.dll DO
    ///      SISTEMA (o DXVK 2.0+ nao traz os dois, so a camada por baixo), o ReShade instala os
    ///      "delayed hooks" neles e envolve o device D3D10 do DXVK num wrapper proprio — e o
    ///      processo cai logo depois, sem evento no Event Log. Na rota DX9 isso nunca acontece:
    ///      o d3d9.dll carregado e o LOCAL do DXVK, e o hook no d3d9.dll do sistema fica
    ///      "Delayed" para sempre.
    ///   2. d3d10.dll e d3d10_1.dll da 1.10.3 em cima do core da 3.1: o jogo sai limpo em 2 s
    ///      ("shut down cleanly" no log do Feeder). Wrapper antigo nao casa com core novo.
    ///   3. O conjunto INTEIRO da 1.10.3 — a ultima release com d3d10.dll e d3d10_1.dll
    ///      proprios: roda. O ReShade nunca ve um d3d10 do sistema, so existe o runtime Vulkan,
    ///      os dois shaders compilam, o Feeder acha os vetores, o host64 sobe e conecta, e o log
    ///      do host diz "signed DLSSNR 310.8.0 D3D12 runtime initialized" e "inline feature 18
    ///      evaluation succeeded" — a 160+ fps em 2560x1440 numa RTX 5090.
    ///
    /// Por isso a rota D3D10 baixa a 1.10.3 FIXA, em subpasta propria da biblioteca, e nunca se
    /// mistura com a release atual que a rota DX9 usa. Sao CINCO arquivos: os dois wrappers, o
    /// core, e o d3d11 e o dxgi que eles chamam. O dxgi.dll e tambem o nome de proxy do ReShade —
    /// por isso o ReShade NAO entra como proxy nesta rota; entra como camada Vulkan, exatamente
    /// como no caminho DX9 pelo DXVK.
    ///
    /// Dali em diante e o mesmo caminho ja testado: o jogo apresenta por Vulkan, o ReShade
    /// compila compute shader, e o addon32 com transporte Vulkan manda os frames ao host64.
    /// O d3d9.dll NAO vai junto: o Just Cause 2 importa d3d9.dll como fallback que nunca usa, e
    /// envolver esse caminho seria carregar um segundo DXVK a toa.
    /// </summary>
    public static readonly string[] D3d10Files =
        { "d3d10.dll", "d3d10_1.dll", "d3d10core.dll", "d3d11.dll", "dxgi.dll" };

    /// <summary>A ultima release do DXVK com d3d10.dll e d3d10_1.dll proprios (ver <see cref="D3d10Files"/>).
    /// Quando o DXVK voltar a trazer os dois wrappers, basta trocar estas duas constantes.</summary>
    public const string D3d10Version = "1.10.3";
    private const string D3d10Url = "https://github.com/doitsujin/dxvk/releases/download/v1.10.3/dxvk-1.10.3.tar.gz";

    public static string LibraryDir { get; } = Path.Combine(AppPaths.DataDir, "dxvk");
    private static string LibraryD3d9_32 { get; } = Path.Combine(LibraryDir, "x32", D3d9File);

    /// <summary>O conjunto de D3D10 mora em subpasta propria, porque e de OUTRA versao do DXVK e
    /// nao pode se misturar com o d3d9.dll da release atual. Calculada, e nao inicializada, para
    /// nao depender da ordem de declaracao dos estaticos.</summary>
    private static string LibraryD3d10Dir => Path.Combine(LibraryDir, "d3d10-" + D3d10Version);
    private static string LibraryD3d10(bool bits64, string file) =>
        Path.Combine(LibraryD3d10Dir, bits64 ? "x64" : "x32", file);

    /// <summary>Só github.com e seus domínios de download, como nas outras buscas do launcher.</summary>
    private static readonly string[] AllowedHosts =
        { "github.com", "api.github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com" };

    private static bool HostOk(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
        && u.Scheme == Uri.UriSchemeHttps
        && AllowedHosts.Contains(u.Host, StringComparer.OrdinalIgnoreCase);

    private static HttpClient NewClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RenoDXLauncher/1.0");
        return http;
    }

    /// <summary>O DXVK de 32 bits ja esta na biblioteca?</summary>
    public static bool InLibrary => File.Exists(LibraryD3d9_32);

    /// <summary>O conjunto de Direct3D 10 (os cinco arquivos da 1.10.3) esta na biblioteca, no
    /// bitness pedido? Separado do <see cref="InLibrary"/>: e outro download, de outra versao.</summary>
    public static bool D3d10InLibrary(bool bits64) =>
        D3d10Files.All(f => File.Exists(LibraryD3d10(bits64, f)));

    /// <summary>Este jogo renderiza em Direct3D 10 — e portanto so o DXVK o atende?</summary>
    public static bool AppliesD3d10(string? exePath) => FeederService.RenderizaEmD3d10(exePath);

    /// <summary>
    /// Jogos em que o DXVK foi testado e PERDEU para o dgVoodoo2.
    ///
    /// Os dois tradutores nao se ordenam: cada um cobre um conjunto, e os conjuntos nao se
    /// contem. Medido nesta maquina, com o mesmo add-on e o mesmo runtime:
    ///
    ///   Resident Evil Revelations 2  dgVoodoo crasha (0xc0000005 no d3d9.dll dele)
    ///                                DXVK roda, 1800 frames avaliados, 64 fps
    ///   Saints Row 2                 DXVK crasha (0xc0000005 no d3d9.dll dele) aos ~25 s,
    ///                                DEPOIS de o DLSS ja estar avaliando — o jogo sobe, o
    ///                                feed entrega 600 frames, e entao morre
    ///                                dgVoodoo roda estavel
    ///
    /// O padrao e o DXVK, porque cobre mais jogos e e mantido ativamente. Esta lista existe
    /// para os casos ja verificados em que ele perde — e so entra aqui o que foi testado
    /// dentro do jogo, nunca por suposicao.
    /// </summary>
    /// <remarks>
    /// Casado por PREFIXO, nao por nome exato. O Saints Row 2 tem dois executaveis — `sr2_pc.exe`
    /// e `sr2_pc_unpatched.exe` — e o localizador escolhe o segundo. Uma lista de nomes exatos
    /// deixava o jogo cair no DXVK apesar da excecao, e o `--check` foi quem mostrou isso.
    /// </remarks>
    private static readonly string[] PreferemDgVoodoo =
    {
        "sr2_pc",   // Saints Row 2 (sr2_pc.exe e sr2_pc_unpatched.exe)
    };

    /// <summary>O DXVK e a rota recomendada para este executavel?</summary>
    public static bool RecomendadoPara(string? exePath)
    {
        if (exePath is null) return true;
        var nome = Path.GetFileNameWithoutExtension(exePath);
        return !PreferemDgVoodoo.Any(p => nome.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Este jogo esta rodando pela rota DXVK? (o d3d9.dll dele e o do DXVK)</summary>
    public static bool IsDeployed(string targetDir)
    {
        var dll = Path.Combine(targetDir, D3d9File);
        if (!File.Exists(dll)) return false;
        // O d3d9.dll do DXVK passa de 5 MB; o do dgVoodoo fica em ~500 KB, e o do Windows nem
        // aparece na pasta do jogo. O tamanho separa os tres sem precisar abrir o PE.
        try { return new FileInfo(dll).Length > 3 * 1024 * 1024; }
        catch { return false; }
    }

    /// <summary>
    /// O d3d9.dll do DXVK nesta pasta foi este launcher que pos?
    ///
    /// Tres provas, qualquer uma basta. A marca `.renodx-ours` responde para tudo que o
    /// <see cref="Deploy"/> instalou depois de ela existir. Para instalacoes de antes da marca,
    /// o binario identico ao da biblioteca e a assinatura: a biblioteca so guarda o que o
    /// launcher baixou, e e dela que o Deploy copia. E um `.pre-dxvk` ao lado — do d3d9 ou do
    /// dgVoodoo que ele tirou do caminho — so o Deploy escreve, entao a sua existencia prova
    /// que o launcher passou por cima de algo aqui. Sem nenhuma das tres, o DXVK e de outra
    /// pessoa e nao e nosso para apagar.
    /// </summary>
    public static bool IsOurs(string targetDir)
    {
        var dll = Path.Combine(targetDir, D3d9File);
        if (File.Exists(dll + OursSuffix)) return true;
        if (Iguais(dll, LibraryD3d9_32)) return true;
        return new[] { D3d9File, "dgVoodoo.conf", "dgVoodooCpl.exe" }
            .Any(n => File.Exists(Path.Combine(targetDir, n + BackupSuffix)));
    }

    /// <summary>
    /// O conjunto de D3D10 nesta pasta foi este launcher que pos? Mesmas tres provas do
    /// <see cref="IsOurs"/>: a marca no d3d10core.dll, o d3d10.dll identico ao de uma das
    /// duas copias da biblioteca (x32 ou x64 — a pasta nao diz qual bitness o
    /// <see cref="DeployD3d10"/> escolheu), ou um `.pre-dxvk` em qualquer um dos cinco nomes.
    /// </summary>
    public static bool IsOursD3d10(string targetDir)
    {
        if (File.Exists(Path.Combine(targetDir, D3d10MarkFile + OursSuffix))) return true;
        var d3d10 = Path.Combine(targetDir, D3d10Files[0]);
        if (Iguais(d3d10, LibraryD3d10(false, D3d10Files[0]))
            || Iguais(d3d10, LibraryD3d10(true, D3d10Files[0]))) return true;
        return D3d10Files.Any(n => File.Exists(Path.Combine(targetDir, n + BackupSuffix)));
    }

    /// <summary>Baixa o DXVK para a biblioteca. Sem efeito se ja estiver la.</summary>
    public static async Task FetchAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (InLibrary) return;
        Directory.CreateDirectory(LibraryDir);

        progress?.Report(L.T("Dxvk_Fetching"));
        using var http = NewClient();

        // Sem a API: ela limita a 60 requisicoes por hora por IP, e quem instala em varios jogos
        // estoura isso e recebe 403 em tudo. A pagina publica de release nao tem cota.
        var url = await GitHubReleaseService.LatestAssetAsync(
            http, Repo, new System.Text.RegularExpressions.Regex(@"^dxvk-[0-9.]+\.tar\.gz$"), ct);
        if (url is null || !HostOk(url)) throw new InvalidOperationException(L.T("Dxvk_NoAsset"));

        // Guarda so o que interessa: o d3d9.dll de 32 bits (e o de 64, para jogo Vulkan x64).
        await BaixarEGuardarAsync(http, url, LibraryDir, [D3d9File], ct);

        if (!InLibrary) throw new InvalidOperationException(L.T("Dxvk_NoAsset"));
        Log.Info($"dxvk: baixado para a biblioteca ({new FileInfo(LibraryD3d9_32).Length:N0} bytes)");
    }

    /// <summary>
    /// Baixa o conjunto de Direct3D 10 para a biblioteca. Sem efeito se ja estiver la.
    ///
    /// Versao FIXA, e nao a mais nova — ver <see cref="D3d10Files"/>: a release atual do DXVK nao
    /// traz d3d10.dll nem d3d10_1.dll, e sem os dois o ReShade engancha os do sistema e o jogo
    /// morre. URL direta da release, sem passar pela API (mesmo motivo do <see cref="FetchAsync"/>).
    /// </summary>
    public static async Task FetchD3d10Async(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (D3d10InLibrary(false) && D3d10InLibrary(true)) return;
        if (!HostOk(D3d10Url)) throw new InvalidOperationException(L.T("Dxvk_NoAsset"));
        Directory.CreateDirectory(LibraryD3d10Dir);

        progress?.Report(L.T("Dxvk_FetchingD3d10", D3d10Version));
        using var http = NewClient();
        await BaixarEGuardarAsync(http, D3d10Url, LibraryD3d10Dir, D3d10Files, ct);

        if (!D3d10InLibrary(false)) throw new InvalidOperationException(L.T("Dxvk_NoAsset"));
        Log.Info($"dxvk: conjunto D3D10 {D3d10Version} na biblioteca "
                 + $"(x32={D3d10InLibrary(false)} x64={D3d10InLibrary(true)})");
    }

    /// <summary>Baixa um .tar.gz de release do DXVK e guarda so os arquivos pedidos, em x32\ e
    /// x64\ sob a pasta dada. O resto do pacote (d3d8.dll, o que nao foi pedido) fica de fora.</summary>
    private static async Task BaixarEGuardarAsync(HttpClient http, string url, string raiz,
                                                  string[] nomes, CancellationToken ct)
    {
        var tgz = Path.Combine(raiz, "dxvk.tar.gz");
        await using (var s = await http.GetStreamAsync(url, ct))
        await using (var f = File.Create(tgz))
            await s.CopyToAsync(f, ct);

        // tar nativo do Windows 10+ resolve .tar.gz sem dependencia externa.
        var tmp = Path.Combine(raiz, "unpack");
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        Directory.CreateDirectory(tmp);
        var psi = new System.Diagnostics.ProcessStartInfo("tar", $"-xzf \"{tgz}\" -C \"{tmp}\"")
        { UseShellExecute = false, CreateNoWindow = true };
        using (var p = System.Diagnostics.Process.Start(psi)) { if (p is not null) await p.WaitForExitAsync(ct); }

        foreach (var (arch, dest) in new[] { ("x32", Path.Combine(raiz, "x32")),
                                             ("x64", Path.Combine(raiz, "x64")) })
        {
            foreach (var nome in nomes)
            {
                var found = Directory.EnumerateFiles(tmp, nome, SearchOption.AllDirectories)
                                     .FirstOrDefault(p => p.Replace('/', '\\').Contains($"\\{arch}\\"));
                if (found is null) continue;
                Directory.CreateDirectory(dest);
                File.Copy(found, Path.Combine(dest, nome), overwrite: true);
            }
        }
        try { Directory.Delete(tmp, true); File.Delete(tgz); } catch { }
    }

    /// <summary>
    /// Poe o d3d9.dll do DXVK na pasta do jogo, guardando o que estiver la.
    ///
    /// O dgVoodoo sai de cena junto: os dois disputam o mesmo nome de arquivo, e deixar os
    /// dois na pasta e como nao ter nenhum.
    /// </summary>
    public static void Deploy(string targetDir, IProgress<string>? progress = null)
    {
        if (!InLibrary) throw new InvalidOperationException(L.T("Dxvk_NotInLibrary"));

        // dgVoodoo fora: mesmo nome, e quem chega por ultimo venceria por acidente.
        foreach (var n in new[] { "D3D9.dll", "d3d9.dll" })
        {
            var p = Path.Combine(targetDir, n);
            if (File.Exists(p) && !IsDeployed(targetDir))
            {
                // Renomear tambem e mexer: para quem carrega DLL pelo nome, sair do nome e o
                // mesmo que sumir. O original vai para o registro antes de perder o nome.
                BackupService.AntesDeEscrever(targetDir, p, "dxvk");
                var bak = p + ".pre-dxvk";
                if (!File.Exists(bak)) File.Move(p, bak);
                else File.Delete(p);
                BackupService.Anotar(targetDir, "dxvk", "tirou do nome", Path.GetFileName(p));
                progress?.Report(L.T("Dxvk_ReplacedD3d9"));
                break;
            }
        }
        foreach (var n in new[] { "dgVoodoo.conf", "dgVoodooCpl.exe" })
        {
            var p = Path.Combine(targetDir, n);
            if (File.Exists(p))
            {
                BackupService.AntesDeEscrever(targetDir, p, "dxvk");
                try { File.Move(p, p + ".pre-dxvk", overwrite: true); } catch { }
            }
        }

        BackupService.Copiar(targetDir, LibraryD3d9_32, Path.Combine(targetDir, D3d9File), "dxvk");
        // A marca e o que autoriza o Remove a apagar este arquivo depois — ver IsOurs.
        Marcar(Path.Combine(targetDir, D3d9File));
        progress?.Report(L.T("Dxvk_Deployed"));
        Log.Info($"dxvk: d3d9.dll implantado em {targetDir}");
    }

    /// <summary>Tira o DXVK e devolve o que estava no lugar.</summary>
    public static void Remove(string targetDir)
    {
        var dll = Path.Combine(targetDir, D3d9File);
        if (IsDeployed(targetDir)) { try { File.Delete(dll); } catch { } }
        // O dxgi.dll entra na lista porque o instalador o tira do caminho ao ir para o DXVK: o
        // proxy do ReShade da rota D3D11 seria carregado pelo DXGI que o DXVK usa por dentro, e
        // o ReShade entraria duas vezes no processo. Ele vai para dxgi.dll.pre-dxvk — e so aqui
        // tem de onde voltar, senao a desinstalacao deixava o proxy guardado e a pasta sem o
        // ReShade que o usuario tinha antes.
        foreach (var n in new[] { "D3D9.dll", "d3d9.dll", "dgVoodoo.conf", "dgVoodooCpl.exe", "dxgi.dll" })
        {
            var bak = Path.Combine(targetDir, n + BackupSuffix);
            if (File.Exists(bak)) { try { File.Move(bak, Path.Combine(targetDir, n), overwrite: true); } catch { } }
        }
        // A marca sai com o arquivo: sobrando, um DXVK que o usuario ponha depois sob o mesmo
        // nome seria tratado como nosso na proxima desinstalacao.
        Desmarcar(Path.Combine(targetDir, D3d9File));
    }

    // ---------------------------------------------------------------- Direct3D 10

    /// <summary>
    /// A rota D3D10 esta montada nesta pasta? Os CINCO arquivos, e o d3d10.dll sendo mesmo o do
    /// DXVK — pelo ProductName do PE, que o DXVK preenche ("DXVK / Direct3D 10 Runtime"). O
    /// tamanho nao serve aqui como serve no d3d9: um dxgi.dll de alguns MB tanto pode ser o do
    /// DXVK quanto o proxy do ReShade. Exigir os cinco e o que separa a instalacao que roda de
    /// um resto da 3.1 (so d3d10core + d3d11 + dxgi), que o ReShade derruba.
    /// </summary>
    public static bool IsDeployedD3d10(string targetDir) =>
        D3d10Files.All(f => File.Exists(Path.Combine(targetDir, f)))
        && EhDxvk(Path.Combine(targetDir, D3d10Files[0]));

    /// <summary>
    /// Poe o conjunto de D3D10 na pasta do jogo, guardando o que ja ocupava esses nomes.
    ///
    /// Guardar, e nao apagar, porque um d3d11.dll ou dxgi.dll ao lado do exe pode ser o proxy do
    /// ReShade de uma instalacao anterior, um wrapper de outro mod ou uma DLL do sistema que o
    /// jogo redistribui. Nenhum deles e nosso para apagar; todos voltam em <see cref="RemoveD3d10"/>.
    /// O que ja e DXVK fica — e so sobrescrito pelo build da biblioteca (e assim que um resto da
    /// 3.1 vira a 1.10.3, sem backup, porque os dois sao nossos).
    ///
    /// Backups: o PRIMEIRO ocupante de cada nome vai para `.pre-dxvk`, e e esse que
    /// <see cref="RemoveD3d10"/> devolve. Qualquer ocupante que apareca DEPOIS (entre uma
    /// instalacao e outra) vai para `.pre-dxvk.2`, `.pre-dxvk.3`, ... — o primeiro numero
    /// livre, nunca por cima de um que ja exista. Esses numerados NAO sao devolvidos
    /// automaticamente: nao ha como saber qual o usuario queria de volta, entao ficam na pasta
    /// para resgate a mao, e o RemoveD3d10 avisa no log que estao la.
    /// </summary>
    public static void DeployD3d10(string targetDir, bool jogo64Bits, IProgress<string>? progress = null)
    {
        if (!D3d10InLibrary(jogo64Bits)) throw new InvalidOperationException(L.T("Dxvk_NotInLibrary"));

        var guardou = false;
        foreach (var n in D3d10Files)
        {
            var p = Path.Combine(targetDir, n);
            if (!File.Exists(p) || EhDxvk(p)) continue;
            // O PRIMEIRO backup e o que fica, como no Deploy() do d3d9: ele e a unica copia do
            // que havia antes do launcher, e e ele que RemoveD3d10 devolve. Apagar o backup para
            // guardar o recem-chegado (um proxy do ReShade que o usuario pos entre uma
            // instalacao e outra, um wrapper de outro mod) trocava o original pelo intruso — e
            // o original nao tem outra copia. O intruso tambem nao e nosso para apagar: vai
            // para um nome numerado ao lado, de onde da para resgata-lo a mao. E numerado sem
            // reaproveitar numero: um segundo intruso sobrescrevendo o `.2` do primeiro era a
            // mesma perda sem copia, so uma casa adiante.
            BackupService.AntesDeEscrever(targetDir, p, "dxvk-d3d10");
            var bak = p + BackupSuffix;
            if (!File.Exists(bak)) File.Move(p, bak);
            else File.Move(p, ProximoBackupLivre(bak));
            BackupService.Anotar(targetDir, "dxvk-d3d10", "tirou do nome", Path.GetFileName(p));
            guardou = true;
        }
        if (guardou) progress?.Report(L.T("Dxvk_ReplacedD3d10"));

        foreach (var n in D3d10Files)
            BackupService.Copiar(targetDir, LibraryD3d10(jogo64Bits, n),
                                 Path.Combine(targetDir, n), "dxvk-d3d10");

        // A marca e o que autoriza o RemoveD3d10 a ser chamado depois — ver IsOursD3d10.
        Marcar(Path.Combine(targetDir, D3d10MarkFile));
        progress?.Report(L.T("Dxvk_DeployedD3d10"));
        Log.Info($"dxvk: conjunto D3D10 ({(jogo64Bits ? "x64" : "x32")}) implantado em {targetDir}");
    }

    /// <summary>`nome.pre-dxvk.N`, com N inteiro — o que <see cref="ProximoBackupLivre"/> gera.</summary>
    private static bool EhBackupNumerado(string nomeArquivo, string prefixo)
    {
        if (!nomeArquivo.StartsWith(prefixo + ".", StringComparison.OrdinalIgnoreCase)) return false;
        return int.TryParse(nomeArquivo.AsSpan(prefixo.Length + 1), out var n) && n >= 2;
    }

    /// <summary>O primeiro de `.pre-dxvk.2`, `.pre-dxvk.3`, ... que ainda nao existe.</summary>
    private static string ProximoBackupLivre(string bak)
    {
        for (var i = 2; ; i++)
        {
            var candidato = $"{bak}.{i}";
            if (!File.Exists(candidato)) return candidato;
        }
    }

    /// <summary>
    /// Tira o conjunto de D3D10 (so o que for DXVK) e devolve o que estava nos nomes.
    ///
    /// So o `.pre-dxvk` original volta. Os numerados (`.pre-dxvk.2`, `.3`, ...) que o
    /// <see cref="DeployD3d10"/> tenha guardado ficam onde estao: sao ocupantes que chegaram
    /// entre uma instalacao e outra, e escolher um deles para por no lugar seria adivinhar. O
    /// log diz quais sobraram, para o usuario resgatar a mao.
    /// </summary>
    public static void RemoveD3d10(string targetDir)
    {
        var numerados = new List<string>();
        foreach (var n in D3d10Files)
        {
            var p = Path.Combine(targetDir, n);
            try { if (File.Exists(p) && EhDxvk(p)) File.Delete(p); }
            catch (Exception ex) { Log.Warn($"dxvk d3d10 remove {n}: {ex.Message}"); }

            var bak = p + BackupSuffix;
            // Pelo nome e nao pelo padrao do sistema de arquivos: o "x.*" do Windows tambem
            // casa o proprio "x", e aqui so interessam os numerados (".2", ".3", ...).
            try
            {
                numerados.AddRange(Directory.EnumerateFiles(targetDir, n + BackupSuffix + "*")
                                            .Select(f => Path.GetFileName(f))
                                            .Where(f => EhBackupNumerado(f, n + BackupSuffix)));
            }
            catch { }
            if (!File.Exists(bak)) continue;
            try { File.Move(bak, p, overwrite: true); }
            catch (Exception ex) { Log.Warn($"dxvk d3d10 restaurar {n}: {ex.Message}"); }
        }
        Desmarcar(Path.Combine(targetDir, D3d10MarkFile));
        if (numerados.Count > 0)
            Log.Info($"dxvk d3d10 remove: backups numerados ficaram em {targetDir} para resgate a mao "
                     + $"(so o .pre-dxvk original e devolvido): {string.Join(", ", numerados)}");
    }

    private static void Marcar(string caminho)
    {
        try
        {
            BackupService.Escrever(Path.GetDirectoryName(caminho), caminho + OursSuffix,
                                   DateTime.UtcNow.ToString("o"), "marca");
        }
        catch (Exception ex) { Log.Warn($"dxvk mark {Path.GetFileName(caminho)}: {ex.Message}"); }
    }

    private static void Desmarcar(string caminho)
    {
        var marca = caminho + OursSuffix;
        try { if (File.Exists(marca)) File.Delete(marca); }
        catch (Exception ex) { Log.Warn($"dxvk mark clear {Path.GetFileName(caminho)}: {ex.Message}"); }
    }

    /// <summary>Os dois arquivos existem e sao iguais byte a byte? Em blocos, porque o d3d9.dll
    /// do DXVK passa de 5 MB e nao vale carregar os dois inteiros so para comparar.</summary>
    private static bool Iguais(string a, string b)
    {
        try
        {
            if (!File.Exists(a) || !File.Exists(b)) return false;
            if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
            using var fa = File.OpenRead(a);
            using var fb = File.OpenRead(b);
            var ba = new byte[1 << 16];
            var bb = new byte[1 << 16];
            int na;
            while ((na = fa.Read(ba, 0, ba.Length)) > 0)
            {
                var nb = fb.ReadAtLeast(bb.AsSpan(0, na), na, throwOnEndOfStream: false);
                if (nb != na || !ba.AsSpan(0, na).SequenceEqual(bb.AsSpan(0, nb))) return false;
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>Este arquivo e um build do DXVK? Pelo ProductName do recurso de versao, que o
    /// DXVK preenche em todas as DLLs dele — o mesmo campo que o scanner de conflitos le.</summary>
    private static bool EhDxvk(string path)
    {
        try
        {
            var v = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            return v.ProductName?.Contains("DXVK", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch { return false; }
    }
}
