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
        var alcancaD3d12 = exePath is null || LooksLikeD3D12(exePath);

        // HasDlss varre a pasta, e a partir da primeira instalacao a pasta contem runtimes que
        // NOS copiamos — o de Ray Reconstruction em todo caminho, o de Super Resolution no do
        // Feeder. Reinstalar entao lia "este jogo tem DLSS" sobre os proprios arquivos do
        // launcher, trocava o Feeder pela ponte e mandava o usuario ligar um DLSS que o jogo nao
        // tem. O Feeder ja implantado e a evidencia de que a decisao anterior foi essa.
        var feederJaAqui = FeederService.IsDeployed(targetDir);
        var temDlssNativo = det.HasDlss && !feederJaAqui;

        var precisaPonte = temDlssNativo && !alcancaD3d12;
        var precisaFeeder = !temDlssNativo && FeederService.Applies(exePath, temDlssNativo, alcancaD3d12);

        // Sem DLSS e sem Feeder aplicavel, nao ha o que fazer — e so aqui que o bloqueio antigo
        // ainda vale. Antes ele valia para todo jogo sem DLSS, inclusive os que o Feeder atende.
        if (!temDlssNativo && !precisaFeeder)
            return new Result(false, L.T("Dlss5_Blocked_NoDlss"), steps, manual);

        if (precisaPonte)
        {
            try { await NeuralUpliftService.FetchBridgeAsync(progress, ct); }
            catch (Exception ex) { Step(ex.Message); }
            if (!File.Exists(NeuralUpliftService.LibraryBridge))
                return new Result(false, L.T("Dlss5_Blocked_NoBridge"), steps, manual);
        }

        if (precisaFeeder)
        {
            try { await FeederService.FetchAsync(progress, ct); }
            catch (Exception ex) { Step(ex.Message); }
            if (!FeederService.InLibrary)
                return new Result(false, L.T("Dlss5_Blocked_NoFeeder"), steps, manual);
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

        // 4. ReShade. Without it nothing loads the addon at all.
        if (det.ReShadeDllName is null)
        {
            if (exePath is null) return new Result(false, L.T("Dlss5_Blocked_NoExe"), steps, manual);
            var dep = await reshade.DeployAsync(targetDir, exePath,
                rhi?.GraphicsApi(game.Name), rhi?.DllNameOverride(game.Name), progress);
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
            if (precisaFeeder)
            {
                // Exclusivos, e o autor do Feeder e explicito quanto a isso. Uma pasta pode ter
                // as duas por historico — o Sonic ficou assim depois de o launcher ter lido, por
                // engano, um runtime nosso como sendo do jogo e ter escolhido a ponte.
                NeuralUpliftService.RemoveBridge(targetDir);
                FeederService.Deploy(targetDir, progress);
                FeederService.Configure(targetDir, iniPath, progress);
                Step(L.T("Dlss5_Step_Feeder"));
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
