using System.IO;
using System.Net.Http;
using System.Text.Json;
using RenoDXLauncher.Localization;

namespace RenoDXLauncher.Services;

/// <summary>
/// OptiScaler: faz um jogo que so oferece FSR 2/3 ou XeSS entregar os mesmos buffers ao DLSS.
///
/// Preenche o unico buraco que restava entre os tres caminhos existentes. Um jogo cai em:
///
///   tem DLSS proprio          -> addon neural direto, ou a ponte DX11
///   nao tem upscaler nenhum   -> Feeder, que FABRICA o contrato a partir de um shader
///   tem FSR ou XeSS, sem DLSS -> era ninguem. Agora e este servico.
///
/// A terceira linha e uma classe grande de jogos — God of War, The Last of Us, e todo titulo que
/// saiu com FSR por acordo comercial. Neles o Feeder tecnicamente funcionaria, mas seria um
/// desperdicio: o jogo JA calcula motion vectors e depth corretos para o proprio upscaler, e o
/// Feeder os reconstroi por fora, com um shader, pior e mais caro. O OptiScaler intercepta a
/// chamada do FSR/XeSS e entrega esses buffers ao DLSS — dado do proprio engine, sem fabricacao.
///
/// Vem do repositorio do autor, como todo o resto. A distribuicao dele e .7z, formato que o .NET
/// nao le, entao o extrator oficial standalone (7zr.exe, livre para redistribuir) e buscado junto
/// na primeira vez.
/// </summary>
public static class OptiScalerService
{
    private const string Repo = "optiscaler/OptiScaler";
    private const string SevenZipUrl = "https://www.7-zip.org/a/7zr.exe";

    /// <summary>O nome de proxy que o OptiScaler usa. `version.dll` e o slot que ele documenta e
    /// o que a comunidade usa: nao colide com dxgi.dll, que fica livre para o ReShade.</summary>
    private const string ProxyName = "version.dll";

    private static readonly string[] AllowedHosts =
    [
        "github.com", "api.github.com",
        "objects.githubusercontent.com", "release-assets.githubusercontent.com",
        "www.7-zip.org", "7-zip.org",
    ];

    public static string LibraryDir { get; } = Path.Combine(AppPaths.DataDir, "optiscaler");
    private static string LibraryDll => Path.Combine(LibraryDir, "OptiScaler.dll");
    private static string LibraryIni => Path.Combine(LibraryDir, "OptiScaler.ini");
    private static string SevenZip => Path.Combine(LibraryDir, "7zr.exe");

    public static bool InLibrary => File.Exists(LibraryDll);

    public static bool IsDeployed(string targetDir) =>
        File.Exists(Path.Combine(targetDir, ProxyName))
        && File.Exists(Path.Combine(targetDir, "OptiScaler.ini"));

    /// <summary>
    /// Este jogo tem FSR ou XeSS proprio, e nao tem DLSS?
    ///
    /// A evidencia e o binario do upscaler na pasta, nao uma string no executavel: um jogo pode
    /// mencionar "FSR" num menu e nao ter FSR 2 nenhum. O que importa e a DLL que faz o trabalho.
    /// </summary>
    public static bool Applies(string targetDir, bool jogoTemDlss)
    {
        if (jogoTemDlss || !Directory.Exists(targetDir)) return false;
        return AchaUpscaler(targetDir) is not null;
    }

    /// <summary>Qual upscaler o jogo traz, para dizer ao usuario o que ligar no menu.</summary>
    public static string? AchaUpscaler(string targetDir)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(targetDir, "*.dll", SearchOption.AllDirectories))
            {
                if (AddonService.IsLauncherOwnedDir(f)) continue;
                var n = Path.GetFileName(f);

                // FSR 2/3: a DLL da AMD. O FSR 1 nao serve — e espacial, nao usa motion vectors,
                // e nao ha o que redirecionar.
                if (n.StartsWith("ffx_fsr2", StringComparison.OrdinalIgnoreCase)
                    || n.StartsWith("amd_fidelityfx", StringComparison.OrdinalIgnoreCase)) return "FSR";

                // XeSS: a DLL da Intel.
                if (n.Equals("libxess.dll", StringComparison.OrdinalIgnoreCase)) return "XeSS";
            }
        }
        catch (Exception ex) { Log.Warn($"optiscaler scan {targetDir}: {ex.Message}"); }
        return null;
    }

    private static bool HostOk(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
        && u.Scheme == Uri.UriSchemeHttps
        && AllowedHosts.Contains(u.Host, StringComparer.OrdinalIgnoreCase);

    public static async Task FetchAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (InLibrary) return;
        Directory.CreateDirectory(LibraryDir);
        progress?.Report(L.T("OptiScaler_Fetching"));

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RenoDXLauncher/1.0");

        // 1. o extrator, porque a distribuicao e .7z e o .NET nao abre esse formato
        if (!File.Exists(SevenZip))
        {
            progress?.Report(L.T("OptiScaler_Fetching7z"));
            await BaixarAsync(http, SevenZipUrl, SevenZip, ct);
        }

        // 2. o asset da release — o nome carrega data e build, entao fixa-lo numa URL
        //    "latest/download" quebraria na proxima versao.
        //
        //    Resolvido pela PAGINA de release, nao pela API: a API anonima limita a 60
        //    requisicoes por hora por IP, e quem instala em varios jogos estoura isso e recebe
        //    403 em tudo — sem nenhuma pista de que a causa e cota, nao rede.
        var url = await GitHubReleaseService.LatestAssetAsync(
            http, Repo, new System.Text.RegularExpressions.Regex(@"\.7z$"), ct);
        if (url is null || !HostOk(url)) throw new InvalidOperationException(L.T("OptiScaler_NoAsset"));

        var pacote = Path.Combine(LibraryDir, "optiscaler.7z");
        try
        {
            await BaixarAsync(http, url, pacote, ct);

            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(SevenZip)
            {
                Arguments = $"x -y -o\"{LibraryDir}\" \"{pacote}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) throw new InvalidOperationException(L.T("OptiScaler_Incomplete"));
            await p.WaitForExitAsync(ct);

            // O pacote pode trazer a DLL numa subpasta; achatar evita depender do layout, que
            // muda entre releases.
            if (!File.Exists(LibraryDll))
            {
                var achado = Directory.EnumerateFiles(LibraryDir, "OptiScaler.dll", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (achado is not null) File.Copy(achado, LibraryDll, overwrite: true);
            }
            if (!File.Exists(LibraryIni))
            {
                var ini = Directory.EnumerateFiles(LibraryDir, "OptiScaler.ini", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (ini is not null) File.Copy(ini, LibraryIni, overwrite: true);
            }
            if (!InLibrary) throw new InvalidOperationException(L.T("OptiScaler_Incomplete"));
            Log.Info("optiscaler: biblioteca montada");
        }
        finally
        {
            try { File.Delete(pacote); } catch { }
        }
    }

    /// <summary>
    /// Poe o OptiScaler na pasta do jogo.
    ///
    /// Ele e mutuamente exclusivo com o Feeder, pela mesma razao que a ponte e: os dois querem
    /// alimentar o mesmo pass, e dois produtores para um consumidor nao e uma configuracao — e
    /// um bug. Quem chama garante a exclusao.
    /// </summary>
    public static void Deploy(string targetDir, IProgress<string>? progress = null)
    {
        if (!InLibrary) throw new InvalidOperationException(L.T("OptiScaler_NotInLibrary"));

        var destino = Path.Combine(targetDir, ProxyName);
        // O jogo pode ter um version.dll proprio (raro, mas acontece com mods antigos).
        var backup = destino + ".renodx-bak";
        if (File.Exists(destino) && !File.Exists(backup)) File.Copy(destino, backup);
        File.Copy(LibraryDll, destino, overwrite: true);

        if (File.Exists(LibraryIni))
        {
            var iniDest = Path.Combine(targetDir, "OptiScaler.ini");
            if (!File.Exists(iniDest)) File.Copy(LibraryIni, iniDest);
        }

        progress?.Report(L.T("OptiScaler_Deployed"));
        Log.Info($"optiscaler: {ProxyName} implantado em {targetDir}");
    }

    /// <summary>Tira o OptiScaler e devolve o que havia antes.</summary>
    public static void Remove(string targetDir)
    {
        try
        {
            var alvo = Path.Combine(targetDir, ProxyName);
            var backup = alvo + ".renodx-bak";
            if (File.Exists(backup)) { File.Copy(backup, alvo, overwrite: true); File.Delete(backup); }
            else if (File.Exists(alvo)) File.Delete(alvo);

            foreach (var f in new[] { "OptiScaler.ini", "OptiScaler.log" })
            {
                var p = Path.Combine(targetDir, f);
                if (File.Exists(p)) File.Delete(p);
            }
        }
        catch (Exception ex) { Log.Warn($"optiscaler remove {targetDir}: {ex.Message}"); }
    }

    private static async Task BaixarAsync(HttpClient http, string url, string destino, CancellationToken ct)
    {
        if (!HostOk(url)) throw new InvalidOperationException(L.T("OptiScaler_BadHost", url));
        Directory.CreateDirectory(Path.GetDirectoryName(destino)!);
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var origem = await resp.Content.ReadAsStreamAsync(ct);
        await using var arquivo = File.Create(destino);
        await origem.CopyToAsync(arquivo, ct);
    }
}
