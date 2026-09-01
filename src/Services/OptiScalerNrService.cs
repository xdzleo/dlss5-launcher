using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.RegularExpressions;
using RenoDXLauncher.Localization;

namespace RenoDXLauncher.Services;

/// <summary>
/// Segunda engine para jogo que JA TEM DLSS: o fork OptiScaler_DLSSNR, do Dagherbou.
///
/// O caminho normal do launcher para esses jogos e ReShade + o add-on de Neural Rendering, que
/// engancha nas chamadas NGX do proprio jogo. Este fork faz o mesmo trabalho por outro desenho:
/// e o OptiScaler com um passe de Neural Rendering embutido (a composicao de cor vem do RenoDX,
/// sob MIT), e entra como `dxgi.dll` sozinho — sem ReShade, sem add-on separado.
///
/// As duas engines NAO convivem no mesmo jogo: as duas carregam como dxgi.dll, e a segunda a
/// chegar simplesmente nao carrega. Por isso a escolha e exclusiva, e trocar desinstala a outra.
///
/// Vale saber antes de escolher: o fork mira o modelo nao-corrigido. Sobre a DLL do proprio
/// driver isso significa RTX 50; com o build `310.8.SF` que este launcher instala, gerações
/// anteriores podem funcionar, mas nao ha teste dessa combinacao.
///
/// No jogo, Insert abre o overlay do OptiScaler; o Neural Rendering comeca DESLIGADO la.
/// </summary>
public static class OptiScalerNrService
{
    private const string Repo = "Dagherbou/OptiScaler_DLSSNR";
    private const string ProxyName = "dxgi.dll";

    /// <summary>Marca o que este launcher implantou, para a remocao levar so o que e nosso.</summary>
    private const string ManifestFile = "optiscaler-nr.renodx-manifest";

    public static string LibraryDir { get; } = Path.Combine(AppPaths.DataDir, "optiscaler-nr");
    private static string LibraryZip { get; } = Path.Combine(LibraryDir, "optiscaler-nr.zip");

    public static bool InLibrary => File.Exists(LibraryZip);

    /// <summary>Este jogo esta rodando pelo fork?</summary>
    public static bool IsDeployed(string targetDir) =>
        File.Exists(Path.Combine(targetDir, ManifestFile));

    private static readonly string[] AllowedHosts =
        { "github.com", "api.github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com" };

    private static bool HostOk(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
        && u.Scheme == Uri.UriSchemeHttps
        && AllowedHosts.Contains(u.Host, StringComparer.OrdinalIgnoreCase);

    private static HttpClient NewClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RenoDXLauncher/1.0");
        return http;
    }

    /// <summary>Baixa o release do fork para a biblioteca. Sem efeito se ja estiver la.</summary>
    public static async Task FetchAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (InLibrary) return;
        Directory.CreateDirectory(LibraryDir);
        progress?.Report(L.T("OptiNr_Fetching"));

        using var http = NewClient();
        // Pela pagina de release, nao pela API: 60 requisicoes/hora por IP viram 403 para quem
        // instala em varios jogos, e o sintoma nao diz que a causa e cota.
        var url = await GitHubReleaseService.LatestAssetAsync(http, Repo, new Regex(@"\.zip$"), ct);
        if (url is null || !HostOk(url)) throw new InvalidOperationException(L.T("OptiNr_NoAsset"));

        await using (var s = await http.GetStreamAsync(url, ct))
        await using (var f = File.Create(LibraryZip))
            await s.CopyToAsync(f, ct);

        Log.Info($"optiscaler-nr: baixado ({new FileInfo(LibraryZip).Length:N0} bytes) de {url}");
    }

    /// <summary>
    /// Extrai o fork na pasta do jogo e anota o que foi escrito.
    ///
    /// O manifesto existe porque este pacote espalha varios arquivos na raiz do jogo: sem a lista
    /// do que veio dele, a remocao teria de adivinhar — e adivinhar numa pasta de jogo significa
    /// apagar arquivo alheio.
    /// </summary>
    public static void Deploy(string targetDir, IProgress<string>? progress = null)
    {
        if (!InLibrary) throw new InvalidOperationException(L.T("OptiNr_NotInLibrary"));

        var escritos = new List<string>();
        using (var zip = ZipFile.OpenRead(LibraryZip))
        {
            foreach (var e in zip.Entries)
            {
                if (string.IsNullOrEmpty(e.Name)) continue;   // diretorio
                var destino = Path.Combine(targetDir, e.FullName.Replace('/', Path.DirectorySeparatorChar));
                var pastaDestino = Path.GetDirectoryName(destino);

                // Nada pode escapar da pasta do jogo por um caminho relativo dentro do zip.
                var raiz = Path.GetFullPath(targetDir);
                if (!Path.GetFullPath(destino).StartsWith(raiz, StringComparison.OrdinalIgnoreCase))
                { Log.Warn($"optiscaler-nr: entrada fora da pasta ignorada ({e.FullName})"); continue; }

                if (pastaDestino is not null) Directory.CreateDirectory(pastaDestino);
                // O dxgi.dll que ja estiver la (ReShade, por exemplo) e guardado, nao apagado.
                if (File.Exists(destino) && !File.Exists(destino + ".pre-optinr"))
                    File.Move(destino, destino + ".pre-optinr");
                e.ExtractToFile(destino, overwrite: true);
                escritos.Add(e.FullName);
            }
        }
        File.WriteAllLines(Path.Combine(targetDir, ManifestFile), escritos);
        progress?.Report(L.T("OptiNr_Deployed"));
        Log.Info($"optiscaler-nr: {escritos.Count} arquivos implantados em {targetDir}");
    }

    /// <summary>Tira o fork, guiado pelo manifesto — nada de adivinhar numa pasta de jogo.</summary>
    public static void Remove(string targetDir)
    {
        var manifesto = Path.Combine(targetDir, ManifestFile);
        if (!File.Exists(manifesto)) return;
        foreach (var rel in File.ReadAllLines(manifesto))
        {
            try
            {
                var p = Path.Combine(targetDir, rel.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(p)) File.Delete(p);
                var guardado = p + ".pre-optinr";
                if (File.Exists(guardado)) File.Move(guardado, p, overwrite: true);
            }
            catch (Exception ex) { Log.Warn($"optiscaler-nr remove ({rel}): {ex.Message}"); }
        }
        try { File.Delete(manifesto); } catch { }
        Log.Info($"optiscaler-nr: removido de {targetDir}");
    }
}
