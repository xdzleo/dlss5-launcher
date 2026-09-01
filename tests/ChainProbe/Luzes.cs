using System.IO;
using RenoDXLauncher.Services;

namespace ChainProbe;

/// <summary>
/// Reproduz o que a lista de jogos faz na abertura, e diz de que cor cada bolinha de DLSS 5
/// nasceria — ANTES de o usuario clicar em qualquer jogo.
///
/// Existe porque a resposta "as bolinhas so acendem quando clico" ja foi respondida errado uma
/// vez: a correcao anterior pos RefreshLuzes em ApplyDetected, o que estava certo, mas
/// ApplyDetected nao era chamado para a maioria dos jogos. Deduzir de novo daria no mesmo. Isto
/// roda o caminho de verdade e conta.
/// </summary>
public static class Luzes
{
    public static void Run(string[] raizes)
    {
        var verdes = 0;
        var vermelhas = 0;
        var total = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        foreach (var raiz in raizes)
        {
            if (!Directory.Exists(raiz)) continue;
            foreach (var jogo in Directory.EnumerateDirectories(raiz))
            {
                total++;
                var (dir, aceso) = Avaliar(jogo);
                if (aceso) { verdes++; Console.WriteLine($"  VERDE     {Path.GetFileName(jogo)}"); }
                else if (dir is not null) { vermelhas++; Console.WriteLine($"  vermelha  {Path.GetFileName(jogo)}  (instalado, desligado)"); }
            }
        }

        sw.Stop();
        Console.WriteLine();
        Console.WriteLine($"  {total} jogos varridos em {sw.ElapsedMilliseconds} ms");
        Console.WriteLine($"  verdes na abertura: {verdes}   vermelhas com instalacao: {vermelhas}");
    }

    /// <summary>O mesmo que GameItemVm.DetectExistingInstall + LerDlss5Ligado fazem.</summary>
    private static (string? Dir, bool Aceso) Avaliar(string installDir)
    {
        try
        {
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                MaxRecursionDepth = 5,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };
            var addon = Directory.EnumerateFiles(installDir, "renodx-*.addon*",
                                                 SearchOption.TopDirectoryOnly).FirstOrDefault()
                        ?? Directory.EnumerateFiles(installDir, "renodx-*.addon*", options)
                            .FirstOrDefault(f => !EhAndaime(f));
            if (addon is null) return (null, false);

            var dir = Path.GetDirectoryName(addon)!;
            var ini = Path.Combine(dir, "ReShade.ini");
            var aceso = File.Exists(ini) && NeuralUpliftService.IsApplied(dir, ini, addon);
            return (dir, aceso);
        }
        catch { return (null, false); }
    }

    private static bool EhAndaime(string arquivo)
    {
        var pasta = Path.GetFileName(Path.GetDirectoryName(arquivo)) ?? "";
        return pasta.Equals(FeederService.Host64Dir, StringComparison.OrdinalIgnoreCase)
               || pasta.Equals("_mods_desligados", StringComparison.OrdinalIgnoreCase)
               || pasta.Equals("vklayer", StringComparison.OrdinalIgnoreCase);
    }
}
