using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RenoDXLauncher.Services;

/// <summary>
/// Pastas onde o jogo se foi e os nossos arquivos ficaram.
///
/// Desinstalar um jogo pela loja apaga os arquivos DELA. Tudo o que o launcher pos ali —
/// runtimes, add-ons, o proxy do ReShade, os backups — nao e dela, entao fica; e como a pasta
/// nao esvazia, a propria loja a deixa de pe. O resultado e uma pasta com centenas de megabytes
/// e nenhum jogo dentro, que ninguem vai procurar porque ninguem sabe que ela existe.
///
/// Medido nesta maquina: tres pastas, 1,2 GB, das quais ~900 MB eram tres copias do mesmo
/// runtime neural de 159 MB.
///
/// O criterio e conservador de proposito, porque o que esta em jogo e apagar arquivo: a pasta so
/// e sobra quando tem marca NOSSA e nenhum executavel. Um executavel qualquer ja a salva — jogo
/// que a loja esqueceu de registrar, port, repack — e o pior erro possivel aqui seria oferecer
/// apagar a pasta de um jogo que a pessoa ainda joga.
/// </summary>
public static class SobraScanner
{
    /// <summary>Arquivos que so existem ali porque o launcher os pos.</summary>
    private static readonly string[] Marcas =
    [
        "renodx-*.addon64", "renodx-*.addon32", "dlss5-*.addon64", "dlss5-*.addon32",
        "*.renodx-bak", "*.renodx-ours", "nvngx_dlssnr.dll",
    ];

    private static readonly EnumerationOptions Busca = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = true,
        MaxRecursionDepth = 4,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    /// <param name="Pasta">A pasta orfa.</param>
    /// <param name="Nome">O nome do jogo que estava ali, deduzido do nome da pasta.</param>
    /// <param name="Bytes">Quanto ela ocupa, para a pergunta valer a pena ser feita.</param>
    /// <param name="Nossos">Quantos arquivos sao reconhecidamente nossos.</param>
    public record Sobra(string Pasta, string Nome, long Bytes, int Nossos);

    /// <summary>
    /// Procura sobras nas bibliotecas de jogos conhecidas.
    /// </summary>
    /// <param name="pastasDeJogos">As pastas dos jogos que a varredura normal ENCONTROU. Uma
    /// pasta que aparece ali tem dono e nunca e sobra, por mais nossos arquivos que tenha.</param>
    public static List<Sobra> Procurar(IEnumerable<string> pastasDeJogos)
    {
        var vivas = new HashSet<string>(
            pastasDeJogos.Where(d => d is not null).Select(NormalizarPasta), StringComparer.OrdinalIgnoreCase);
        var achadas = new List<Sobra>();

        foreach (var common in BibliotecasDeJogos())
        {
            IEnumerable<string> filhas;
            try { filhas = Directory.EnumerateDirectories(common); }
            catch (Exception ex) { Log.Warn($"sobras em {common}: {ex.Message}"); continue; }

            foreach (var dir in filhas)
            {
                try
                {
                    if (vivas.Contains(NormalizarPasta(dir))) continue;
                    // Um executavel — qualquer um — e prova de que ainda ha jogo ali.
                    if (Directory.EnumerateFiles(dir, "*.exe", Busca).Any()) continue;

                    var nossos = Marcas.SelectMany(m =>
                    {
                        try { return Directory.EnumerateFiles(dir, m, Busca); }
                        catch { return []; }
                    }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (nossos.Count == 0) continue;

                    long bytes = 0;
                    try
                    {
                        foreach (var f in Directory.EnumerateFiles(dir, "*", Busca))
                            bytes += new FileInfo(f).Length;
                    }
                    catch (Exception ex) { Log.Warn($"sobras tamanho {dir}: {ex.Message}"); }

                    achadas.Add(new Sobra(dir, Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar)),
                                          bytes, nossos.Count));
                }
                catch (Exception ex) { Log.Warn($"sobras {dir}: {ex.Message}"); }
            }
        }
        return achadas;
    }

    /// <summary>
    /// As pastas `common` das bibliotecas Steam.
    ///
    /// So Steam por enquanto: e a unica loja em que a pasta sobrevive a desinstalacao com nome
    /// legivel e num lugar previsivel. As outras ou apagam a pasta inteira ou usam identificador
    /// no lugar do nome, e chutar ali sairia mais caro do que o problema.
    /// </summary>
    private static IEnumerable<string> BibliotecasDeJogos()
    {
        var steam = StoreScanners.SteamInstallPath;
        if (steam is null) yield break;
        var libs = new List<string> { steam };
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
        {
            string texto;
            try { texto = File.ReadAllText(vdf); }
            catch (Exception ex) { Log.Warn($"sobras libraryfolders: {ex.Message}"); texto = ""; }
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(texto, "\"path\"\\s*\"([^\"]+)\""))
                libs.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
        }
        foreach (var lib in libs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var common = Path.Combine(lib, "steamapps", "common");
            if (Directory.Exists(common)) yield return common;
        }
    }

    private static string NormalizarPasta(string caminho)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(caminho)); }
        catch { return caminho; }
    }

    /// <summary>
    /// Apaga a pasta inteira.
    ///
    /// Inteira, e nao so os nossos arquivos: o que sobra depois de tirar os nossos e o resto de
    /// um jogo desinstalado — save antigo, config, lixo de outro mod — numa pasta que a loja nao
    /// conhece mais. Deixar a casca vazia so adiaria a mesma pergunta.
    ///
    /// Quem chama pergunta antes. Aqui nao ha confirmacao nenhuma: e uma funcao que apaga.
    /// </summary>
    public static void Apagar(string pasta)
    {
        Directory.Delete(pasta, recursive: true);
        Log.Info($"sobra apagada: {pasta}");
    }
}
