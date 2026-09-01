using System.IO;
using System.IO.Compression;
using System.Net.Http;
using RenoDXLauncher.Localization;

namespace RenoDXLauncher.Services;

/// <summary>
/// O DLSS5 Feeder: neural rendering em jogo DirectX 11 que NAO tem DLSS nenhum.
///
/// A ponte e o Feeder resolvem problemas diferentes e nao convivem — o proprio autor do Feeder
/// diz para nao rodar os dois. A ponte serve ao jogo DX11 que JA TEM DLSS: ela so leva o pass de
/// D3D12 ate ele. O Feeder serve ao jogo que nao tem DLSS nenhum, e por isso precisa FABRICAR o
/// que o DLSS exige: um shader do ReShade produz motion vectors e profundidade, o addon abre um
/// device D3D12 proprio, compartilha as texturas por handle NT com fence, e roda em DLAA.
///
/// O que ele nao faz: performance. E DLAA, resolucao de render igual a de saida. Quem instalar
/// esperando FPS vai concluir que nao funcionou — por isso o launcher diz isso antes.
///
/// Sobre o iMMERSE: a licenca dele proibe propagacao publica, entao nada aqui e empacotado ou
/// re-hospedado. O download vem do repositorio do proprio autor, que e exatamente o passo manual
/// que o guia do Feeder manda fazer.
/// </summary>
public static class FeederService
{
    public const string AddonFile = "dlss5-feed.addon64";
    public const string FxFile = "DLSS5_Feed.fx";

    /// <summary>
    /// As pecas do caminho de 32 bits.
    ///
    /// NGX e o addon neural sao 64-bit-only, entao num jogo de 32 bits o Feeder se parte em dois:
    /// um addon pequeno de 32 bits vive dentro do jogo e manda os frames para um processo
    /// auxiliar de 64 bits, que abre o proprio device D3D12 e roda o mesmo DLAA que o addon de
    /// 64 bits roda in-process. Nenhum frame passa pela memoria do sistema — tudo fica na GPU,
    /// por recurso compartilhado entre processos.
    ///
    /// O host precisa da propria copia de tudo o que e 64 bits: ReShade, o addon neural e os dois
    /// runtimes. E uma instalacao inteira dentro de host64\.
    /// </summary>
    public const string Addon32File = "dlss5-feed.addon32";
    public const string Host64Exe = "dlss5-feed-host64.exe";
    public const string Host64Dir = "host64";
    private const string Addon32Url = "https://github.com/jlrouzies-fr/DLSS5-Feeder/releases/latest/download/dlss5-feed.addon32";
    private const string Host64Url = "https://github.com/jlrouzies-fr/DLSS5-Feeder/releases/latest/download/dlss5-feed-host64.exe";

    // Propriedades calculadas, e nao inicializadas: campo estatico e inicializado na ordem de
    // declaracao, e estes vem antes de LibraryDir no arquivo. Como inicializados, recebiam
    // Path.Combine(null, ...) e a classe inteira falhava ao carregar.
    private static string LibraryAddon32 => Path.Combine(LibraryDir, Addon32File);
    private static string LibraryHost64 => Path.Combine(LibraryDir, Host64Exe);

    /// <summary>A biblioteca tem o que o caminho de 32 bits precisa.</summary>
    public static bool Bits32InLibrary => File.Exists(LibraryAddon32) && File.Exists(LibraryHost64);

    /// <summary>
    /// O provedor de motion vectors: LumeniteFX Kernel.
    ///
    /// Quarta escolha em um dia, e cada troca teve um motivo que so o log mostrou:
    ///
    ///   iMMERSE LaunchPad — o guia da v0.1.0 indicava, mas ele publica em texturas proprias e
    ///     nunca declarou `texMotionVectors`, que era de onde o Feed 0.5.x lia. Os dois nunca se
    ///     encontravam, e o log dizia "no known texMotionVectors provider found".
    ///
    ///   DRME — declara a textura certa e resolveu aquilo. Mas as notas da 0.6.0 dizem que ele
    ///     NAO COMPILA no ReShade 6.8: a tecnica aparece ligada, o efeito lista como carregado, e
    ///     nao sai vetor nenhum. O erro estava no ReShade.log o tempo todo, engolido por um
    ///     "Successfully compiled ... with warnings".
    ///
    ///   VORT — funcionava, mas e o provedor 2. As notas da 0.6.0-beta.1 dizem, sobre o provedor
    ///     3: "this is the configuration the beta was tuned on, and the one to use". Rodavamos a
    ///     beta com um provedor que ela nao foi ajustada em cima, e os limiares de validacao que
    ///     ela introduziu (teste de luma, profundidade 0.10, consistencia 1.4 px) foram
    ///     calibrados contra a saida do Kernel.
    ///
    /// Na 0.6.0 o contrato tambem mudou: o provedor e escolhido em tempo de COMPILACAO, pelo
    /// define DLSS5_MV_PROVIDER, e nao mais por uma textura de nome combinado. Sem o define o
    /// shader compila com provedor 0 — nenhum — e o pass roda cego.
    /// </summary>
    private const int MvProviderId = 3; // 3 = LumeniteFX Kernel
    private const string MvTechnique = "Lumenite_Kernel@lumenite_Kernel.fx";
    private const string MvUrl = "https://raw.githubusercontent.com/umar-afzaal/LumeniteFX/mainline/Shaders/";
    private const string MvTexUrl = "https://raw.githubusercontent.com/umar-afzaal/LumeniteFX/mainline/Textures/";

    /// <summary>
    /// As texturas do provedor, que NAO sao opcionais.
    ///
    /// Sem a blue noise o ReShade recusa o efeito com "Source '...' was not found in any of the
    /// texture search paths", e o provedor entra na lista sem produzir nada. Copiar so os .fx e
    /// .fxh parecia bastar e nao bastava.
    /// </summary>
    private static readonly string[] MvTextures = ["lumenite_bluenoise256.png"];

    /// <summary>O .fx e os includes que ele puxa, copiados inteiros — resolver a arvore de
    /// #include na mao quebra a cada versao do provedor.</summary>
    private static readonly string[] MvFiles =
    [
        "lumenite_Kernel.fx",
        "include/lumenite_Compute.fxh", "include/lumenite_Helpers.fxh",
        "include/lumenite_Projections.fxh", "include/lumenite_ColorManagement.fxh",
    ];

    // latest/download em vez de uma tag fixa: o Feeder acabou de nascer (v0.1.0) e vai mudar
    // rapido. Fixar versao aqui congelaria o launcher numa build antiga do dia da integracao.
    private const string AddonUrl = "https://github.com/jlrouzies-fr/DLSS5-Feeder/releases/latest/download/dlss5-feed.addon64";
    private const string FxUrl = "https://github.com/jlrouzies-fr/DLSS5-Feeder/releases/latest/download/DLSS5_Feed.fx";

    // O instalador do ReShade poe o dxgi.dll e nada mais: a pasta Shaders fica vazia. O
    // DLSS5_Feed.fx abre com #include "ReShade.fxh" e falha a compilar sem ele — e o sintoma nao
    // aparece na instalacao, so no ReShade.log depois de abrir o jogo:
    //   preprocessor error: could not open included file 'ReShade.fxh'
    // DrawText.fxh entrou junto com o LumeniteFX: o lumenite_Kernel.fx o inclui para desenhar o
    // proprio HUD de depuracao, e sem ele o provedor inteiro falha a compilar — mesmo sintoma
    // silencioso do ReShade.fxh ausente, visivel so no ReShade.log depois de abrir o jogo.
    private static readonly string[] BaseIncludes = ["ReShade.fxh", "ReShadeUI.fxh", "DrawText.fxh"];
    private const string BaseIncludeUrl = "https://raw.githubusercontent.com/crosire/reshade-shaders/slim/Shaders/";

    public static string LibraryDir { get; } = Path.Combine(AppPaths.DataDir, "feeder");
    public static string LibraryAddon { get; } = Path.Combine(LibraryDir, AddonFile);
    public static string LibraryFx { get; } = Path.Combine(LibraryDir, FxFile);

    /// <summary>A biblioteca tem tudo o que o deploy precisa.</summary>
    public static bool InLibrary =>
        File.Exists(LibraryAddon) && File.Exists(LibraryFx)
        && MvFiles.Distinct().All(n => File.Exists(Path.Combine(LibraryDir, n.Replace('/', Path.DirectorySeparatorChar))))
        && MvTextures.All(n => File.Exists(Path.Combine(LibraryDir, "Textures", n)))
        && BaseIncludes.All(n => File.Exists(Path.Combine(LibraryDir, n)));

    /// <summary>
    /// O Feeder esta na pasta, integro, e com o que ele precisa para carregar?
    ///
    /// Nao e um arquivo: sao o addon, o shader do Feed, os includes base do ReShade e o provedor
    /// de motion vectors com seus tres includes. Faltando qualquer um o pass nao entrega — e
    /// faltando so o provedor ele entrega vetores zerados, que e pior, porque nada acusa erro.
    /// </summary>
    public static bool IsDeployed(string targetDir)
    {
        // No caminho de 32 bits quem fica no jogo e o addon32 — o de 64 bits foi removido de
        // proposito, porque o processo nao o carregaria. Exigir so o addon64 reportava "Feeder
        // ausente" numa instalacao completa e funcionando.
        var addon = Path.Combine(targetDir, AddonFile);
        var addon32 = Path.Combine(targetDir, Addon32File);
        if (File.Exists(addon32) && !File.Exists(addon)) addon = addon32;
        if (!File.Exists(addon)) return false;

        var shaders = Path.Combine(targetDir, "reshade-shaders", "Shaders");
        if (!File.Exists(Path.Combine(shaders, FxFile))) return false;
        if (!MvFiles.Distinct().All(n => File.Exists(Path.Combine(shaders, n.Replace('/', Path.DirectorySeparatorChar))))) return false;
        if (!BaseIncludes.All(n => File.Exists(Path.Combine(shaders, n)))) return false;
        var texturas = Path.Combine(targetDir, "reshade-shaders", "Textures");
        if (!MvTextures.All(n => File.Exists(Path.Combine(texturas, n)))) return false;

        // Integridade contra a copia certa da biblioteca: o addon de 32 bits tem outro tamanho,
        // e compara-lo com o de 64 daria "corrompido" em toda instalacao de 32 bits.
        try
        {
            var referencia = addon.EndsWith(Addon32File, StringComparison.OrdinalIgnoreCase)
                ? LibraryAddon32 : LibraryAddon;
            if (File.Exists(referencia)
                && new FileInfo(addon).Length != new FileInfo(referencia).Length) return false;
        }
        catch { /* sem leitura, aceita o que esta la */ }

        return true;
    }

    /// <summary>
    /// O Feeder e a resposta certa para este jogo?
    ///
    /// Duas condicoes: nao ha DLSS nativo (com DLSS, a ponte faz o mesmo sem shader no meio) e o
    /// executavel e 64 bits.
    ///
    /// D3D12 NAO desqualifica, ao contrario do que o README da v0.1.0 dizia ("64-bit DirectX 11
    /// game only"). O binario da 0.5.0 se descreve como "D3D11 and D3D12 games without DLSS —
    /// a private D3D12 device for D3D11 games, the game's own device for D3D12". Manter a regra
    /// antiga barraria jogos que o addon atende hoje.
    /// </summary>
    public static bool Applies(string? exePath, bool jogoTemDlss, bool alcancaD3d12)
    {
        _ = alcancaD3d12; // ver acima: deixou de ser criterio na 0.5.0
        // 32 bits tambem deixou de desqualificar: a 0.6.0 traz addon32 e um host auxiliar.
        if (jogoTemDlss || exePath is null) return false;
        if (PeUtils.Inspect(exePath, readImports: false) is null) return false;
        return !EhD3d10(exePath);
    }

    /// <summary>
    /// O jogo renderiza em Direct3D 10?
    ///
    /// O README do Feeder e de uma linha so: "D3D10 is not supported". O transporte compartilha
    /// texturas por handle NT com um device D3D12, e as pontas que ele sabe abrir sao D3D11,
    /// D3D12 e Vulkan — D3D10 nao esta entre elas.
    ///
    /// O dgVoodoo tambem nao salva este caso: ele entra como D3D9.dll e traduz D3D9 para D3D11.
    /// Um jogo que chama D3D10CreateDevice1 direto nunca passa por ele.
    ///
    /// Custou o Just Cause 2: instalacao inteira, coerente, e o jogo fechando ao criar o device.
    /// </summary>
    /// <summary>
    /// Publico porque a razao da recusa importa para quem le a mensagem. Um jogo D3D10 nao esta
    /// "faltando um arquivo": nenhuma das tres camadas cobre essa API, e nao ha o que baixar.
    /// </summary>
    public static bool RenderizaEmD3d10(string? exePath)
        => exePath is not null && File.Exists(exePath) && EhD3d10(exePath);

    private static bool EhD3d10(string exePath)
    {
        var pe = PeUtils.Inspect(exePath);
        if (pe is null) return false;
        // A D3DX9 decide antes de tudo. Ela e a biblioteca auxiliar do Direct3D 9 e de mais nada:
        // quem a linka renderiza em D3D9, e o dgVoodoo cuida do resto. Sem esta saida o `Bully.exe`
        // era lido como D3D10 — ele carrega a string "d3d10.dll" e nao menciona nenhuma API mais
        // nova, que e exatamente o padrao que a heuristica abaixo usa para acusar D3D10 — e o
        // launcher recusava um jogo que a comunidade ja demonstrou funcionando por esta rota.
        if (pe.Imports.Any(i => i.StartsWith("d3dx9_", StringComparison.OrdinalIgnoreCase))) return false;

        var d10 = pe.Imports.Any(i => i.StartsWith("d3d10", StringComparison.OrdinalIgnoreCase));
        var d11ou12 = pe.Imports.Any(i => i.StartsWith("d3d11", StringComparison.OrdinalIgnoreCase)
                                          || i.StartsWith("d3d12", StringComparison.OrdinalIgnoreCase));
        if (d10 && !d11ou12) return true;

        // Carregado por LoadLibrary nao aparece na tabela: o Just Cause 2 chama d3d10_1.dll assim.
        // So decide quando D3D10 esta no binario e nenhuma API mais nova esta.
        var mencionaD10 = ContemTexto(exePath, "d3d10.dll") || ContemTexto(exePath, "d3d10_1.dll");
        if (!mencionaD10) return false;
        return !ContemTexto(exePath, "d3d11.dll") && !ContemTexto(exePath, "d3d12.dll");
    }

    private static bool ContemTexto(string path, string alvo)
    {
        try
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(alvo);
            using var fs = File.OpenRead(path);
            var buf = new byte[1 << 20];
            var carry = bytes.Length - 1;
            var anterior = new byte[carry];
            var temAnterior = false;
            int n;
            while ((n = fs.Read(buf, 0, buf.Length)) > 0)
            {
                var janela = temAnterior ? anterior.Concat(buf.Take(n)).ToArray() : buf.Take(n).ToArray();
                if (System.Text.Encoding.ASCII.GetString(janela)
                        .Contains(alvo, StringComparison.OrdinalIgnoreCase)) return true;
                if (n >= carry) { Array.Copy(buf, n - carry, anterior, 0, carry); temAnterior = true; }
            }
        }
        catch (Exception ex) { Log.Warn($"feeder api probe {path}: {ex.Message}"); }
        return false;
    }

    /// <summary>Este jogo precisa do caminho partido (addon de 32 bits + host de 64)?</summary>
    public static bool NeedsHost64(string? exePath) =>
        exePath is not null && PeUtils.Inspect(exePath, readImports: false)?.Is64Bit == false;

    /// <summary>Baixa as pecas de 32 bits. Separado do FetchAsync porque a maioria dos jogos
    /// nunca precisa delas, e sao mais dois downloads.</summary>
    public static async Task FetchBits32Async(IProgress<string>? progress = null,
                                              CancellationToken ct = default)
    {
        if (Bits32InLibrary) return;
        Directory.CreateDirectory(LibraryDir);
        progress?.Report(L.T("Feeder_Fetching32"));
        using var http = NewClient();
        if (!File.Exists(LibraryAddon32)) await BaixarAsync(http, Addon32Url, LibraryAddon32, ct);
        if (!File.Exists(LibraryHost64)) await BaixarAsync(http, Host64Url, LibraryHost64, ct);
        if (!EhPe(LibraryAddon32) || !EhPe(LibraryHost64))
        {
            TryDelete(LibraryAddon32);
            TryDelete(LibraryHost64);
            throw new InvalidOperationException(L.T("Feeder_BadDownload"));
        }
    }

    /// <summary>
    /// Monta o caminho de 32 bits: o addon pequeno na pasta do jogo e a instalacao de 64 bits
    /// inteira em host64\.
    ///
    /// O host e um processo separado e de verdade 64 bits, entao ele precisa de tudo o que um
    /// jogo 64-bit precisaria: ReShade proprio, o addon neural e os dois runtimes. Faltando
    /// qualquer um, ele sobe e nao roda o pass — sem erro no lado do jogo, porque o jogo nem
    /// sabe que existe um host.
    /// </summary>
    public static async Task DeployBits32Async(string targetDir, ReShadeService reshade,
                                               IProgress<string>? progress = null,
                                               CancellationToken ct = default)
    {
        if (!Bits32InLibrary) throw new InvalidOperationException(L.T("Feeder_NotInLibrary"));

        // No jogo: o addon de 32 bits, e NAO o de 64 — o processo é 32 bits e o ReShade dali só
        // carrega .addon32.
        File.Copy(LibraryAddon32, Path.Combine(targetDir, Addon32File), overwrite: true);
        var addon64Solto = Path.Combine(targetDir, AddonFile);
        if (File.Exists(addon64Solto)) File.Delete(addon64Solto);

        var host = Path.Combine(targetDir, Host64Dir);
        Directory.CreateDirectory(host);
        File.Copy(LibraryHost64, Path.Combine(host, Host64Exe), overwrite: true);

        var r = await reshade.Deploy64BitAsync(host, "dxgi.dll", progress);
        if (!r.Success) throw new InvalidOperationException(r.Message);

        // O host roda o pass neural, entao o addon e os runtimes vao para LA, nao para o jogo.
        NeuralUpliftService.DeployForHost64(host, progress);

        // E saem da pasta do jogo. O processo do jogo tem 32 bits e nao carrega nenhum deles —
        // ficariam ali como 271 MB de peso morto por jogo, e o launcher acabou de os copiar
        // porque o caminho comum roda antes de sabermos que este jogo e partido em dois.
        NeuralUpliftService.RemoveRuntimesFrom(targetDir, Path.Combine(targetDir, "ReShade.ini"));

        // host_window=0 esconde a janela do auxiliar. Fica visivel na primeira vez de proposito:
        // e por ela que se ve o host subir, e um processo invisivel que falha em silencio e
        // exatamente o que nao queremos aqui.
        progress?.Report(L.T("Feeder_Host64Deployed"));
        Log.Info($"feeder: caminho de 32 bits montado em {targetDir} (host em {host})");
    }

    /// <summary>
    /// Tira a reconstrucao de aquecimento do Feeder, sem tocar no atraso de criacao.
    ///
    /// O padrao dele e `create_delay=60`: ele deixa passar 60 frames antes de criar as texturas
    /// compartilhadas. Em motor que reserva um pool de memoria proprio na largada — id Tech 7 e o
    /// caso — esses 60 frames sao suficientes para o jogo fechar o pool inteiro, e a alocacao do
    /// Feeder chega depois da porta fechada. O sintoma e uma mensagem do JOGO, nao nossa:
    ///   "Failed to allocate video memory. Total allocated: 5887 MiB"
    /// com o mesmo valor toda vez, e dezenas de GB livres na placa — porque o teto nao e a VRAM,
    /// e o pool que o motor ja fechou.
    ///
    /// `warmup_rebuild` cai pelo mesmo motivo: ele refaz a feature la pelo frame 180, que e uma
    /// segunda alocacao no pior momento possivel.
    ///
    /// Sem efeito colateral em jogo que nao reserva pool: alocar cedo e so alocar cedo.
    /// </summary>
    public static void AjustarAlocacao(string targetDir, IProgress<string>? progress = null)
    {
        try
        {
            var cfg = Path.Combine(targetDir, CfgFile);
            if (!File.Exists(cfg)) return;   // o addon escreve na primeira execucao; nada a fazer

            var linhas = File.ReadAllLines(cfg);
            var mudou = false;
            for (int i = 0; i < linhas.Length; i++)
            {
                var chave = linhas[i].Split('=')[0].Trim();
                var novo = chave switch
                {
                    // warmup_rebuild sai, e so ele.
                    //
                    // Ele refaz a feature la pelo frame 180 para contornar o painel travar em
                    // STANDBY — um problema que as builds "v45+" do addon nao tem mais, e que o
                    // proprio Feeder pula automaticamente nelas. O que sobra e uma realocacao de
                    // cinco texturas do tamanho da tela num momento arbitrario, que em motor com
                    // pool proprio de memoria cai no pior instante possivel.
                    "warmup_rebuild" => "warmup_rebuild=0",

                    // create_delay NAO e mexido, e isto ja foi aprendido do jeito caro.
                    //
                    // A tentacao e zera-lo: assim o Feeder aloca no primeiro quadro, antes de o
                    // motor fechar o pool, e o "Failed to allocate video memory" do DOOM Eternal
                    // some. Mas o README e explicito sobre o que esse atraso protege: "the DLSS 5
                    // add-on arms its NGX hooks asynchronously, and calling in too early CAN
                    // CRASH". Carregar um save e um re-init de runtime — o addon rearma os hooks
                    // e o Feeder reconstroi a feature em cima disso. Zerado, o Final Fantasy XV
                    // fechava sempre ao terminar de carregar, sem excecao no Event Log e sem
                    // breadcrumb, porque nao e uma falha do Feeder: e uma chamada valida cedo
                    // demais.
                    //
                    // Trocar um crash garantido por um problema de memoria que so aparece em
                    // motor que reserva pool inteiro na largada e um mau negocio.
                    _ => null,
                };
                if (novo is not null && linhas[i] != novo) { linhas[i] = novo; mudou = true; }
            }
            if (!mudou) return;

            File.WriteAllLines(cfg, linhas);
            Log.Info($"feeder: alocacao antecipada em {targetDir} (create_delay=0)");
            progress?.Report(L.T("Feeder_EarlyAlloc"));
        }
        catch (Exception ex) { Log.Warn($"feeder cfg {targetDir}: {ex.Message}"); }
    }

    private const string CfgFile = "dlss5-feed.cfg";

    private static HttpClient NewClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RenoDXLauncher/1.0");
        return http;
    }

    /// <summary>Baixa o addon, o shader do Feed, os includes base e o provedor de motion vectors.</summary>
    public static async Task FetchAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (InLibrary) return;
        Directory.CreateDirectory(LibraryDir);
        using var http = NewClient();

        if (!File.Exists(LibraryAddon) || !File.Exists(LibraryFx))
        {
            progress?.Report(L.T("Feeder_Fetching"));
            await BaixarAsync(http, AddonUrl, LibraryAddon, ct);
            await BaixarAsync(http, FxUrl, LibraryFx, ct);
            // O addon e um PE; o .fx e texto. Um servidor que devolve pagina de erro com 200
            // passaria pelos dois sem isto.
            if (new FileInfo(LibraryAddon).Length < 4096 || !EhPe(LibraryAddon))
            {
                File.Delete(LibraryAddon);
                throw new InvalidOperationException(L.T("Feeder_BadDownload"));
            }
        }

        foreach (var nome in BaseIncludes)
        {
            var destino = Path.Combine(LibraryDir, nome);
            if (File.Exists(destino)) continue;
            await BaixarAsync(http, BaseIncludeUrl + nome, destino, ct);
        }

        // O provedor de motion vectors, do repositorio do autor. MIT, entao redistribuir seria
        // permitido — mesmo assim vem da fonte, como todo o resto: uma copia nossa envelheceria
        // sozinha e ninguem notaria.
        var mv = MvFiles.Distinct().ToArray();
        if (!mv.All(n => File.Exists(Caminho(LibraryDir, n))))
        {
            progress?.Report(L.T("Feeder_FetchingMv"));
            foreach (var nome in mv)
            {
                var destino = Caminho(LibraryDir, nome);
                if (File.Exists(destino)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(destino)!);
                await BaixarAsync(http, MvUrl + nome, destino, ct);
            }

            // O arquivo baixado tem mesmo a tecnica que vamos ligar no preset? Sem esta
            // conferencia, um download que "funcionou" — um 404 salvo como HTML, um repositorio
            // que renomeou o shader — deixa o jogo rodando com vetores zerados e sem erro em
            // lugar nenhum. Foi assim que o Feeder passou o dia inteiro cego.
            var kernel = Caminho(LibraryDir, "lumenite_Kernel.fx");
            var tecnica = MvTechnique.Split('@')[0];
            if (!File.Exists(kernel)
                || !File.ReadAllText(kernel).Contains($"technique {tecnica}", StringComparison.Ordinal))
            {
                foreach (var n in mv) TryDelete(Caminho(LibraryDir, n));
                throw new InvalidOperationException(L.T("Feeder_MvNoProvider"));
            }
        }

        // Fora do bloco acima de proposito: as texturas foram acrescentadas depois dos shaders,
        // e quem ja tinha a biblioteca montada tem os .fx em dia e nenhuma textura. Aninhado
        // ali, este download nunca rodava para essas instalacoes — que sao todas as existentes.
        foreach (var tex in MvTextures)
        {
            var destinoTex = Path.Combine(LibraryDir, "Textures", tex);
            if (File.Exists(destinoTex)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(destinoTex)!);
            await BaixarAsync(http, MvTexUrl + tex, destinoTex, ct);
        }
    }

    /// <summary>Resolve um caminho do manifesto (que usa "/") sob uma pasta.</summary>
    private static string Caminho(string raiz, string relativo) =>
        Path.Combine(raiz, relativo.Replace('/', Path.DirectorySeparatorChar));

    private static void TryDelete(string p)
    {
        try { if (File.Exists(p)) File.Delete(p); } catch { }
    }

    /// <summary>
    /// Busca a ultima versao do Feeder e do shader e, se mudou, guarda e leva aos jogos que ja o
    /// usam. Devolve quantos jogos foram atualizados, ou -1 quando nao havia nada novo.
    ///
    /// O Feeder muda rapido — nasceu em v0.1.0 e ja esta em 0.5.0, com o contrato de motion
    /// vectors trocado no meio do caminho. Esperar que alguem repare e reinstale a mao e o mesmo
    /// que ficar parado.
    /// </summary>
    public static async Task<int> UpdateAsync(IEnumerable<string> installDirs,
                                              IProgress<string>? progress = null,
                                              CancellationToken ct = default)
    {
        if (!File.Exists(LibraryAddon)) return -1;
        using var http = NewClient();

        var addon = await http.GetByteArrayAsync(AddonUrl, ct);
        if (addon.Length < 4096 || addon[0] != (byte)'M' || addon[1] != (byte)'Z')
            throw new InvalidOperationException(L.T("Feeder_BadDownload"));
        var fx = await http.GetByteArrayAsync(FxUrl, ct);
        if (!System.Text.Encoding.ASCII.GetString(fx).Contains("texMotionVectors", StringComparison.Ordinal))
            throw new InvalidOperationException(L.T("Feeder_MvNoProvider"));

        var atual = File.ReadAllBytes(LibraryAddon);
        if (addon.Length == atual.Length && addon.SequenceEqual(atual)) return -1;

        await File.WriteAllBytesAsync(LibraryAddon, addon, ct);
        await File.WriteAllBytesAsync(LibraryFx, fx, ct);
        Log.Info($"feeder atualizado na biblioteca ({addon.Length} bytes)");

        // Reimplantar por inteiro, e nao so trocar o .addon64: uma versao nova pode mudar o
        // contrato entre o shader e o addon — foi o que aconteceu da 0.1 para a 0.5 — e deixar
        // os dois em versoes diferentes e a mesma falha silenciosa de sempre.
        var n = 0;
        foreach (var dir in installDirs.Where(d => d is not null && Directory.Exists(d)))
        {
            foreach (var alvo in Directory.EnumerateFiles(dir, AddonFile, new EnumerationOptions
                     {
                         IgnoreInaccessible = true,
                         RecurseSubdirectories = true,
                         MaxRecursionDepth = 10,
                         AttributesToSkip = FileAttributes.ReparsePoint,
                     }))
            {
                var pasta = Path.GetDirectoryName(alvo);
                if (pasta is null || AddonService.IsGameRunning(pasta)) continue;
                try { Deploy(pasta); n++; }
                catch (Exception ex) { Log.Warn($"atualizar feeder em {alvo}: {ex.Message}"); }
            }
        }
        progress?.Report(L.T("Feeder_Updated", n));
        return n;
    }

    private static async Task BaixarAsync(HttpClient http, string url, string destino, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var origem = await resp.Content.ReadAsStreamAsync(ct);
        await using var arquivo = File.Create(destino);
        await origem.CopyToAsync(arquivo, ct);
    }

    private static bool EhPe(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return fs.ReadByte() == 'M' && fs.ReadByte() == 'Z';
        }
        catch { return false; }
    }


    /// <summary>
    /// Poe tudo na pasta do jogo: o addon ao lado do proxy do ReShade, os shaders onde o ReShade
    /// procura efeito.
    /// </summary>
    public static void Deploy(string targetDir, IProgress<string>? progress = null)
    {
        if (!InLibrary) throw new InvalidOperationException(L.T("Feeder_NotInLibrary"));

        var shaders = Path.Combine(targetDir, "reshade-shaders", "Shaders");
        Directory.CreateDirectory(shaders);

        File.Copy(LibraryAddon, Path.Combine(targetDir, AddonFile), overwrite: true);
        File.Copy(LibraryFx, Path.Combine(shaders, FxFile), overwrite: true);

        // O provedor de motion vectors e seus includes. Sempre sobrescritos: se ficarem de uma
        // versao antiga, o Feed le uma textura com layout diferente e o defeito e silencioso.
        foreach (var nome in MvFiles.Distinct())
        {
            var destino = Caminho(shaders, nome);
            Directory.CreateDirectory(Path.GetDirectoryName(destino)!);
            File.Copy(Caminho(LibraryDir, nome), destino, overwrite: true);
        }

        // Os includes base so entram se ainda nao existirem: uma instalacao completa do ReShade
        // ja os tem, e podem ser de uma versao diferente da que buscamos.
        foreach (var nome in BaseIncludes)
        {
            var destino = Path.Combine(shaders, nome);
            if (!File.Exists(destino)) File.Copy(Path.Combine(LibraryDir, nome), destino);
        }

        // As texturas vao para reshade-shaders\Textures, que e onde TextureSearchPaths aponta.
        var pastaTex = Path.Combine(targetDir, "reshade-shaders", "Textures");
        Directory.CreateDirectory(pastaTex);
        foreach (var tex in MvTextures)
            File.Copy(Path.Combine(LibraryDir, "Textures", tex), Path.Combine(pastaTex, tex), overwrite: true);

        // O DRME nao compila no ReShade 6.8 e so gera erro no log de quem tem a instalacao
        // antiga. Sai junto, senao fica poluindo o diagnostico para sempre.
        foreach (var velho in new[] { "MotionEstimation.fx", "MotionEstimation.fxh", "MotionEstimationUI.fxh", "MotionVectors.fxh" })
        {
            var p = Path.Combine(shaders, velho);
            try { if (File.Exists(p)) File.Delete(p); } catch (Exception ex) { Log.Warn($"feeder limpar {velho}: {ex.Message}"); }
        }

        DeploySuperResolution(targetDir, progress);
        progress?.Report(L.T("Feeder_Deployed"));
    }

    /// <summary>
    /// Poe o nvngx_dlss.dll na pasta, que so aqui precisa ser trazido de fora.
    ///
    /// Nos outros caminhos o jogo tem DLSS e portanto ja tem este arquivo. No caminho do Feeder
    /// ele nunca esta la — o jogo nao tem DLSS, e essa e justamente a razao de o Feeder existir.
    /// Sem ele nao ha feature de Super Resolution para avaliar em DLAA, e o pass neural, que
    /// entra por cima dela, nao tem sobre o que rodar: instalacao completa, log limpo, nada na
    /// tela.
    /// </summary>
    private static void DeploySuperResolution(string targetDir, IProgress<string>? progress)
    {
        const string arquivo = "nvngx_dlss.dll";
        var origem = Path.Combine(DlssRuntimeService.LibraryDir, arquivo);
        if (!File.Exists(origem)) { Log.Warn($"feeder: {arquivo} nao esta na biblioteca"); return; }

        var destino = Path.Combine(targetDir, arquivo);
        if (File.Exists(destino) && new FileInfo(destino).Length == new FileInfo(origem).Length) return;

        // Jogo com DLSS 1.0 fica INTOCADO, mesmo que isso custe o Feeder.
        //
        // A geracao 1.0 e outra API. Substituir a DLL nao a atualiza: o jogo passa a chamar uma
        // implementacao que nao atende o contrato dele e MORRE — no Final Fantasy XV, sempre ao
        // terminar de carregar o save, sem excecao no Event Log e sem breadcrumb, porque nao ha
        // codigo quebrado: e uma chamada valida a uma DLL que nao a responde.
        //
        // O Feeder precisa desta runtime para criar a propria feature, entao os dois nao cabem no
        // mesmo jogo: ou o DLSS nativo continua funcionando, ou entra o Feeder e o jogo perde o
        // DLSS dele. Entre quebrar um jogo que funciona e nao instalar um recurso, nao instalar
        // ganha — o usuario ainda tem o jogo.
        //
        // NeuralUpliftService.Detect ja nao conta DLSS 1.x como "tem DLSS", o que manda o jogo
        // para o Feeder; esta guarda existe porque o caminho do Feeder chegava aqui e recolocava
        // a runtime nova pela porta dos fundos, desfazendo aquela decisao sem dizer nada.
        if (File.Exists(destino) && DlssRuntimeService.ReadVersion(destino) is { Major: 1 })
        {
            Log.Warn($"feeder: {targetDir} usa DLSS 1.x; runtime NAO substituido (o jogo quebraria)");
            progress?.Report(L.T("Feeder_Dlss1_Skipped"));
            return;
        }

        // Mesma regra de todo runtime que este launcher escreve: a assinatura da NVIDIA decide.
        if (!DlssRuntimeService.IsGenuine(origem, out var porque))
        {
            Log.Warn($"feeder: {arquivo} recusado: {porque}");
            return;
        }

        var novo = !File.Exists(destino);
        var backup = destino + ".renodx-bak";
        if (!novo && !File.Exists(backup)) File.Copy(destino, backup);
        File.Copy(origem, destino, overwrite: true);
        // Marca so quando fomos nos que trouxemos o arquivo. Sem isso, desligar o recurso apagaria
        // uma copia que ja estava na pasta — o mesmo erro que o runtime neural ja cometeu uma vez.
        if (novo)
        {
            try { File.WriteAllText(SrMark(targetDir), DateTime.UtcNow.ToString("o")); }
            catch (Exception ex) { Log.Warn($"feeder mark: {ex.Message}"); }
        }
        progress?.Report(L.T("Feeder_DeployingSr"));
        Log.Info($"feeder: {arquivo} deployed to {targetDir}");
    }

    private static string SrMark(string targetDir) =>
        Path.Combine(targetDir, "nvngx_dlss.dll.renodx-ours");

    /// <summary>
    /// Tira as nossas duas tecnicas do preset, devolvendo o resto como estava.
    ///
    /// Sem isso, o preset fica apontando para um DLSS5_Feed.fx que acabamos de apagar; o ReShade
    /// reage reescrevendo a lista e removendo o que nao existe, e quem paga e o setup do usuario
    /// que estava na mesma linha. Um jogo desta maquina terminou com "Techniques=" vazio e catorze
    /// shaders de HDR desligados depois de uma remocao.
    ///
    /// So mexemos nas nossas: as outras entradas voltam exatamente como estavam.
    /// </summary>
    private static void RestaurarPreset(string targetDir)
    {
        try
        {
            var ini = Path.Combine(targetDir, "ReShade.ini");
            var relativo = File.Exists(ini) ? new IniFile(ini).Get("GENERAL", "PresetPath") : null;
            var preset = Path.GetFullPath(Path.Combine(
                targetDir, (relativo ?? @".\ReShadePreset.ini").TrimStart('.', '\\', '/')));
            if (!File.Exists(preset)) return;

            var nossas = new[] { "DLSS5_Feed@DLSS5_Feed.fx", "DLSS5_Feed_Debug@DLSS5_Feed.fx" };
            var linhas = File.ReadAllLines(preset).ToList();
            var primeiraSecao = linhas.FindIndex(l => l.TrimStart().StartsWith('['));
            var limite = primeiraSecao < 0 ? linhas.Count : primeiraSecao;
            var mudou = false;

            for (int i = 0; i < limite; i++)
            {
                var t = linhas[i].TrimStart();
                if (!t.StartsWith("Techniques=", StringComparison.OrdinalIgnoreCase)
                    && !t.StartsWith("TechniqueSorting=", StringComparison.OrdinalIgnoreCase)) continue;

                var corte = linhas[i].IndexOf('=');
                var chave = linhas[i][..corte];
                var restantes = linhas[i][(corte + 1)..]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(p => !nossas.Contains(p, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                var nova = chave + "=" + string.Join(',', restantes);
                if (nova == linhas[i]) continue;
                linhas[i] = nova;
                mudou = true;
            }

            if (mudou) File.WriteAllLines(preset, linhas, new System.Text.UTF8Encoding(false));
        }
        catch (Exception ex) { Log.Warn($"feeder preset restore: {ex.Message}"); }
    }

    /// <summary>
    /// Prepara o ReShade.ini: caminho dos shaders, profundidade generica, e as duas tecnicas na
    /// ordem exigida.
    ///
    /// A ordem nao e preferencia: o DRME escreve texMotionVectors e o DLSS5_Feed le dessa textura.
    /// Fica gravada em TechniqueSorting, que e como o ReShade guarda a ordem de execucao.
    /// </summary>
    /// <summary>
    /// O jogo e de motor pre-reversed-Z?
    ///
    /// A pergunta que importa e "este motor usa profundidade invertida?", e ela nao tem resposta
    /// direta de fora. A aproximacao boa e o dgVoodoo estar em uso: ele so entra em jogo D3D9, e
    /// D3D9 e anterior a reversed-Z virar praxe. Cobre o caso que motiva isto sem arriscar mexer
    /// na profundidade de um jogo moderno, onde o padrao 1 esta certo.
    /// </summary>
    private static bool EhMotorAntigo(string targetDir) =>
        File.Exists(Path.Combine(targetDir, "D3D9.dll"))
        && File.Exists(Path.Combine(targetDir, "dgVoodoo.conf"));

    public static void Configure(string targetDir, string iniPath, IProgress<string>? progress = null)
    {
        var ini = new IniFile(iniPath);

        // Onde procurar efeito e textura. Sem isto o ReShade nao acha o que acabamos de copiar.
        GarantirCaminho(ini, "GENERAL", "EffectSearchPaths", @".\reshade-shaders\Shaders\**");
        GarantirCaminho(ini, "GENERAL", "TextureSearchPaths", @".\reshade-shaders\Textures\**");

        // O Feeder le a profundidade da cena pelo addon Generic Depth, que vem com o ReShade e
        // costuma estar DESLIGADO — o ReShade.ini do Baldur's Gate desta maquina o lista em
        // DisabledAddons. Sem ele nao ha depth, e sem depth o Feeder nao tem o que entregar.
        var desligados = ini.Get("ADDON", "DisabledAddons");
        if (desligados is not null)
            ini.Set("ADDON", "DisabledAddons", RemoverDaLista(desligados, "Generic Depth"));

        // Profundidade NAO invertida, e o motor da epoca limpa o depth varias vezes por quadro.
        //
        // O ReShade assume RESHADE_DEPTH_INPUT_IS_REVERSED=1, que e certo para engine moderna com
        // reversed-Z e ERRADO para jogo dos anos 2000. Com o padrao, o DLSS recebe profundidade
        // invertida e a logica de desoclusao dele trabalha sobre lixo: a imagem sai LAVADA, sem
        // erro em lugar nenhum. Foi assim no Saints Row 2 ate o log mostrar "depth reversed=1".
        //
        // DepthCopyBeforeClears existe pelo motivo vizinho: esses motores limpam o depth mais de
        // uma vez por quadro (cena, cutscene, UI) e o ReShade por padrao pega o buffer no fim do
        // quadro, ja apagado. Se ainda vier vazio, o indice do clear e o proximo ajuste — da para
        // incrementa-lo ao vivo pelo overlay.
        if (EhMotorAntigo(targetDir))
        {
            GarantirDefine(ini, "RESHADE_DEPTH_INPUT_IS_REVERSED", "0");
            if (ini.Get("DEPTH", "DepthCopyBeforeClears") is null)
                ini.Set("DEPTH", "DepthCopyBeforeClears", "1");
        }

        // Na 0.6.0 o provedor de motion vectors e escolhido em tempo de COMPILACAO. Sem este
        // define o shader compila com DLSS5_MV_PROVIDER=0 — nenhum provedor — e o pass roda cego,
        // sem nada indicando: as tecnicas ficam ligadas, o log diz "feature ready", os frames
        // saem, e o filtro trabalha sobre vetores zerados.
        GarantirDefine(ini, "DLSS5_MV_PROVIDER", MvProviderId.ToString());

        var preset = ini.Get("GENERAL", "PresetPath");
        if (string.IsNullOrWhiteSpace(preset))
        {
            preset = @".\ReShadePreset.ini";
            ini.Set("GENERAL", "PresetPath", preset);
        }
        ini.Save();

        // As tecnicas ligadas moram no PRESET, nao no ReShade.ini, e na raiz do arquivo — sem
        // cabecalho de secao. E o que runtime.cpp faz: preset.set({}, "Techniques", ...).
        //
        // A ordem nao e preferencia: o DRME escreve texMotionVectors e o DLSS5_Feed le dessa
        // textura no mesmo frame. Invertida, o Feed le o que ainda nao foi escrito.
        var tecnicas = new[] { MvTechnique, "DLSS5_Feed@DLSS5_Feed.fx" };
        var presetPath = Path.GetFullPath(Path.Combine(targetDir, preset.TrimStart('.', '\\', '/')));
        DefinirNaRaiz(presetPath, "Techniques", tecnicas);
        DefinirNaRaiz(presetPath, "TechniqueSorting", tecnicas);

        progress?.Report(L.T("Feeder_Configured"));
    }

    /// <summary>
    /// Grava uma chave na raiz do preset (antes de qualquer [secao]), preservando o resto.
    ///
    /// O IniFile do projeto exige cabecalho de secao e nao alcanca a raiz. Em vez de mexer numa
    /// classe usada por todo o launcher para atender um caso, este arquivo pequeno tem seu proprio
    /// tratamento — o preset e curto e o formato, uma linha por chave.
    /// </summary>
    private static void DefinirNaRaiz(string presetPath, string chave, IEnumerable<string> itens)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(presetPath)!);

        // O preset e do usuario, nao nosso: ele pode ter um setup inteiro de shaders ali, montado
        // ao longo de meses. Uma copia antes da primeira alteracao e a unica forma de devolver
        // exatamente o que estava — o ReShade reescreve o arquivo sozinho ao fechar o jogo, e a
        // partir dai nao ha de onde tirar a lista original.
        var backup = presetPath + ".renodx-bak";
        if (File.Exists(presetPath) && !File.Exists(backup))
        {
            try { File.Copy(presetPath, backup); }
            catch (Exception ex) { Log.Warn($"feeder preset backup: {ex.Message}"); }
        }

        var linhas = File.Exists(presetPath) ? File.ReadAllLines(presetPath).ToList() : new List<string>();

        int primeiraSecao = linhas.FindIndex(l => l.TrimStart().StartsWith('['));
        int limite = primeiraSecao < 0 ? linhas.Count : primeiraSecao;

        int existente = -1;
        for (int i = 0; i < limite; i++)
        {
            var t = linhas[i].TrimStart();
            if (t.StartsWith(chave + "=", StringComparison.OrdinalIgnoreCase)) { existente = i; break; }
        }

        var atual = existente >= 0 ? linhas[existente][(linhas[existente].IndexOf('=') + 1)..] : null;
        var valor = chave + "=" + PrefixarLista(atual, itens);

        if (existente >= 0) linhas[existente] = valor;
        else linhas.Insert(limite, valor);

        File.WriteAllLines(presetPath, linhas, new System.Text.UTF8Encoding(false));
    }

    /// <summary>
    /// Poe (ou corrige) um define na lista PreprocessorDefinitions do ReShade.
    ///
    /// A lista e "NOME=VALOR,NOME=VALOR" e pode ja ter defines do usuario, entao a chave e
    /// substituida no lugar em vez de a lista ser reescrita.
    /// </summary>
    private static void GarantirDefine(IniFile ini, string nome, string valor)
    {
        var atual = ini.Get("GENERAL", "PreprocessorDefinitions") ?? "";
        var partes = atual.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => !p.StartsWith(nome + "=", StringComparison.OrdinalIgnoreCase))
            .Append($"{nome}={valor}");
        ini.Set("GENERAL", "PreprocessorDefinitions", string.Join(',', partes));
    }

    private static void GarantirCaminho(IniFile ini, string secao, string chave, string valor)
    {
        var atual = ini.Get(secao, chave);
        if (string.IsNullOrWhiteSpace(atual)) { ini.Set(secao, chave, valor); return; }
        var partes = atual.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (partes.Any(p => p.Equals(valor, StringComparison.OrdinalIgnoreCase))) return;
        ini.Set(secao, chave, string.Join(',', partes.Append(valor)));
    }

    /// <summary>Poe os itens na frente da lista, sem duplicar o que ja estava nela.</summary>
    private static string PrefixarLista(string? atual, IEnumerable<string> itens)
    {
        var resto = (atual ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => !itens.Any(i => i.Equals(p, StringComparison.OrdinalIgnoreCase)));
        return string.Join(',', itens.Concat(resto));
    }

    private static string RemoverDaLista(string? atual, string item)
    {
        if (string.IsNullOrWhiteSpace(atual)) return "";
        return string.Join(',', atual
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => !p.Equals(item, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Tira o Feeder da pasta do jogo. O provedor de motion vectors fica: e inerte com a
    /// tecnica desligada, outros shaders do usuario podem consumir a mesma textura, e apagar
    /// arquivo de terceiro por saber o nome dele nao e nosso direito.</summary>
    public static void Remove(string targetDir)
    {
        var addon = Path.Combine(targetDir, AddonFile);
        if (File.Exists(addon)) File.Delete(addon);
        var fx = Path.Combine(targetDir, "reshade-shaders", "Shaders", FxFile);
        if (File.Exists(fx)) File.Delete(fx);
        RestaurarPreset(targetDir);

        // O nvngx_dlss.dll so sai se fomos nos que o trouxemos. Se ja estava aqui, ele e do jogo
        // ou do usuario, e apagar por saber o nome do arquivo nao e nosso direito.
        if (File.Exists(SrMark(targetDir)))
        {
            try
            {
                var sr = Path.Combine(targetDir, "nvngx_dlss.dll");
                if (File.Exists(sr)) File.Delete(sr);
                File.Delete(SrMark(targetDir));
            }
            catch (Exception ex) { Log.Warn($"feeder remove SR: {ex.Message}"); }
        }
    }
}
