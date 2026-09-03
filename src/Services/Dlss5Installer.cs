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
        IProgress<string>? progress = null, CancellationToken ct = default,
        bool preferirDxvk = true, bool forcarDgVoodoo = false, bool trocarDlss1 = false)
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

        // Direct3D 10 e o terceiro caso de traducao, e o unico SEM escolha: so o DXVK cobre. O
        // dgVoodoo entra como D3D9.dll e nunca ve um device D3D10; o Feeder diz "D3D10 is not
        // supported". O DXVK traz o d3d10core.dll — a camada por baixo do d3d10.dll do Windows,
        // que o proprio Windows resolve na pasta do jogo — e dali o jogo e Vulkan, como no DX9
        // pelo DXVK. Ate a 1.69 isto era uma recusa; foi o Just Cause 2 que a motivou, e e o Just
        // Cause 2 que passa por aqui agora. Ver DxvkService.D3d10Files.
        var precisaDxvkD3d10 = DxvkService.AppliesD3d10(exePath);
        if (precisaDxvkD3d10 && forcarDgVoodoo)
        {
            // --dgvoodoo num jogo D3D10 e um pedido que nao tem como atender. Dizer por que, em
            // vez de obedecer em silencio e entregar a instalacao que fechava o jogo.
            Step(L.T("Dlss5_Step_DgVoodooNaoTraduzD3d10"));
            forcarDgVoodoo = false;
        }

        // Duas rotas para o mesmo problema, e desde a 1.57 o DXVK e a primeira.
        //
        // O dgVoodoo2 entrega D3D11 e foi o caminho original. Ele derruba jogos que nao tem
        // defeito nenhum: o Resident Evil Revelations 2 crasha com 0xc0000005 dentro do proprio
        // d3d9.dll dele, em TODA configuracao testada — VRAM, OutputAPI, PresentationModel,
        // VideoCard — com o binario de SHA identico ao que roda o Saints Row 2. Sem ele o jogo
        // abre normal.
        //
        // O DXVK traduz para Vulkan em vez de D3D11, cobre mais jogos e e mantido ativamente.
        // Alem disso desbloqueia o que o D3D9 nunca teve: em Vulkan o ReShade compila COMPUTE
        // SHADER, e e por isso que o Lumenite Kernel funciona la e nao em D3D9 puro, onde tudo
        // para no Shader Model 3.
        //
        // O dgVoodoo continua disponivel em --dgvoodoo, para o caso inverso: jogo que o DXVK
        // recuse e ele aceite. Nenhum dos dois cobre 100%, e por isso os dois ficam.
        var usarDxvk = precisaDxvkD3d10
                       || (preferirDxvk && !forcarDgVoodoo && precisaDgVoodoo && ehJogo32Bits(exePath)
                           && DxvkService.RecomendadoPara(exePath));

        // Trocar de tradutor exige DESFAZER o outro, nao so instalar o novo. Os dois disputam o
        // d3d9.dll, e o resto da cadeia muda junto: o ReShade sai de camada Vulkan para proxy (ou
        // o contrario) e as metades de 32 bits trocam de build. Deixar o anterior para tras
        // significa duas DLLs disputando o mesmo nome e uma camada Vulkan registrada apontando
        // para um jogo que voltou a ser D3D11 — nos dois casos, nada carrega.
        var trocaTradutor = (precisaDgVoodoo && ehJogo32Bits(exePath)) || precisaDxvkD3d10;

        // O download ANTES de mexer na pasta, como manda a regra 2 la em cima — e aqui ela tinha
        // sido esquecida, com custo: o dgVoodoo saia e o proxy dxgi.dll do ReShade era posto de
        // lado, e so entao o DXVK era baixado. Offline, ou com o GitHub devolvendo 403, o
        // download falhava, o dgVoodoo voltava e o proxy nao — `det` ainda dizia "dxgi.dll
        // presente", o instalador acreditava, e a cadeia terminava verde num jogo em que nada
        // carregava, sem sequer um ReShade.log. Com o download primeiro, uma falha aqui nao muda
        // um byte da pasta, e `det` continua descrevendo o disco.
        if (usarDxvk)
        {
            try
            {
                if (precisaDxvkD3d10)
                {
                    // Outro download, de outra versao: a 1.10.3, a ultima com d3d10.dll e
                    // d3d10_1.dll proprios. Os cinco arquivos, no bitness do jogo; o d3d9.dll
                    // fica de fora de proposito. Ver DxvkService.D3d10Files, que conta por que a
                    // release atual nao serve aqui.
                    await DxvkService.FetchD3d10Async(progress, ct);
                }
                else await DxvkService.FetchAsync(progress, ct);
            }
            catch (Exception ex)
            {
                Step(ex.Message);
                usarDxvk = false;
                // Sem o tradutor nao ha rota nenhuma para D3D10. Seguir adiante montaria a mesma
                // instalacao que fechava o Just Cause 2 ao criar o device — cadeia verde, jogo
                // morto. Parar aqui, com o motivo, e o unico resultado honesto.
                if (precisaDxvkD3d10)
                    return new Result(false, L.T("Dlss5_Blocked_D3d10"), steps, manual);
            }
        }

        // O proxy do ReShade foi posto de lado NESTA passada? E a unica mudanca que a rota DXVK
        // faz na pasta antes de existir; se a implantacao falhar depois, ele volta.
        var proxyGuardado = false;
        if (trocaTradutor)
        {
            if (usarDxvk)
            {
                // indo para o DXVK: a camada e registrada adiante; aqui o dgVoodoo sai...
                //
                // Num jogo D3D10 ele sai pelo motivo oposto ao do D3D9: nao disputa o nome com
                // ninguem, simplesmente nunca e chamado — e um D3D9.dll que o Just Cause 2
                // importa como fallback e que o dgVoodoo tentaria inicializar em vao.
                if (DgVoodooService.IsDeployed(targetDir))
                {
                    DgVoodooService.Remove(targetDir);
                    Step(L.T(precisaDxvkD3d10 ? "Dlss5_Step_DgVoodooRemovedD3d10" : "Dlss5_Step_SwitchedToDxvk"));
                }
                // ...e o proxy dxgi.dll do ReShade tambem, que era do caminho D3D11.
                //
                // Deixa-lo seria pior que lixo: o DXVK usa DXGI por dentro, entao ele CARREGARIA
                // esse proxy — e o ReShade entraria duas vezes no mesmo processo, uma pela camada
                // e outra pelo proxy. Carga dupla e a receita conhecida de 0xc0000005.
                var proxy = Path.Combine(targetDir, "dxgi.dll");
                var ehNosso = false;
                if (File.Exists(proxy))
                {
                    try
                    {
                        ehNosso = System.Diagnostics.FileVersionInfo.GetVersionInfo(proxy)
                            .ProductName?.Contains("ReShade", StringComparison.OrdinalIgnoreCase) == true;
                    }
                    catch { }
                }
                if (ehNosso)
                {
                    try
                    {
                        var guardado = proxy + ".pre-dxvk";
                        if (File.Exists(guardado)) File.Delete(proxy);
                        else File.Move(proxy, guardado);
                        proxyGuardado = true;
                        Step(L.T("Dlss5_Step_ProxyRemovedForVulkan"));
                    }
                    catch (Exception ex) { Log.Warn($"dxvk: nao consegui tirar o proxy dxgi.dll: {ex.Message}"); }
                }
            }
            else if (DxvkService.IsDeployed(targetDir) && DxvkService.IsOurs(targetDir))
            {
                // voltando ao dgVoodoo: tira o DXVK E a camada Vulkan, que nao serve a D3D11.
                // So o DXVK que NOS pusemos (marcador, copia da biblioteca ou .pre-dxvk): um
                // d3d9.dll do DXVK que o usuario trouxe nao e nosso para apagar.
                DxvkService.Remove(targetDir);
                VulkanLayerService.Remove(targetDir);
                Step(L.T("Dlss5_Step_SwitchedToDgVoodoo"));
            }
        }
        if (usarDxvk)
        {
            try
            {
                if (precisaDxvkD3d10)
                {
                    DxvkService.DeployD3d10(targetDir, jogo64Bits: !ehJogo32Bits(exePath), progress);
                    Step(L.T("Dlss5_Step_DxvkD3d10", DxvkService.D3d10Version));
                }
                else
                {
                    DxvkService.Deploy(targetDir, progress);
                    Step(L.T("Dlss5_Step_Dxvk"));
                }
                precisaDgVoodoo = false;   // os dois disputam o d3d9.dll; so um pode ficar
            }
            catch (Exception ex)
            {
                Step(ex.Message);
                usarDxvk = false;
                // A pasta ja foi mexida: o proxy volta ao lugar, e `det` e relido do disco —
                // senao o passo do ReShade decide sobre um dxgi.dll que nao esta mais la.
                if (proxyGuardado) DevolverProxyPreDxvk(targetDir);
                det = NeuralUpliftService.Detect(installDir, targetDir, addonPath);
                // Sem o tradutor nao ha rota nenhuma para D3D10 (ver o catch do download).
                if (precisaDxvkD3d10)
                    return new Result(false, L.T("Dlss5_Blocked_D3d10"), steps, manual);
            }
        }

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

        static bool ehJogo32Bits(string? exe) =>
            exe is not null && PeUtils.Inspect(exe, readImports: false)?.Is64Bit == false;

        var alcancaD3d12 = exePath is null || LooksLikeD3D12(exePath) || precisaDgVoodoo;

        // HasDlss varre a pasta, e a partir da primeira instalacao a pasta contem runtimes que
        // NOS copiamos — o de Ray Reconstruction em todo caminho, o de Super Resolution no do
        // Feeder. Reinstalar entao lia "este jogo tem DLSS" sobre os proprios arquivos do
        // launcher, trocava o Feeder pela ponte e mandava o usuario ligar um DLSS que o jogo nao
        // tem. O Feeder ja implantado e a evidencia de que a decisao anterior foi essa.
        var feederJaAqui = FeederService.IsDeployed(targetDir);
        // O marcador `.renodx-ours` ja impede que a deteccao conte os runtimes que este launcher
        // copiou, entao anular por Feeder presente so fazia jogo COM DLSS parecer sem.
        var temDlssNativo = det.HasDlss;

        // A pasta inteira, e nao so o executavel escolhido: se o jogo tem um exe por API, os dois
        // caminhos sao instalados de uma vez e o usuario nao precisa saber que a escolha existia.
        var rota = RotearPasta(targetDir, exePath, temDlssNativo);
        var ehVulkan = rota.EhVulkan;
        // Os dois caminhos ao mesmo tempo e uma instalacao INTENCIONAL, e nao o residuo de uma
        // troca mal feita. A marca diz isso para quem ler a pasta depois -- o scanner de
        // conflitos, e o proximo reinstalar.
        var multiApi = rota.Ponte && rota.Feeder;
        var upscaler = rota.Upscaler;
        var precisaPonte = rota.Ponte;
        var precisaOpti = rota.OptiScaler;
        var precisaFeeder = rota.Feeder;

        // Sem DLSS, sem OptiScaler e sem Feeder aplicavel, nao ha o que fazer — e so aqui que o
        // bloqueio antigo ainda vale. Antes ele valia para todo jogo sem DLSS.
        if (!temDlssNativo && !precisaOpti && !precisaFeeder)
        {
            // Um jogo D3D10 normalmente nao chega aqui: desde a 1.70 ele vai pelo DXVK (acima) e
            // o Feeder o aceita. Se chegou, o tradutor falhou e o bloqueio ja foi devolvido la —
            // esta linha e so a rede de seguranca, com a mensagem que diz isso em vez da generica
            // ("nao traz runtime de DLSS"), que mandaria procurar um download que nao existe.
            if (FeederService.RenderizaEmD3d10(exePath))
                return new Result(false, L.T("Dlss5_Blocked_D3d10"), steps, manual);

            return new Result(false, L.T("Dlss5_Blocked_NoDlss"), steps, manual);
        }

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
        // O DXVK TRANSFORMA o jogo em Vulkan, entao ele cai aqui como qualquer Vulkan nativo.
        // Sem isto o instalador tentava por um proxy d3d9.dll do ReShade numa pasta onde o
        // d3d9.dll ja pertence ao DXVK, e parava no guard de conflito de proxy — corretamente,
        // porque dois donos do mesmo nome e o mesmo que nenhum.
        var precisaCamadaVulkan = usarDxvk || (!precisaDgVoodoo && ehVulkan);
        if (precisaCamadaVulkan)
        {
            var bits64 = exePath is null
                         || PeUtils.Inspect(exePath, readImports: false)?.Is64Bit != false;
            if (VulkanLayerService.IsRegistered(targetDir, bits64))
            {
                Step(L.T("Dlss5_Step_VulkanLayer"));
                // Ja registrada nesta bitness — mas a outra pode ter ficado sem entrada quando o
                // ReShade.json unico foi aposentado. O DeployAsync cuida disso; aqui, que o pula,
                // a garantia tem de ser chamada de proposito.
                VulkanLayerService.GarantirBitnessIrma(!bits64);
            }
            else if (await VulkanLayerService.DeployAsync(reshade, targetDir, bits64, progress))
                Step(L.T("Dlss5_Step_VulkanLayer"));
            else
            {
                // A bitness vai no texto: e o exe que decide qual no do registro falhou, e dizer
                // "32 bits" a quem instala um jogo de 64 manda procurar no lugar errado.
                Step(L.T("Dlss5_Step_VulkanLayerFailedBits", bits64 ? 64 : 32));
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
                // A remocao do Feeder vale para o caso EXCLUSIVO: um jogo com uma API so, em que
                // ter os dois e residuo de uma troca. Quando a pasta tem um exe por API, os dois
                // sao pedidos de proposito e tirar um desfaria metade do trabalho.
                if (!multiApi) FeederService.Remove(targetDir);
                NeuralUpliftService.DeployBridge(targetDir, progress);
                Step(L.T("Dlss5_Step_Bridge"));
                // Com os dois caminhos instalados nao ha mais executavel "certo" para abrir — que
                // era o unico motivo deste aviso existir. Ele so vale quando so a Ponte foi
                // instalada e o jogo tem mais de um executavel.
                if (!multiApi) manual.Add(L.T("Dlss5_Manual_UseDx11Exe"));
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
                // Exclusivos quando o jogo tem uma API so — e uma pasta podia ter as duas por
                // historico: o Sonic ficou assim depois de o launcher ter lido, por engano, um
                // runtime nosso como sendo do jogo e ter escolhido a ponte. Num jogo com um exe
                // por API a convivencia e o objetivo, e nao o defeito.
                if (!multiApi) NeuralUpliftService.RemoveBridge(targetDir);
                FeederService.Deploy(targetDir, progress, trocarDlss1);
                // A instrucao vai junto do resultado: sem desligar o DLSS do proprio jogo, ele
                // chama a geracao 1.0 num arquivo que agora responde outra API, e morre ao
                // terminar de carregar um save. Ver FeederService.DeploySuperResolution.
                if (trocarDlss1) manual.Add(L.T("Feeder_Dlss1_TurnOffInGame"));
                FeederService.Configure(targetDir, iniPath, progress);
                // O Feeder resolve o addon de NR por nome literal; sem esta copia ele entrega
                // frames com o pass sem quem o dirija, e diz isso so no proprio log.
                //
                // So quando o pass roda NO JOGO. Num jogo de 32 bits ele roda no host64\, que
                // recebe o nome certo por DeployForHost64 — e na primeira instalacao o host64\
                // ainda nao existe, entao o guard de dentro nao segurava: o addon de 64 bits era
                // renomeado na raiz do jogo de 32, o DeployBits32Async so tira o nome generico, e
                // o ReShade de 32 bits tentava carregar um PE de 64 no DllMain a cada abertura
                // (error code 193).
                if (!precisaHost64) NeuralUpliftService.GarantirNomeDoFeeder(targetDir, iniPath, progress);
                FeederService.AjustarAlocacao(targetDir, progress);
                Step(L.T("Dlss5_Step_Feeder"));

                // Ponte E Feeder na mesma pasta, de proposito, porque o jogo tem um executavel
                // por API. A marca separa isso do caso que o scanner de conflitos deve acusar:
                // duas metades que ficaram juntas por historico. Sem ela, a instalacao correta
                // seria reportada como defeito na propria tela que acabou de faze-la.
                if (multiApi)
                {
                    try
                    {
                        File.WriteAllText(Path.Combine(targetDir, MarcaMultiApi),
                                          DateTime.UtcNow.ToString("o"));
                        Step(L.T("Dlss5_Step_MultiApi"));
                    }
                    catch (Exception ex) { Log.Warn($"marca multi-api: {ex.Message}"); }
                }

                if (precisaHost64)
                {
                    await FeederService.DeployBits32Async(targetDir, reshade, progress, ct);
                    // Instalacoes anteriores deixaram na raiz o addon de 64 bits ja renomeado
                    // (ver o GarantirNomeDoFeeder acima). O DeployBits32Async so conhece o nome
                    // generico; o renomeado, com a marca de que foi o launcher que o pos, sai
                    // aqui — e a linha de carga antecipada junto, senao o error 193 vira 126.
                    TirarAddon64RenomeadoDaRaiz(targetDir, iniPath);
                    // Sempre que o jogo apresenta por Vulkan — nativo ou traduzido pelo DXVK —
                    // o add-on oficial de 32 bits recusa tudo que nao seja D3D11; a linha e
                    // literal no fonte dele. Amarrar isto ao DXVK deixava o Vulkan nativo de 32
                    // bits com as metades oficiais: camada registrada, host64 no lugar, e o
                    // addon32 se desligando no primeiro device. As metades com transporte Vulkan
                    // sobrescrevem as oficiais que acabaram de ser copiadas; o host64 montado
                    // acima (ReShade, addon de NR, runtimes) continua valendo, porque so o
                    // executavel do host muda.
                    if (precisaCamadaVulkan)
                    {
                        FeederService.DeployBits32Vulkan(targetDir, progress);
                        // A camada de 32 bits ja foi registrada no passo 4, com a bitness do
                        // exe; aqui so se confere, e se repoe se aquele passo falhou. A mesma
                        // chave com bitness do passo 4: a antiga existe duas vezes no
                        // strings.json, com textos diferentes, e qual delas vence depende do
                        // gerador do .resx.
                        var okLayer = VulkanLayerService.IsRegistered(targetDir, jogo64Bits: false)
                                      || await VulkanLayerService.DeployAsync(
                                             reshade, targetDir, jogo64Bits: false, progress);
                        Step(okLayer ? L.T("Dlss5_Step_VulkanLayer32") : L.T("Dlss5_Step_VulkanLayerFailedBits", 32));
                    }
                    Step(L.T("Dlss5_Step_Host64"));
                    // A janela do auxiliar aparece junto com o jogo na primeira vez. Sem aviso,
                    // isso parece coisa estranha se abrindo sozinha.
                    manual.Add(L.T("Dlss5_Manual_Host64"));
                }
                // Duas coisas que nenhum instalador resolve, e que decidem se a pessoa vai achar
                // que funcionou: nao ha ganho de FPS (e DLAA), e MSAA/SSAA do jogo precisa sair.
                manual.Add(L.T("Dlss5_Manual_FeederNoFps"));
                manual.Add(L.T("Dlss5_Manual_FeederMsaa"));
                // Traduzido, o renderizador D3D10 do jogo nao existe mais — e o que dependia
                // dele some das opcoes. No Just Cause 2 sao o Bokeh e a agua por GPU (CUDA com
                // interop D3D10). Dizer antes, senao parece que a instalacao quebrou o jogo.
                if (precisaDxvkD3d10) manual.Add(L.T("Dlss5_Manual_D3d10Dxvk"));
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
    /// Devolve o dxgi.dll do ReShade que a rota DXVK pos de lado, quando a rota nao se
    /// concretizou. Sem isto a pasta ficava sem proxy e sem camada — o ReShade nao entrava por
    /// lugar nenhum, com a cadeia inteira dizendo que sim.
    /// </summary>
    private static void DevolverProxyPreDxvk(string targetDir)
    {
        var proxy = Path.Combine(targetDir, "dxgi.dll");
        var guardado = proxy + ".pre-dxvk";
        try
        {
            if (File.Exists(proxy) || !File.Exists(guardado)) return;
            File.Move(guardado, proxy);
            Log.Info($"dxvk: proxy dxgi.dll devolvido em {targetDir}");
        }
        catch (Exception ex) { Log.Warn($"dxvk: nao consegui devolver o proxy dxgi.dll: {ex.Message}"); }
    }

    /// <summary>O nome pelo qual o Feeder procura o addon de NR, e que o host64 recebe por
    /// DeployForHost64. Na raiz de um jogo de 32 bits ele nunca deveria existir.</summary>
    private const string AddonNomeDoFeeder = "renodx-dlss5.addon64";

    /// <summary>
    /// Tira da raiz de um jogo de 32 bits o addon de 64 bits que versoes anteriores renomearam
    /// ali, e a linha de carga antecipada que apontava para ele.
    ///
    /// So o que foi o launcher que renomeou: a marca .renodx-ours decide, igual em todo o resto.
    /// Um renodx-dlss5.addon64 posto a mao pelo usuario nao e nosso para apagar — e nesse caso a
    /// linha do ini tambem fica. A linha so sai quando o arquivo nao esta mais la, porque ai ela
    /// e um LoadFromDllMain para um modulo inexistente, que o ReShade responde com error 126.
    /// </summary>
    private static void TirarAddon64RenomeadoDaRaiz(string targetDir, string iniPath)
    {
        var addon = Path.Combine(targetDir, AddonNomeDoFeeder);
        var marca = addon + ".renodx-ours";
        try
        {
            if (File.Exists(addon) && File.Exists(marca))
            {
                File.Delete(addon);
                File.Delete(marca);
                Log.Info($"neural: addon de 64 bits renomeado tirado da raiz de 32 bits em {targetDir}");
            }
            if (File.Exists(addon) || !File.Exists(iniPath)) return;

            var ini = new IniFile(iniPath);
            var lista = ini.Get("ADDON", "LoadFromDllMain", ignoreCase: true);
            if (lista is null) return;
            var entradas = lista.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var restantes = entradas
                .Where(e => !e.Equals(AddonNomeDoFeeder, StringComparison.OrdinalIgnoreCase)).ToList();
            if (restantes.Count == entradas.Length) return;
            if (restantes.Count == 0) ini.RemoveKey("ADDON", "LoadFromDllMain");
            else ini.Set("ADDON", "LoadFromDllMain", string.Join(',', restantes));
            ini.Save();
            Log.Info($"neural: LoadFromDllMain={AddonNomeDoFeeder} tirado de {iniPath}");
        }
        catch (Exception ex) { Log.Warn($"neural: addon renomeado na raiz de {targetDir}: {ex.Message}"); }
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

    /// <summary>A API grafica em que um executavel renderiza.</summary>
    public enum GraficosApi { Desconhecida, D3D9, D3D10, D3D11, D3D12, Vulkan }

    /// <summary>Marca de que a Ponte e o Feeder convivem nesta pasta DE PROPOSITO — o jogo tem um
    /// executavel por API e os dois caminhos foram instalados juntos.</summary>
    public const string MarcaMultiApi = ".dlss5-multi-api";

    /// <summary>Esta pasta foi instalada para mais de uma API?</summary>
    public static bool MultiApiInstalado(string targetDir) =>
        File.Exists(Path.Combine(targetDir, MarcaMultiApi));

    /// <summary>
    /// Em que API este executavel renderiza.
    ///
    /// A resposta ja era calculada em tres lugares diferentes, cada um respondendo a sua propria
    /// pergunta (cabe dgVoodoo? cabe a camada Vulkan? alcanca D3D12?) e nenhum dizendo o nome da
    /// API. Um jogo como o Baldur's Gate 3 tem um executavel por API na MESMA pasta, e sem esse
    /// nome nao ha como mostrar ao usuario o que ele esta escolhendo.
    ///
    /// A ordem das perguntas nao e arbitraria: <see cref="ReachesD3D12"/> responde "sim" no
    /// silencio — um binario que nao menciona API nenhuma e tratado como D3D12 para nao barrar
    /// sem base — entao ele tem de vir DEPOIS dos testes que exigem evidencia positiva.
    /// </summary>
    /// <param name="exigirEvidencia">
    /// Para EXIBIR, e nao para rotear. Sem isto, um binario que nao menciona API nenhuma volta
    /// como D3D12 — o padrao permissivo do <see cref="ReachesD3D12"/>, correto para decidir
    /// caminho (nao barrar sem base) e errado para escrever na tela. Foi assim que o painel de
    /// controle do dgVoodoo apareceu na Bayonetta como se fosse uma escolha de "DX12".
    /// </param>
    public static GraficosApi ApiDoExe(string? exePath, bool exigirEvidencia = false)
    {
        if (exePath is null || !File.Exists(exePath)) return GraficosApi.Desconhecida;

        // Ferramentas que o proprio launcher deixa na pasta. Nunca sao o jogo, e listar uma delas
        // como alternativa de API e oferecer ao usuario uma escolha que nao existe.
        if (EhFerramentaNossa(exePath)) return GraficosApi.Desconhecida;

        if (VulkanLayerService.Applies(exePath)) return GraficosApi.Vulkan;
        if (DgVoodooService.Applies(exePath)) return GraficosApi.D3D9;
        // Antes do D3D12, que responde "sim" no silencio: o Just Cause 2 nao menciona d3d11 nem
        // d3d12, entao ReachesD3D12 o daria como D3D12 — e a tela chamaria de "DX12" um jogo de
        // 2010 que vai pelo DXVK.
        if (FeederService.RenderizaEmD3d10(exePath)) return GraficosApi.D3D10;

        if (exigirEvidencia && !MencionaApiDirectX(exePath)) return GraficosApi.Desconhecida;

        if (ReachesD3D12(exePath)) return GraficosApi.D3D12;
        return GraficosApi.D3D11;
    }

    /// <summary>Executaveis que acompanham o que NOS implantamos — painel do dgVoodoo, instalador
    /// do ReShade. Um jogo nunca e um deles.</summary>
    private static bool EhFerramentaNossa(string exePath)
    {
        var nome = Path.GetFileNameWithoutExtension(exePath);
        return nome.StartsWith("dgVoodoo", StringComparison.OrdinalIgnoreCase)
               || nome.StartsWith("ReShade", StringComparison.OrdinalIgnoreCase)
               || nome.StartsWith("dlss5-feed", StringComparison.OrdinalIgnoreCase)
               || nome.Equals("OptiScaler", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>O binario da algum sinal positivo de renderizar em DirectX?</summary>
    private static bool MencionaApiDirectX(string exePath)
    {
        var pe = PeUtils.Inspect(exePath);
        if (pe is not null && pe.Imports.Any(i => i.Equals("dxgi.dll", StringComparison.OrdinalIgnoreCase)
                                                  || i.StartsWith("d3d1", StringComparison.OrdinalIgnoreCase)))
            return true;
        // Carga tardia: o nome do modulo precisa estar no binario para chegar ao LoadLibrary.
        return ContainsAscii(exePath, "d3d12") || ContainsAscii(exePath, "d3d11")
               || ContainsAscii(exePath, "d3d10") || ContainsAscii(exePath, "dxgi");
    }

    /// <summary>Nome curto da API, para a interface. "DX11", "Vulkan"...</summary>
    public static string ApiLabel(GraficosApi api) => api switch
    {
        GraficosApi.D3D9 => "DX9",
        GraficosApi.D3D10 => "DX10",
        GraficosApi.D3D11 => "DX11",
        GraficosApi.D3D12 => "DX12",
        GraficosApi.Vulkan => "Vulkan",
        _ => "?",
    };

    /// <summary>Qual das tres camadas serve este jogo, e por que. Ver <see cref="Rotear"/>.</summary>
    public readonly record struct Rota(bool Ponte, bool OptiScaler, bool Feeder,
                                       bool EhVulkan, string? Upscaler);

    /// <summary>
    /// A decisao de caminho, em um lugar so.
    ///
    /// Ela existia duas vezes — aqui e na tela de detalhe — e as duas copias sairam do lugar. A
    /// tela nao tinha o termo `!ehVulkan` da ponte, entao no Baldur's Gate 3 ela cobrava a Ponte
    /// de um jogo que o instalador tinha, corretamente, mandado para o Feeder. O elo ficava
    /// vermelho para sempre e o interruptor nao ligava, sem nada que o usuario pudesse clicar
    /// para resolver. Duas copias de uma regra de tres termos so vao divergir de novo; uma
    /// funcao nao pode.
    ///
    ///   D3D12                  -> nada no meio, o pass roda no device do jogo
    ///   DX11 + tem DLSS        -> ponte: um segundo device D3D12 reproduz o contrato do jogo
    ///   DX11 + NAO tem DLSS    -> Feeder: nao ha contrato para reproduzir, entao ele FABRICA um
    /// </summary>
    public static Rota Rotear(string targetDir, string? exePath, bool temDlssNativo, bool alcancaD3d12)
    {
        // A ponte e de DirectX 11 — ela engancha o device D3D11 do jogo para dar ao pass neural
        // um lugar onde rodar. Num jogo Vulkan nao ha device D3D11 nenhum: instala-la ali punha
        // um addon inerte na pasta e, pior, o passo manual mandava "abra pelo executavel de
        // DirectX 11", que no DOOM Eternal nao existe.
        var ehVulkan = VulkanLayerService.Applies(exePath);
        var ponte = temDlssNativo && !alcancaD3d12 && !ehVulkan;

        // Jogo com FSR ou XeSS proprio e sem DLSS: o OptiScaler redireciona o upscaler que ele ja
        // tem. Vem ANTES do Feeder na decisao, e por um motivo de qualidade, nao de gosto — o
        // jogo ja calcula motion vectors e depth corretos para o proprio upscaler, enquanto o
        // Feeder teria de reconstruir os dois por fora, com um shader. Dado do engine ganha de
        // dado fabricado sempre que existe.
        var upscaler = OptiScalerService.AchaUpscaler(targetDir);
        var opti = !temDlssNativo && !ehVulkan && upscaler is not null
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
        var feeder = (!temDlssNativo && !opti
                      && FeederService.Applies(exePath, temDlssNativo, alcancaD3d12))
                     || (ehVulkan && FeederService.Applies(exePath, false, true));

        return new Rota(ponte, opti, feeder, ehVulkan, upscaler);
    }

    /// <summary>
    /// A rota da PASTA: a uniao do que cada executavel dela precisa.
    ///
    /// Um jogo moderno pode trazer um executavel por API — o Baldur's Gate 3 tem um Vulkan e um
    /// DX11 no mesmo `bin`. Perguntar ao usuario qual ele joga e empurrar para ele uma decisao
    /// tecnica que nao e dele: ele quer o DLSS 5 funcionando, e nao saber que a Ponte engancha
    /// D3D11 e o Feeder importa memoria externa do VkDevice.
    ///
    /// Instalar os dois e possivel porque eles nao disputam arquivo nenhum: a Ponte e um addon
    /// proprio (`dlss5-dx11-bridge.addon64`), o Feeder e o addon neural sob outro nome mais os
    /// shaders em `reshade-shaders`. O que cada processo usa e decidido em tempo de execucao —
    /// num processo Vulkan a Ponte nao acha device D3D11 para enganchar e fica quieta.
    ///
    /// A evidencia de que addon fora do seu contexto e inofensivo esta na propria pasta do
    /// Baldur's Gate: o addon neural ja convive ali com a rota Feeder, em Vulkan, funcionando.
    /// </summary>
    public static Rota RotearPasta(string targetDir, string? exeEscolhido, bool temDlssNativo)
    {
        var uniao = Rotear(targetDir, exeEscolhido, temDlssNativo, ReachesD3D12(exeEscolhido));
        foreach (var f in ExesComApi(targetDir))
        {
            if (string.Equals(f, exeEscolhido, StringComparison.OrdinalIgnoreCase)) continue;
            var r = Rotear(targetDir, f, temDlssNativo, ReachesD3D12(f));
            uniao = uniao with
            {
                Ponte = uniao.Ponte || r.Ponte,
                Feeder = uniao.Feeder || r.Feeder,
                // O OptiScaler fica de fora da uniao: ele reescreve o upscaler do jogo, e dois
                // redirecionadores no mesmo lugar e o caso que o codigo ja trata como bug.
                Upscaler = uniao.Upscaler ?? r.Upscaler,
            };
        }
        return uniao;
    }

    /// <summary>Executaveis da pasta que dao sinal positivo de renderizar em alguma API.</summary>
    public static IEnumerable<string> ExesComApi(string targetDir)
    {
        List<string> achados = [];
        try
        {
            foreach (var f in Directory.EnumerateFiles(targetDir, "*.exe", SearchOption.TopDirectoryOnly)
                         .OrderByDescending(f => { try { return new FileInfo(f).Length; } catch { return 0L; } })
                         .Take(12))
                if (ApiDoExe(f, exigirEvidencia: true) != GraficosApi.Desconhecida)
                    achados.Add(f);
        }
        catch (Exception ex) { Log.Warn($"exes com api em {targetDir}: {ex.Message}"); }
        return achados;
    }

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
