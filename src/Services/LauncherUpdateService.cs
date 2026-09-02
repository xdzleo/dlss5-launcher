using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using RenoDXLauncher.Localization;

namespace RenoDXLauncher.Services;

/// <summary>Uma release do launcher mais nova do que a que esta rodando.</summary>
public sealed record LauncherRelease(Version Version, string Tag, string SetupUrl, long Size, string? SumsUrl);

/// <summary>
/// Atualizacao do proprio launcher, a partir da ultima release publicada no GitHub.
///
/// O publico deste app instala mod por conta propria e nao volta ao repositorio para conferir
/// versao; entao a checagem roda sozinha na abertura e so aparece quando ha o que mostrar.
///
/// O download e conferido contra o SHA256SUMS.txt da mesma release. Isso NAO defende de um
/// repositorio comprometido — o hash viria comprometido junto. Defende do que de fato acontece:
/// download truncado, proxy que devolve pagina de erro com 200, cache corrompido. Um setup.exe
/// pela metade que roda mesmo assim e pior do que nao atualizar.
/// </summary>
public static class LauncherUpdateService
{
    private const string Repo = "xdzleo/dlss5-launcher";

    /// <summary>Hosts de onde um asset pode vir. A URL sai da API, mas a API e um servidor — se
    /// ela apontar o binario para outro lugar, nao seguimos.</summary>
    private static readonly string[] AllowedHosts =
    [
        "github.com", "api.github.com",
        "objects.githubusercontent.com", "release-assets.githubusercontent.com",
    ];

    public static Version Current =>
        Assembly.GetExecutingAssembly().GetName().Version is { } v
            ? new Version(v.Major, v.Minor, v.Build)
            : new Version(0, 0, 0);

    private static HttpClient NewClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RenoDXLauncher/1.0");
        return http;
    }

    private static bool HostOk(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
        && u.Scheme == Uri.UriSchemeHttps
        && AllowedHosts.Contains(u.Host, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Pergunta ao GitHub qual e a ultima release. Devolve null quando ja estamos na mais nova e
    /// tambem quando a checagem falha: ficar sem internet nao e evento que mereca interromper
    /// alguem que abriu o app para jogar.
    /// </summary>
    public static async Task<LauncherRelease?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = NewClient();
            var json = await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("draft", out var d) && d.GetBoolean()) return null;
            if (root.TryGetProperty("prerelease", out var p) && p.GetBoolean()) return null;

            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v)) return null;
            v = new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
            if (v <= Current) return null;

            string? setup = null, sums = null;
            long size = 0;
            foreach (var a in root.GetProperty("assets").EnumerateArray())
            {
                var nome = a.GetProperty("name").GetString() ?? "";
                var url = a.GetProperty("browser_download_url").GetString() ?? "";
                if (!HostOk(url)) continue;
                if (nome.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase))
                {
                    setup = url;
                    size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                }
                else if (nome.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                {
                    sums = url;
                }
            }

            // Sem setup.exe nao ha atualizacao a oferecer. Anunciar "tem versao nova" e depois nao
            // conseguir instalar e pior do que ficar quieto.
            if (setup is null) return null;
            return new LauncherRelease(v, tag, setup, size, sums);
        }
        catch (Exception ex)
        {
            Log.Warn($"launcher update check: {ex.Message}");
            return null;
        }
    }

    /// <summary>Baixa o setup e confere o hash. Devolve o caminho do arquivo pronto para rodar.</summary>
    public static async Task<string> DownloadAsync(LauncherRelease rel, IProgress<string>? progress = null,
                                                   CancellationToken ct = default)
    {
        if (!HostOk(rel.SetupUrl)) throw new InvalidOperationException(L.T("Update_BadHost"));

        // Na pasta de cache do app, e nao em %TEMP%: e a regra do projeto (ver AppPaths.CacheDir),
        // e este e o pior lugar para quebra-la — um executavel de nome variavel que sai de %TEMP%
        // pedindo elevacao e exatamente o par que a heuristica de antivirus pontua. Todo outro
        // binario que o launcher baixa (ReShade, banco da Battle.net) ja vive aqui.
        var dir = Path.Combine(AppPaths.CacheDir, "update");
        Directory.CreateDirectory(dir);
        var destino = Path.Combine(dir, $"RenoDXLauncher-{rel.Version}-setup.exe");

        using var http = NewClient();
        using (var resp = await http.GetAsync(rel.SetupUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? rel.Size;
            using var origem = await resp.Content.ReadAsStreamAsync(ct);
            using var arquivo = File.Create(destino);
            var buffer = new byte[128 * 1024];
            long lidos = 0;
            int n, ultimoPct = -1;
            while ((n = await origem.ReadAsync(buffer, ct)) > 0)
            {
                await arquivo.WriteAsync(buffer.AsMemory(0, n), ct);
                lidos += n;
                if (total <= 0) continue;
                var pct = (int)(lidos * 100 / total);
                if (pct == ultimoPct) continue;
                ultimoPct = pct;
                progress?.Report(L.T("Update_Downloading", pct));
            }
        }

        var esperado = await ExpectedHashAsync(http, rel, ct);
        if (esperado is not null)
        {
            string obtido;
            using (var fs = File.OpenRead(destino))
                obtido = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
            if (!obtido.Equals(esperado, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(destino);
                throw new InvalidOperationException(L.T("Update_HashMismatch"));
            }
        }
        else if (rel.Size > 0 && new FileInfo(destino).Length != rel.Size)
        {
            // Sem SHA256SUMS na release, o tamanho anunciado pela API e a unica confirmacao que
            // sobra de que o arquivo chegou inteiro.
            TryDelete(destino);
            throw new InvalidOperationException(L.T("Update_HashMismatch"));
        }

        return destino;
    }

    /// <summary>Le o SHA256SUMS.txt da release e devolve o hash da linha do setup.</summary>
    private static async Task<string?> ExpectedHashAsync(HttpClient http, LauncherRelease rel, CancellationToken ct)
    {
        if (rel.SumsUrl is null || !HostOk(rel.SumsUrl)) return null;
        try
        {
            var texto = await http.GetStringAsync(rel.SumsUrl, ct);
            foreach (var linha in texto.Split('\n'))
            {
                var partes = linha.Trim().Split([' ', '\t', '*'], StringSplitOptions.RemoveEmptyEntries);
                if (partes.Length < 2 || partes[0].Length != 64) continue;
                var nome = Path.GetFileName(partes[^1]);
                if (nome.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase)) return partes[0];
            }
        }
        catch (Exception ex) { Log.Warn($"SHA256SUMS: {ex.Message}"); }
        return null;
    }

    private static void TryDelete(string p)
    {
        try { File.Delete(p); } catch { }
    }

    /// <summary>
    /// Roda o setup. Devolve true quando ele foi mesmo iniciado.
    ///
    /// O setup pede elevacao e o launcher nao esta elevado, entao nao da para esperar por ele: o
    /// processo que iniciamos morre assim que o elevado nasce. Um acompanhante separado espera o
    /// setup terminar e reabre o app — sem isso a atualizacao acaba com a janela fechada e nada
    /// explicando por que.
    /// </summary>
    public static bool RunSetup(string setupPath)
    {
        if (!File.Exists(setupPath)) return false;
        try
        {
            var proc = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo(setupPath)
                {
                    // /SILENT mostra so a barra de progresso. Sem SUPPRESSMSGBOXES de proposito:
                    // se algo falhar, quem esta na frente da tela precisa ver.
                    Arguments = "/SILENT /NORESTART /CLOSEAPPLICATIONS",
                    UseShellExecute = true,
                },
            };
            if (!proc.Start()) return false;

            if (Environment.ProcessPath is { } exe)
                Relaunch(Path.GetFileNameWithoutExtension(setupPath), exe);
            return true;
        }
        catch (Exception ex)
        {
            // Cancelar o UAC cai aqui. Nao e erro: e uma resposta.
            Log.Warn($"run setup: {ex.Message}");
            return false;
        }
    }

    /// <summary>Espera o setup sumir da lista de processos e reabre o launcher ja atualizado.</summary>
    private static void Relaunch(string nomeSetup, string exe)
    {
        var alvo = exe.Replace("'", "''");
        var script =
            "Start-Sleep -Seconds 4; " +
            "$fim=(Get-Date).AddMinutes(10); " +
            $"while ((Get-Process -Name '{nomeSetup}' -ErrorAction SilentlyContinue) -and (Get-Date) -lt $fim)" +
            " { Start-Sleep -Seconds 1 }; " +
            "Start-Sleep -Seconds 2; " +
            $"if (Test-Path '{alvo}') {{ Start-Process '{alvo}' }}";
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe")
            {
                Arguments = $"-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"")}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex) { Log.Warn($"relaunch shim: {ex.Message}"); }
    }
}
