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
    /// O provedor de motion vectors: DRME, de Jakob Wapenhensch (CC BY-NC 4.0).
    ///
    /// NAO e o iMMERSE LaunchPad, que o guia da v0.1.0 do Feeder indicava. O LaunchPad calcula
    /// movimento, mas publica em texturas proprias (MotionTexLA*, MotionTexLB*) e nao declara
    /// `texMotionVectors` em lugar nenhum — zero ocorrencias no arquivo. O DLSS5_Feed.fx 0.5.0 le
    /// exatamente dessa textura, a "community-standard", entao os dois nunca se encontravam:
    /// tudo instalado, tudo verde, e o log dizendo
    ///
    ///     no known texMotionVectors provider found: motion vectors will be zero (still images only)
    ///
    /// Sem vetores, o DLSS neural perde a informacao temporal e o ganho fica proximo de nada em
    /// movimento — que foi exatamente o sintoma relatado ("efeito muito pouco").
    /// </summary>
    private static readonly string[] MvFiles =
    [
        "MotionEstimation.fx", "MotionEstimation.fxh", "MotionEstimationUI.fxh", "MotionVectors.fxh",
    ];
    private const string MvTechnique = "DRME@MotionEstimation.fx";
    private const string MvUrl =
        "https://raw.githubusercontent.com/JakobPCoder/ReshadeMotionEstimation/main/";

    // latest/download em vez de uma tag fixa: o Feeder acabou de nascer (v0.1.0) e vai mudar
    // rapido. Fixar versao aqui congelaria o launcher numa build antiga do dia da integracao.
    private const string AddonUrl = "https://github.com/jlrouzies-fr/DLSS5-Feeder/releases/latest/download/dlss5-feed.addon64";
    private const string FxUrl = "https://github.com/jlrouzies-fr/DLSS5-Feeder/releases/latest/download/DLSS5_Feed.fx";

    // O instalador do ReShade poe o dxgi.dll e nada mais: a pasta Shaders fica vazia. O
    // DLSS5_Feed.fx abre com #include "ReShade.fxh" e falha a compilar sem ele — e o sintoma nao
    // aparece na instalacao, so no ReShade.log depois de abrir o jogo:
    //   preprocessor error: could not open included file 'ReShade.fxh'
    private static readonly string[] BaseIncludes = ["ReShade.fxh", "ReShadeUI.fxh"];
    private const string BaseIncludeUrl = "https://raw.githubusercontent.com/crosire/reshade-shaders/slim/Shaders/";

    public static string LibraryDir { get; } = Path.Combine(AppPaths.DataDir, "feeder");
    public static string LibraryAddon { get; } = Path.Combine(LibraryDir, AddonFile);
    public static string LibraryFx { get; } = Path.Combine(LibraryDir, FxFile);

    /// <summary>A biblioteca tem tudo o que o deploy precisa.</summary>
    public static bool InLibrary =>
        File.Exists(LibraryAddon) && File.Exists(LibraryFx)
        && MvFiles.All(n => File.Exists(Path.Combine(LibraryDir, n)))
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
        var addon = Path.Combine(targetDir, AddonFile);
        if (!File.Exists(addon)) return false;

        var shaders = Path.Combine(targetDir, "reshade-shaders", "Shaders");
        if (!File.Exists(Path.Combine(shaders, FxFile))) return false;
        if (!MvFiles.All(n => File.Exists(Path.Combine(shaders, n)))) return false;
        if (!BaseIncludes.All(n => File.Exists(Path.Combine(shaders, n)))) return false;

        // Integridade do addon contra a biblioteca, quando ha uma para comparar.
        try
        {
            if (File.Exists(LibraryAddon)
                && new FileInfo(addon).Length != new FileInfo(LibraryAddon).Length) return false;
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
        if (jogoTemDlss || exePath is null) return false;
        return PeUtils.Inspect(exePath, readImports: false)?.Is64Bit == true;
    }

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

        // O provedor de motion vectors. Sao quatro arquivos de texto vindos do repositorio do
        // autor — nada e re-hospedado por nos, o que a licenca CC BY-NC pede em termos de credito
        // fica com ele, e o launcher e gratuito.
        if (!MvFiles.All(n => File.Exists(Path.Combine(LibraryDir, n))))
        {
            progress?.Report(L.T("Feeder_FetchingMv"));
            foreach (var nome in MvFiles)
            {
                var destino = Path.Combine(LibraryDir, nome);
                if (File.Exists(destino)) continue;
                await BaixarAsync(http, MvUrl + nome, destino, ct);
            }
            // O .fx precisa declarar a textura que o Feed le. Sem isso o download "funcionou" e
            // o jogo roda com vetores zerados, sem erro nenhum a lugar nenhum.
            var principal = Path.Combine(LibraryDir, MvFiles[0]);
            if (!File.ReadAllText(principal).Contains("texMotionVectors", StringComparison.Ordinal))
            {
                foreach (var n in MvFiles) TryDelete(Path.Combine(LibraryDir, n));
                throw new InvalidOperationException(L.T("Feeder_MvNoProvider"));
            }
        }
    }

    private static void TryDelete(string p)
    {
        try { if (File.Exists(p)) File.Delete(p); } catch { }
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
        foreach (var nome in MvFiles)
            File.Copy(Path.Combine(LibraryDir, nome), Path.Combine(shaders, nome), overwrite: true);

        // Os includes base so entram se ainda nao existirem: uma instalacao completa do ReShade
        // ja os tem, e podem ser de uma versao diferente da que buscamos.
        foreach (var nome in BaseIncludes)
        {
            var destino = Path.Combine(shaders, nome);
            if (!File.Exists(destino)) File.Copy(Path.Combine(LibraryDir, nome), destino);
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
