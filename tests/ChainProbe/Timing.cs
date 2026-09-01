using System.Diagnostics;
using System.IO;
using RenoDXLauncher.Models;
using RenoDXLauncher.Services;

namespace ChainProbe;

/// <summary>
/// Mede quanto custa cada peca do carregamento de detalhe de um jogo.
///
/// Existe porque "selecionar um jogo demora" nao diz o que otimizar: sao seis etapas em
/// sequencia, todas com I/O, e paralelizar as erradas nao muda nada. Isto aponta a cara.
/// </summary>
public static class Timing
{
    public static void Run(string dir, string? exe)
    {
        Console.WriteLine($"pasta : {dir}");
        Console.WriteLine($"exe   : {exe ?? "(nenhum)"}");
        Console.WriteLine();

        var total = Stopwatch.StartNew();
        Medir("ExeLocator.FindCandidates", () =>
        {
            var g = new GameInfo { Name = Path.GetFileName(dir), InstallDir = dir, Store = GameStore.Folder };
            _ = ExeLocator.FindCandidates(g, null).ToList();
        });
        Medir("NeuralUpliftService.Detect", () => _ = NeuralUpliftService.Detect(dir, dir, null));
        Medir("NeuralUpliftService.ProbeHost", () => _ = NeuralUpliftService.ProbeHost());
        Medir("AddonService.GetState", () => _ = AddonService.GetState(dir, exe));
        Medir("FeederService.IsDeployed", () => _ = FeederService.IsDeployed(dir));
        Medir("DgVoodooService.IsDeployed", () => _ = DgVoodooService.IsDeployed(dir));
        Medir("DxvkService.IsDeployed", () => _ = DxvkService.IsDeployed(dir));
        Medir("VulkanLayerService.IsRegistered", () => _ = VulkanLayerService.IsRegistered(dir, false));
        Medir("DlssRuntimeService.DetectInGame", () => { try { _ = DlssRuntimeService.DetectInGame(dir).ToList(); } catch { } });
        Medir("AntiCheatScanner.Detect", () => { try { _ = AntiCheatScanner.Detect(dir, dir); } catch { } });
        if (exe is not null)
            Medir("PeUtils.Inspect (com imports)", () => _ = PeUtils.Inspect(exe));
        total.Stop();
        Console.WriteLine();
        Console.WriteLine($"  {"TOTAL",-34} {total.ElapsedMilliseconds,6} ms");

        // Segunda passada: e o que o usuario sente ao voltar para um jogo ja visto. A varredura
        // de .exe e cacheada, entao aqui ela deve custar perto de zero.
        Console.WriteLine();
        Console.WriteLine("  --- segunda selecao do mesmo jogo (com cache) ---");
        var t2 = Stopwatch.StartNew();
        Medir("ExeLocator.FindCandidates", () =>
        {
            var g = new GameInfo { Name = Path.GetFileName(dir), InstallDir = dir, Store = GameStore.Folder };
            _ = ExeLocator.FindCandidates(g, null).ToList();
        });
        Medir("NeuralUpliftService.Detect", () => _ = NeuralUpliftService.Detect(dir, dir, null));
        Medir("AddonService.GetState", () => _ = AddonService.GetState(dir, exe));
        t2.Stop();
        Console.WriteLine($"  {"TOTAL (2a vez)",-34} {t2.ElapsedMilliseconds,6} ms");
    }

    private static void Medir(string nome, Action acao)
    {
        var sw = Stopwatch.StartNew();
        try { acao(); } catch (Exception ex) { Console.WriteLine($"  {nome,-34} erro: {ex.GetType().Name}"); return; }
        sw.Stop();
        var marca = sw.ElapsedMilliseconds >= 100 ? "  <-- caro" : "";
        Console.WriteLine($"  {nome,-34} {sw.ElapsedMilliseconds,6} ms{marca}");
    }
}
