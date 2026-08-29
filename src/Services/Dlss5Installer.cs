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

        var det = NeuralUpliftService.Detect(installDir, targetDir, addonPath);
        if (!det.HasDlss) return new Result(false, L.T("Dlss5_Blocked_NoDlss"), steps, manual);

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
        }
        catch (Exception ex)
        {
            Step(ex.Message);
            return new Result(false, ex.Message, steps, manual);
        }

        // What no installer can do: the feature reads the game's own DLSS buffers, so DLSS has to
        // be on in the game's settings. Saying so is the difference between "it does not work" and
        // "there is one switch left".
        manual.Add(L.T("Dlss5_Manual_EnableDlss"));
        manual.Add(L.T("Dlss5_Manual_Overlay"));

        var applied = NeuralUpliftService.IsApplied(targetDir, iniPath, addonPath);
        return new Result(applied, applied ? null : L.T("Dlss5_Blocked_Unknown"), steps, manual);
    }

    /// <summary>The library sweep is slow and noisy; it only matters when the runtime is absent.</summary>
    private static void AutoDiscoverRuntimeQuiet(IEnumerable<string> gameDirs, IProgress<string>? progress)
    {
        try { NeuralUpliftService.AutoDiscoverRuntime(gameDirs, progress); }
        catch (Exception ex) { Log.Warn($"dlss5 runtime discovery: {ex.Message}"); }
    }
}
