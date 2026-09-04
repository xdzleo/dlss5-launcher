using System.IO;

namespace RenoDXLauncher.Services;

/// <summary>
/// Detects anti-cheat by the FILES on disk, independent of whether the wiki note happens to
/// mention it. Injecting an unsigned proxy DLL into an EAC/BattlEye title can get the user's
/// account permanently banned — the only irreversible damage this app can cause — so this must
/// not depend on free-text parsing.
/// </summary>
public static class AntiCheatScanner
{
    private static readonly (string Needle, string Name)[] Signatures =
    {
        ("easyanticheat", "Easy Anti-Cheat"),
        ("eac_launcher", "Easy Anti-Cheat"),
        ("battleye", "BattlEye"),
        ("beservice", "BattlEye"),
        ("beclient", "BattlEye"),
        ("vgk.sys", "Vanguard"),
        ("vanguard", "Vanguard"),
    };

    /// <summary>Name of the anti-cheat found, or null. Scans the install root (the
    /// EasyAntiCheat\ folder lives there, not next to the shipping exe) and the deploy dir.</summary>
    /// <summary>
    /// O que ja foi respondido para esta pasta.
    ///
    /// A varredura desce tres niveis na pasta do jogo e olha o NOME de cada entrada. Num jogo
    /// pequeno isso e instantaneo; no Cyberpunk 2077, com dezenas de milhares de arquivos, foram
    /// medidos ~400 ms — e ela rodava a cada clique no interruptor, no meio do caminho entre o
    /// clique e a tela.
    ///
    /// A resposta nao muda enquanto o launcher esta aberto: ninguem instala um anti-cheat no jogo
    /// entre abrir o cartao e clicar em instalar. E se instalasse, a proxima abertura pega.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> Lembrado = new();

    /// <summary>Faz a varredura ANTES de ela ser necessaria, do lado de fora do caminho do
    /// clique. Chamado quando o cartao do jogo abre, que e quando ha tempo de sobra.</summary>
    public static void Preaquecer(string? installDir, string? targetDir)
    {
        try { Detect(installDir, targetDir); }
        catch (Exception ex) { Log.Warn($"anti-cheat preaquecer: {ex.Message}"); }
    }

    public static string? Detect(string? installDir, string? targetDir)
    {
        var chave = (installDir ?? "") + "|" + (targetDir ?? "");
        if (Lembrado.TryGetValue(chave, out var lembrado)) return lembrado;
        var achado = Varrer(installDir, targetDir);
        Lembrado[chave] = achado;
        return achado;
    }

    private static string? Varrer(string? installDir, string? targetDir)
    {
        foreach (var root in new[] { installDir, targetDir })
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
            try
            {
                var options = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = true,
                    MaxRecursionDepth = 3,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                };
                foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", options))
                {
                    var name = Path.GetFileName(entry).ToLowerInvariant();
                    foreach (var (needle, display) in Signatures)
                        if (name.Contains(needle, StringComparison.Ordinal))
                            return display;
                }
            }
            catch (Exception ex) { Log.Warn($"anti-cheat scan {root}: {ex.Message}"); }
        }
        return null;
    }
}
