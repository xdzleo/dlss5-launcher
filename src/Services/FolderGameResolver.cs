using System.IO;
using System.Text.RegularExpressions;
using RenoDXLauncher.Models;

namespace RenoDXLauncher.Services;

/// <summary>
/// Works out WHICH GAME a hand-picked folder holds.
///
/// A folder added by hand rarely carries the game's title. The folder the user actually points at
/// is usually the one with the exe in it — "…\007.First.Light-InsaneRamZes\Retail" — so the name
/// is "Retail", which matches nothing. The parent is not much better: a download folder carries
/// release decorations ("-InsaneRamZes", "[Repack]", "v1.2.3") that no catalog title has.
///
/// So instead of one name, this offers several: the folder, its parents while the folder name is a
/// generic container, each with the decorations stripped, and — the strongest signal — the render
/// exe's own file name. "007FirstLight.exe" normalizes to exactly the catalog's "007 First Light".
/// </summary>
public static partial class FolderGameResolver
{
    /// <summary>Folder names that describe a LAYOUT, not a game.</summary>
    private static readonly HashSet<string> Containers = new(StringComparer.OrdinalIgnoreCase)
    {
        "retail", "bin", "bin64", "binaries", "win64", "win32", "x64", "x86", "game", "games",
        "build", "release", "client", "data", "app", "content", "shipping", "pc", "windows",
        "wingdk", "steamapps", "common", "launcher", "files",
    };

    /// <summary>
    /// Pastas que guardam MUITAS coisas sem relacao entre si.
    ///
    /// Uma dessas nunca e um jogo, e o estrago de trata-la como um so aparece de um jeito
    /// desconcertante: sem um nome de pasta utilizavel, o resolvedor cai no ProductName de algum
    /// executavel la dentro. `C:\Users\Admin\Downloads` virou um jogo chamado "WinBox" — o nome de
    /// uma ferramenta de rede que por acaso estava baixada ali.
    ///
    /// Separado de <see cref="Containers"/> porque a resposta e outra: um "bin" ainda faz parte de
    /// um jogo e o resolvedor deve SUBIR a arvore; uma "Downloads" nao faz parte de nada.
    /// </summary>
    private static readonly HashSet<string> PastasDoUsuario = new(StringComparer.OrdinalIgnoreCase)
    {
        "downloads", "desktop", "documents", "documentos", "downloads (2)", "temp", "tmp",
        "onedrive", "dropbox", "google drive", "music", "pictures", "videos", "public",
        "program files", "program files (x86)", "programdata", "users", "appdata", "windows",
    };

    /// <summary>
    /// As pastas manuais que de fato viram jogos.
    ///
    /// Existe para ser o UNICO lugar que aplica esse filtro. A interface e o CLI montam a lista de
    /// jogos cada um por sua conta, e quando a guarda de deposito foi para so um dos dois o WinBox
    /// sumiu da grade e continuou aparecendo no `list` — a mesma divergencia entre duas copias que
    /// ja tinha travado o interruptor no Baldur's Gate 3.
    /// </summary>
    public static IEnumerable<GameInfo> ResolverPastasManuais(IEnumerable<string> dirs,
                                                             IReadOnlyList<CatalogEntry> catalog)
    {
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            if (EhDeposito(dir))
            {
                Log.Warn($"pasta manual ignorada (e um deposito, nao um jogo): {dir}");
                continue;
            }
            yield return Resolve(dir, catalog);
        }
    }

    /// <summary>
    /// Esta pasta e um deposito, e nao um jogo?
    ///
    /// Vale tambem para a raiz de uma unidade: adicionar `D:\` como "um jogo" tem o mesmo problema,
    /// e e um engano facil de cometer no seletor de pastas.
    /// </summary>
    public static bool EhDeposito(string dir)
    {
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
            // Raiz de unidade: "D:" depois do trim, ou o proprio caminho raiz.
            if (Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar)
                    .Equals(full, StringComparison.OrdinalIgnoreCase) == true) return true;
            return PastasDoUsuario.Contains(Path.GetFileName(full));
        }
        catch { return false; }
    }

    [GeneratedRegex(@"[\[\(\{][^\]\)\}]*[\]\)\}]")]
    private static partial Regex BracketTagRegex();

    [GeneratedRegex(@"[-_. ]v?\d+(\.\d+){1,3}([-_. ].*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionTailRegex();

    /// <summary>
    /// Sufixo de grupo de release ("-InsaneRamZes", "-CODEX").
    ///
    /// O que distingue um grupo de uma palavra do titulo e a CAIXA: grupos sao all-caps (CODEX,
    /// FLT, RELOADED) ou tem maiuscula interna (InsaneRamZes); palavra de titulo depois de hifen e
    /// so capitalizada (Spider-<b>Man</b>, Half-<b>Life</b>, Call-of-<b>Duty</b>). Um corte cego de
    /// "-qualquer coisa" destruia esses titulos, e pior: "Dishonored-2" virava "Dishonored" e
    /// casava com o jogo errado — exatamente o que <see cref="MatchService.FindMatch"/> existe para
    /// impedir. Numeral romano fica de fora pelo mesmo motivo ("Control-III").
    /// </summary>
    [GeneratedRegex(@"-(?![IVXivx]+$)([A-Z]{2,}|[A-Za-z]*[a-z][A-Z][A-Za-z]*)$")]
    private static partial Regex GroupSuffixRegex();

    [GeneratedRegex(@"\b(repack|multi\d*|proper|readnfo|incl|dlc|update|build\s*\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReleaseWordRegex();

    /// <summary>Drop release-folder decorations. The result is only ever used as an EXTRA
    /// candidate, never as a replacement — "Half-Life" would lose its second half here, and the
    /// unstripped name is always tried first.</summary>
    public static string StripReleaseTags(string name)
    {
        var s = BracketTagRegex().Replace(name, " ");
        s = VersionTailRegex().Replace(s, " ");
        s = ReleaseWordRegex().Replace(s, " ");
        s = GroupSuffixRegex().Replace(s.Trim(), " ");
        return Regex.Replace(s, @"\s+", " ").Trim(' ', '.', '-', '_');
    }

    /// <summary>Names worth trying for this folder, best signal first.</summary>
    public static List<string> CandidateNames(string dir, string? exePath = null)
    {
        var names = new List<string>();
        void Offer(string? n)
        {
            if (string.IsNullOrWhiteSpace(n)) return;
            if (!names.Contains(n, StringComparer.OrdinalIgnoreCase)) names.Add(n);
            var stripped = StripReleaseTags(n);
            if (stripped.Length >= 3 && !names.Contains(stripped, StringComparer.OrdinalIgnoreCase))
                names.Add(stripped);
        }

        var current = dir.TrimEnd('\\', '/');
        var folder = Path.GetFileName(current);
        Offer(folder);

        // climb out of layout folders: …\<Game>\Retail and …\<Game>\Binaries\Win64 both end up
        // pointing at the folder that actually carries the title
        int hops = 0;
        while (hops++ < 3 && folder.Length > 0 && Containers.Contains(folder))
        {
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent)) break;
            current = parent.TrimEnd('\\', '/');
            folder = Path.GetFileName(current);
            if (folder.Length == 0) break;
            Offer(folder);
        }

        // the exe is the most reliable of all: it is named by the developer, not by whoever
        // packed the folder
        if (exePath != null)
        {
            Offer(Path.GetFileNameWithoutExtension(exePath));
            try
            {
                var product = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath).ProductName;
                Offer(product);
            }
            catch { }
        }
        return names;
    }

    /// <summary>Build a GameInfo for a hand-picked folder, naming it after whichever candidate the
    /// catalog recognizes. Falls back to the folder's own name so the entry still shows up.</summary>
    public static GameInfo Resolve(string dir, IReadOnlyList<CatalogEntry> catalog)
    {
        var folderName = Path.GetFileName(dir.TrimEnd('\\', '/'));
        string? exe = null;
        try
        {
            var probe = new GameInfo { Name = folderName, InstallDir = dir, Store = GameStore.Manual };
            exe = ExeLocator.FindCandidates(probe, null).FirstOrDefault();
        }
        catch (Exception ex) { Log.Warn($"resolver exe de {dir}: {ex.Message}"); }

        foreach (var candidate in CandidateNames(dir, exe))
        {
            var probe = new GameInfo { Name = candidate, InstallDir = dir, Store = GameStore.Manual };
            if (MatchService.FindMatch(probe, catalog) is { } hit)
            {
                Log.Info($"pasta manual {dir}: reconhecida como \"{hit.GameName}\" por \"{candidate}\"");
                // show the catalog's spelling, not "Retail" or "007.First.Light-InsaneRamZes"
                return new GameInfo { Name = hit.GameName, InstallDir = dir, Store = GameStore.Manual };
            }
        }
        // Sem entrada no catalogo o jogo ainda aparece — e continua util, porque ReShade e o addon
        // neural generico nao dependem de mod proprio. So o NOME precisa ser apresentavel: a pasta
        // costuma ser a pior fonte possivel ("Mortal.Shell.II-InsaneRamZes", "Retail").
        return new GameInfo { Name = DisplayName(dir, exe) ?? folderName, InstallDir = dir,
                              Store = GameStore.Manual };
    }

    /// <summary>
    /// Melhor nome legivel para uma pasta que o catalogo nao reconhece.
    ///
    /// O ProductName do executavel vem primeiro porque e o unico candidato escrito pelo
    /// DESENVOLVEDOR — nome de pasta e escrito por quem empacotou, e carrega separador estranho,
    /// versao e sufixo de grupo. Depois dele vem o nome da pasta sem as decoracoes, e so entao o
    /// nome cru.
    /// </summary>
    public static string? DisplayName(string dir, string? exePath)
    {
        if (exePath != null)
        {
            try
            {
                var product = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath).ProductName?.Trim();
                // descarta placeholder de engine e string vazia
                if (!string.IsNullOrWhiteSpace(product) && product.Length >= 3
                    && product.Any(char.IsLetter)
                    && !product.Equals("UnrealGame", StringComparison.OrdinalIgnoreCase)
                    && !product.Equals("Unreal Engine", StringComparison.OrdinalIgnoreCase))
                    return product;
            }
            catch (Exception ex) { Log.Warn($"produto de {exePath}: {ex.Message}"); }
        }

        var folder = Path.GetFileName(dir.TrimEnd('\\', '/'));
        var stripped = StripReleaseTags(folder);
        // pontos e underscores como separador viram espaco: "Mortal.Shell.II" -> "Mortal Shell II"
        stripped = Regex.Replace(stripped, @"[._]+", " ").Trim();
        stripped = Regex.Replace(stripped, @"\s+", " ");
        return stripped.Length >= 3 && !Containers.Contains(stripped) ? stripped : null;
    }
}
