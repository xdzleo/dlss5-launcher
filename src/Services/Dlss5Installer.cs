using System.IO;
using RenoDXLauncher.Localization;
using RenoDXLauncher.Models;

namespace RenoDXLauncher.Services;

/// <summary>
/// The whole DLSS 5 chain, in one call.
///
/// Everything this does already existed, spread across the neural service, the runtime service,
/// the ReShade service and two copies of the ordering logic — one in the view model, one in the
/// CLI. That is how links kept going missing: each caller assembled its own version of the chain
/// and each forgot something different. The Ray Reconstruction runtime was absent from both. The
/// early-load key was only in one. Neither repaired an incoherent Streamline set first.
///
/// The order below is the one the community documents, and it is not arbitrary:
///
///   1. host        — a GPU or driver that cannot run it makes every later step pointless
///   2. library     — the runtime and the addon have to exist somewhere before they can be copied
///   3. streamline  — a set from mismatched builds crashes the game on launch, so it is repaired
///                    before anything else is added to the folder
///   4. reshade     — nothing loads an addon without it, and the proxy name depends on the exe
///   5. addon       — deployed or refreshed to the library's build
///   6. runtimes    — nvngx_dlssnr and nvngx_dlssd, beside the executable, where the addon looks
///   7. ini         — LoadFromDllMain first (the addon must be up before the game's DLSS SDK),
///                    then the enable key
///
/// Each step reports what it found and what it did, because every broken link produces the same
/// symptom from outside — the game opens and nothing happens — and a user who cannot tell which
/// one failed cannot act.
/// </summary>
public static class Dlss5Installer
{
    /// <param name="Ok">Every step that could be done was done.</param>
    /// <param name="Blocker">The one thing stopping it, in the user's words, or null.</param>
    /// <param name="Steps">What happened, link by link, for display.</param>
    /// <param name="Manual">What the user still has to do themselves, in the game.</param>
    public record Result(bool Ok, string? Blocker, IReadOnlyList<string> Steps, IReadOnlyList<string> Manual);

    public static async Task<Result> InstallAsync(
        GameInfo game, string targetDir, string? iniPath, string? exePath, string? addonPath,
        DlssIndexService index, ReShadeService reshade, RhiManifestService? rhi = null,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var steps = new List<string>();
        var manual = new List<string>();
        var installDir = game.InstallDir;
        iniPath ??= Path.Combine(targetDir, "ReShade.ini");

        void Step(string s) { steps.Add(s); progress?.Report(s); }

        if (AddonService.IsGameRunning(targetDir))
            return new Result(false, L.T("Error_GameRunning"), steps, manual);

        // 1. The curated index says to leave some games' DLSS alone. Nothing below is worth doing
        //    against that.
        if (rhi is not null && rhi.SkipsDlss(game.Name))
            return new Result(false, L.T("Dlss5_Blocked_Skip"), steps, manual);

        // 2. Fill the library before touching the game: a half-installed folder is worse than an
        //    untouched one, and the fetch is the step most likely to fail (network).
        var gameDirs = new[] { installDir };
        AutoDiscoverRuntimeQuiet(gameDirs, progress);
        NeuralUpliftService.AutoDiscoverAddon(gameDirs);
        try { await NeuralUpliftService.FetchAddonAsync(progress, ct); }
        catch (Exception ex) { Step(L.T("Dlss5_Step_AddonFetchFailed", ex.Message)); }

        // Antes de detectar, e nao depois: versoes antigas deste launcher deixavam um Ray
        // Reconstruction em pastas que nunca tiveram runtime proprio, e a varredura o lia como
        // "o jogo tem DLSS". Limpar primeiro faz a deteccao enxergar o jogo, nao o nosso rastro.
        NeuralUpliftService.CleanOrphanRayReconstruction(targetDir, progress);

        var det = NeuralUpliftService.Detect(installDir, targetDir, addonPath);

        // Tres caminhos, e qual deles serve depende de duas perguntas: o jogo alcanca D3D12, e o
        // jogo tem DLSS proprio.
        //
        //   D3D12                  -> nada no meio, o pass roda no device do jogo
        //   DX11 + tem DLSS        -> ponte: um segundo device D3D12 reproduz o contrato do jogo
        //   DX11 + NAO tem DLSS    -> Feeder: nao ha contrato para reproduzir, entao ele FABRICA
        //                             um (motion vectors e depth vindos de um shader do ReShade)
        //
        // Ponte e Feeder nao convivem, e o autor do Feeder e explicito quanto a isso. A escolha
        // acima ja os mantem exclusivos: uma exige DLSS nativo, o outro exige a ausencia dele.
        // Um jogo D3D9 nao tem como receber o pass: o ReShade em D3D9 para no Shader Model 3, e
        // nenhum provedor de motion vectors compila; e a API nao tem handle compartilhado nem
        // fence, que e por onde as texturas chegam ao device D3D12. Traduzir para D3D11 antes
        // resolve os dois de uma vez e transforma o jogo no caso comum.
        //
        // Vem antes do ReShade de proposito: depois de envolvido, o jogo apresenta por dxgi, e o
        // proxy tem de ser dxgi.dll — nao d3d9.dll, que agora pertence ao dgVoodoo.
        var precisaDgVoodoo = DgVoodooService.Applies(exePath);
        if (precisaDgVoodoo)
        {
            try
            {
                await DgVoodooService.FetchAsync(progress, ct);
                var bits64 = exePath is not null
                             && PeUtils.Inspect(exePath, readImports: false)?.Is64Bit == true;
                DgVoodooService.Deploy(targetDir, bits64, progress);
                Step(L.T("Dlss5_Step_DgVoodoo"));
                manual.Add(L.T("Dlss5_Manual_DgVoodoo"));
            }
            catch (Exception ex) { Step(ex.Message); }
        }

        var alcancaD3d12 = exePath is null || LooksLikeD3D12(exePath) || precisaDgVoodoo;

        // HasDlss varre a pasta, e a partir da primeira instalacao a pasta contem runtimes que
        // NOS copiamos — o de Ray Reconstruction em todo caminho, o de Super Resolution no do
        // Feeder. Reinstalar entao lia "este jogo tem DLSS" sobre os proprios arquivos do
        // launcher, trocava o Feeder pela ponte e mandava o usuario ligar um DLSS que o jogo nao
        // tem. O Feeder ja implantado e a evidencia de que a decisao anterior foi essa.
        var feederJaAqui = FeederService.IsDeployed(targetDir);
        var temDlssNativo = det.HasDlss && !feederJaAqui;

        // A ponte e de DirectX 11 — ela engancha o device D3D11 do jogo para dar ao pass neural
        // um lugar onde rodar. Num jogo Vulkan nao ha device D3D11 nenhum: instala-la ali punha
        // um addon inerte na pasta e, pior, o passo manual mandava "abra pelo executavel de
        // DirectX 11", que no DOOM Eternal nao existe.
        var ehVulkan = VulkanLayerService.Applies(exePath);
        var precisaPonte = temDlssNativo && !alcancaD3d12 && !ehVulkan;

        // Jogo com FSR ou XeSS proprio e sem DLSS: o OptiScaler redireciona o upscaler que ele ja
        // tem. Vem ANTES do Feeder na decisao, e por um motivo de qualidade, nao de gosto — o
        // jogo ja calcula motion vectors e depth corretos para o proprio upscaler, enquanto o
        // Feeder teria de reconstruir os dois por fora, com um shader. Dado do engine ganha de
        // dado fabricado sempre que existe.
        var upscaler = OptiScalerService.AchaUpscaler(targetDir);
        var precisaOpti = !temDlssNativo && !ehVulkan && upscaler is not null
                          && OptiScalerService.Applies(targetDir, temDlssNativo);

        // Jogo Vulkan vai para o Feeder MESMO tendo DLSS proprio, e a razao esta no addon: ele
        // engancha `NVSDK_NGX_D3D12_EvaluateFeature_C`, uma funcao de Direct3D 12. Um jogo Vulkan
        // chama a familia NVSDK_NGX_VULKAN_*, que o addon nao procura — ele carrega, nao acha o
        // que enganchar, e ainda assim tenta alocar os buffers do pass. O sintoma que chega ao
        // usuario e "Failed to allocate video memory" numa placa com 32 GB livres, e no log:
        //   ERROR | [DLSS 5 Neural Rendering] vtable::Hook(Failed to find NVSDK_NGX_D3D12_...)
        //
        // O Feeder resolve porque nao depende do NGX do jogo: ele cria um device D3D12 PROPRIO e
        // importa as texturas do VkDevice por memoria externa. E o caminho que o autor documenta
        // para Vulkan, e o que faz o DOOM 2016 funcionar na tabela de status dele.
        var precisaFeeder = (!temDlssNativo && !precisaOpti
                             && FeederService.Applies(exePath, temDlssNativo, alcancaD3d12))
                            || (ehVulkan && FeederService.Applies(exePath, false, true));

        // Sem DLSS, sem OptiScaler e sem Feeder aplicavel, nao ha o que fazer — e so aqui que o
        // bloqueio antigo ainda vale. Antes ele valia para todo jogo sem DLSS.
        if (!temDlssNativo && !precisaOpti && !precisaFeeder)
            return new Result(false, L.T("Dlss5_Blocked_NoDlss"), steps, manual);

        if (precisaPonte)
        {
            try { await NeuralUpliftService.FetchBridgeAsync(progress, ct); }
            catch (Exception ex) { Step(ex.Message); }
            if (!File.Exists(NeuralUpliftService.LibraryBridge))
                return new Result(false, L.T("Dlss5_Blocked_NoBridge"), steps, manual);
        }

        // A biblioteca antes da pasta do jogo, como em todo o resto: uma pasta meio escrita e
        // pior do que uma intocada, e o download e o passo com mais chance de falhar.
        if (precisaOpti)
        {
            try { await OptiScalerService.FetchAsync(progress, ct); }
            catch (Exception ex) { Step(ex.Message); }
            if (!OptiScalerService.InLibrary)
            {
                // Sem o OptiScaler, o Feeder ainda atende este jogo — pior, porque reconstroi o
                // que o engine ja tinha, mas melhor do que nao instalar nada.
                precisaOpti = false;
                precisaFeeder = FeederService.Applies(exePath, temDlssNativo, alcancaD3d12);
            }
        }

        var precisaHost64 = precisaFeeder && FeederService.NeedsHost64(exePath);
        if (precisaFeeder)
        {
            try
            {
                await FeederService.FetchAsync(progress, ct);
                if (precisaHost64) await FeederService.FetchBits32Async(progress, ct);
            }
            catch (Exception ex) { Step(ex.Message); }
            if (!FeederService.InLibrary)
                return new Result(false, L.T("Dlss5_Blocked_NoFeeder"), steps, manual);
            if (precisaHost64 && !FeederService.Bits32InLibrary)
                return new Result(false, L.T("Dlss5_Blocked_No32"), steps, manual);
        }

        if (!det.Host.RuntimeInLibrary)
        {
            try
            {
                var v = await NeuralUpliftService.FetchRuntimeAsync(index, progress, ct);
                if (v is not null) Step(L.T("Dlss5_Step_RuntimeFetched", v));
            }
            catch (Exception ex) { Step(ex.Message); }
            det = NeuralUpliftService.Detect(installDir, targetDir, addonPath);
        }

        if (det.Host.Blocker is { } hostBlocker) return new Result(false, hostBlocker, steps, manual);
        if (det.GenericBlocker is { } addonBlocker) return new Result(false, addonBlocker, steps, manual);

        // 3. An incoherent Streamline set is the documented way a game crashes on launch. Repair
        //    it before adding anything, so a crash after this cannot be blamed on what we added.
        // SO conjunto incoerente. Um "arquivo nao assinado" tambem e severidade erro, e tratar os
        // dois igual disparava uma troca COMPLETA de Frame Generation por causa de um arquivo
        // solto — uma operacao que este servico so oferece separada, de proposito, e que CRIA
        // arquivo em pasta que nunca teve conjunto (sem backup, portanto sem volta). Arquivo nao
        // assinado e reportado, nao "consertado" reescrevendo tudo em volta dele.
        var health = DlssRuntimeService.CheckHealth(installDir);
        foreach (var h in health.Where(h => h.Kind == DlssRuntimeService.KindNotSigned))
            Step(h.Message);
        if (health.Any(h => h.Kind == DlssRuntimeService.KindIncoherentSet))
        {
            try
            {
                DlssRuntimeService.Repair(installDir, targetDir, progress);
                var left = DlssRuntimeService.CheckHealth(installDir).Count(h => h.Severity == "erro");
                Step(left == 0 ? L.T("Dlss5_Step_StreamlineFixed") : L.T("Dlss5_Step_StreamlineLeft", left));
            }
            catch (Exception ex) { Step(L.T("Dlss5_Step_StreamlineFailed", ex.Message)); }
        }

        // Com o dgVoodoo no meio, um ReShade que esteja como d3d9.dll TEM de sair de la.
        //
        // No Windows o nome de arquivo nao distingue maiusculas: "d3d9.dll" e "D3D9.dll" sao o
        // mesmo arquivo, e o wrapper simplesmente escreve por cima do ReShade — que some sem
        // aviso. O guia do Feeder diz isso em uma linha: "ReShade must install as dxgi.dll, not
        // d3d9.dll, since dgVoodoo owns that filename now and the two would fight."
        //
        // Depois de traduzido o jogo apresenta por DXGI, entao dxgi.dll e o proxy certo de
        // qualquer forma. Isto so realoca; o backup do wrapper preserva o que estava la.
        if (precisaDgVoodoo
            && det.ReShadeDllName is not null
            && det.ReShadeDllName.Equals("d3d9.dll", StringComparison.OrdinalIgnoreCase))
        {
            det = det with { ReShadeDllName = null };
            Step(L.T("Dlss5_Step_ReShadeMoved"));
        }

        // 4. ReShade. Without it nothing loads the addon at all.
        //
        // Jogo Vulkan nao usa proxy de DLL: ele fala com vulkan-1.dll e mais nada, entao um
        // dxgi.dll na pasta nunca e carregado e a instalacao inteira fica inerte — sem sequer um
        // ReShade.log para dizer que falhou. Era o caso do DOOM Eternal, que recebia dxgi.dll e
        // a ponte de DX11 num jogo que nao tem uma linha de DirectX.
        var precisaCamadaVulkan = !precisaDgVoodoo && ehVulkan;
        if (precisaCamadaVulkan)
        {
            var bits64 = exePath is null
                         || PeUtils.Inspect(exePath, readImports: false)?.Is64Bit != false;
            if (VulkanLayerService.IsRegistered(targetDir, bits64))
                Step(L.T("Dlss5_Step_VulkanLayer"));
            else if (await VulkanLayerService.DeployAsync(reshade, targetDir, bits64, progress))
                Step(L.T("Dlss5_Step_VulkanLayer"));
            else
            {
                Step(L.T("Dlss5_Step_VulkanLayerFailed", "HKLM"));
                manual.Add(L.T("Dlss5_Manual_VulkanAdmin"));
            }
        }
        else if (det.ReShadeDllName is null)
        {
            if (exePath is null) return new Result(false, L.T("Dlss5_Blocked_NoExe"), steps, manual);
            // Com o dgVoodoo no meio, o proxy TEM de ser dxgi.dll: o d3d9.dll agora pertence ao
            // wrapper, e os dois com o mesmo nome brigariam pelo carregamento. O override vai
            // por cima de qualquer palpite da heuristica ou do indice do RHI.
            var dllForcada = precisaDgVoodoo ? "dxgi.dll" : rhi?.DllNameOverride(game.Name);
            var apiForcada = precisaDgVoodoo ? "DX11" : rhi?.GraphicsApi(game.Name);
            var dep = await reshade.DeployAsync(targetDir, exePath, apiForcada, dllForcada, progress);
            if (!dep.Success) return new Result(false, dep.Message, steps, manual);
            Step(L.T("Dlss5_Step_ReShade", dep.DllName ?? "?"));
        }
        else Step(L.T("Dlss5_Step_ReShadeAlready", det.ReShadeDllName));

        // 5-7. Addon, both runtimes, early-load key and the switch. Apply owns this order.
        //
        // Wrapped, because Apply throws — game running, library file gone, a read-only ini, any
        // copy failing. Letting that escape discarded the whole step list and left the folder
        // with a repaired Streamline set and a freshly deployed ReShade proxy for a feature that
        // was never installed, with nothing telling the caller what had already been written.
        try
        {
            NeuralUpliftService.Apply(targetDir, iniPath, det.UsesGeneric, progress);
            Step(det.UsesGeneric ? L.T("Dlss5_Step_AddonGeneric") : L.T("Dlss5_Step_AddonGame"));
            if (precisaPonte)
            {
                FeederService.Remove(targetDir); // ver acima: os dois nunca convivem
                NeuralUpliftService.DeployBridge(targetDir, progress);
                Step(L.T("Dlss5_Step_Bridge"));
                // O jogo DX11 tem mais de um executavel (o Baldur's Gate tem um Vulkan e um DX11),
                // e a ponte so tem o que enganchar num deles. Dizer qual e a diferenca entre
                // funcionar e "instalei e nao vi nada".
                manual.Add(L.T("Dlss5_Manual_UseDx11Exe"));
            }
            if (precisaOpti)
            {
                // Feeder e OptiScaler alimentam o mesmo pass: dois produtores para um consumidor
                // nao e configuracao, e bug. Sai o que nao vale mais.
                FeederService.Remove(targetDir);
                OptiScalerService.Deploy(targetDir, progress);
                Step(L.T("Dlss5_Step_OptiScaler", upscaler!));
                // O redirecionamento so acontece quando o jogo CHAMA o upscaler. Deixar isso
                // implicito e a diferenca entre funcionar e "instalei e nao vi nada".
                manual.Add(L.T("Dlss5_Manual_OptiScaler", upscaler!));
            }
            if (precisaFeeder)
            {
                // Exclusivos, e o autor do Feeder e explicito quanto a isso. Uma pasta pode ter
                // as duas por historico — o Sonic ficou assim depois de o launcher ter lido, por
                // engano, um runtime nosso como sendo do jogo e ter escolhido a ponte.
                NeuralUpliftService.RemoveBridge(targetDir);
                FeederService.Deploy(targetDir, progress);
                FeederService.Configure(targetDir, iniPath, progress);
                // O Feeder resolve o addon de NR por nome literal; sem esta copia ele entrega
                // frames com o pass sem quem o dirija, e diz isso so no proprio log.
                NeuralUpliftService.GarantirNomeDoFeeder(targetDir, iniPath, progress);
                FeederService.AjustarAlocacao(targetDir, progress);
                Step(L.T("Dlss5_Step_Feeder"));

                if (precisaHost64)
                {
                    await FeederService.DeployBits32Async(targetDir, reshade, progress, ct);
                    Step(L.T("Dlss5_Step_Host64"));
                    // A janela do auxiliar aparece junto com o jogo na primeira vez. Sem aviso,
                    // isso parece coisa estranha se abrindo sozinha.
                    manual.Add(L.T("Dlss5_Manual_Host64"));
                }
                // Duas coisas que nenhum instalador resolve, e que decidem se a pessoa vai achar
                // que funcionou: nao ha ganho de FPS (e DLAA), e MSAA/SSAA do jogo precisa sair.
                manual.Add(L.T("Dlss5_Manual_FeederNoFps"));
                manual.Add(L.T("Dlss5_Manual_FeederMsaa"));
            }
        }
        catch (Exception ex)
        {
            Step(ex.Message);
            return new Result(false, ex.Message, steps, manual);
        }

        // What no installer can do: the feature reads the game's own DLSS buffers, so DLSS has to
        // be on in the game's settings. Saying so is the difference between "it does not work" and
        // "there is one switch left".
        // "Ligue o DLSS nas opcoes do jogo" seria instrucao impossivel no caminho do Feeder: ele
        // existe justamente porque o jogo NAO tem DLSS. Mandar procurar uma opcao que nao existe
        // faria a pessoa concluir que instalou errado.
        if (!precisaFeeder) manual.Add(L.T("Dlss5_Manual_EnableDlss"));
        manual.Add(L.T("Dlss5_Manual_Overlay"));

        var applied = NeuralUpliftService.IsApplied(targetDir, iniPath, addonPath);
        return new Result(applied, applied ? null : L.T("Dlss5_Blocked_Unknown"), steps, manual);
    }

    /// <summary>
    /// O executavel alcanca D3D12?
    ///
    /// Responde SIM na duvida. Um jogo pode carregar `d3d12.dll` dinamicamente, e nesse caso a
    /// tabela de importacao nao diz nada — entao so um NAO bem fundamentado bloqueia: o
    /// executavel importa uma API grafica concorrente e nao importa D3D12. Sem essa condicao
    /// dupla, um binario que resolve tudo em runtime seria barrado sem motivo.
    /// </summary>
    /// <summary>O executavel alcanca D3D12? Null (exe desconhecido) responde SIM, como o resto
    /// da heuristica: so um NAO bem fundamentado desvia do caminho normal.</summary>
    public static bool ReachesD3D12(string? exePath) => exePath is null || LooksLikeD3D12(exePath);

    private static bool LooksLikeD3D12(string exePath)
    {
        var pe = PeUtils.Inspect(exePath);
        if (pe is null) return true;
        if (pe.Imports.Any(i => i.Equals("d3d12.dll", StringComparison.OrdinalIgnoreCase))) return true;

        // A tabela de importacao nao basta. Um renderizador Vulkan carrega `vulkan-1.dll` em
        // runtime e nao aparece ali: o `bg3.exe` do Baldur's Gate importa SO `dxgi.dll`, o que
        // pela tabela e indistinguivel de um jogo que resolve D3D12 dinamicamente.
        //
        // O nome do modulo, porem, precisa existir em algum lugar do binario para ser passado ao
        // LoadLibrary. Procurar a string decide o caso: no Baldur's Gate, `d3d12` aparece ZERO
        // vezes nos dois executaveis, e `vulkan-1` aparece.
        var mencionaD3d12 = ContainsAscii(exePath, "d3d12");
        if (mencionaD3d12) return true;
        var mencionaOutra = ContainsAscii(exePath, "vulkan-1") || ContainsAscii(exePath, "d3d11");
        // So um NAO bem fundamentado — mencionar outra API e nunca mencionar D3D12 — decide pela
        // ponte. Silencio total continua sendo "provavelmente sim", para nao barrar sem base.
        return !mencionaOutra;
    }

    /// <summary>Procura uma string ASCII no arquivo, sem carrega-lo inteiro na memoria: estes
    /// executaveis passam de 100 MB.</summary>
    private static bool ContainsAscii(string path, string needle)
    {
        try
        {
            var alvo = System.Text.Encoding.ASCII.GetBytes(needle);
            using var fs = File.OpenRead(path);
            var buf = new byte[1 << 20];
            var carry = alvo.Length - 1;
            int lido, inicio = 0;
            while ((lido = fs.Read(buf, inicio, buf.Length - inicio)) > 0)
            {
                var total = inicio + lido;
                for (var i = 0; i <= total - alvo.Length; i++)
                {
                    var j = 0;
                    while (j < alvo.Length && buf[i + j] == alvo[j]) j++;
                    if (j == alvo.Length) return true;
                }
                // preserva a cauda, senao uma ocorrencia partida entre dois blocos escapa
                if (total >= carry) Array.Copy(buf, total - carry, buf, 0, carry);
                inicio = Math.Min(carry, total);
            }
        }
        catch (Exception ex) { Log.Warn($"scan {needle} em {path}: {ex.Message}"); }
        return false;
    }

    /// <summary>The library sweep is slow and noisy; it only matters when the runtime is absent.</summary>
    private static void AutoDiscoverRuntimeQuiet(IEnumerable<string> gameDirs, IProgress<string>? progress)
    {
        try { NeuralUpliftService.AutoDiscoverRuntime(gameDirs, progress); }
        catch (Exception ex) { Log.Warn($"dlss5 runtime discovery: {ex.Message}"); }
    }
}
