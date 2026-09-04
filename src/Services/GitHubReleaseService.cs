using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Net;

namespace RenoDXLauncher.Services;

/// <summary>
/// Resolve o asset de um release do GitHub SEM usar a API.
///
/// A API anonima permite 60 requisicoes por hora POR IP. O launcher consulta releases em varios
/// pontos — DXVK, OptiScaler, dgVoodoo, a propria atualizacao — e quem instala em muitos jogos
/// acumula chamadas rapido. Passado o teto, tudo passa a responder `HTTP 403 Forbidden`, e o
/// sintoma que chega ao usuario e "nao consegui baixar", sem dizer que a causa e uma cota que se
/// recupera sozinha em uma hora.
///
/// As paginas publicas de release nao tem essa cota:
///
///   github.com/{repo}/releases/latest                     -> 302 para .../tag/{versao}
///   github.com/{repo}/releases/expanded_assets/{tag}       -> HTML com os links dos assets
///
/// Sao as mesmas paginas que o navegador abre. Um GITHUB_TOKEN no ambiente, se houver, ainda faz
/// a API valer a pena (5000/hora), entao ele e tentado primeiro e a pagina fica como caminho
/// normal — nao como remendo de emergencia.
/// </summary>
public static partial class GitHubReleaseService
{
    private static readonly string[] AllowedHosts =
        { "github.com", "api.github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com" };

    private static bool HostOk(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
        && u.Scheme == Uri.UriSchemeHttps
        && AllowedHosts.Contains(u.Host, StringComparer.OrdinalIgnoreCase);

    /// <summary>A tag do release mais recente, pelo redirecionamento de /releases/latest.</summary>
    public static async Task<string?> LatestTagAsync(HttpClient http, string repo, CancellationToken ct = default)
    {
        // Sem seguir o redirecionamento: o destino JA e a resposta.
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var plain = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) };
        plain.DefaultRequestHeaders.UserAgent.ParseAdd("RenoDXLauncher/1.0");
        try
        {
            using var r = await plain.GetAsync($"https://github.com/{repo}/releases/latest",
                                               HttpCompletionOption.ResponseHeadersRead, ct);
            var loc = r.Headers.Location?.ToString();
            if (string.IsNullOrEmpty(loc)) return null;
            var tag = loc[(loc.LastIndexOf('/') + 1)..];
            return string.IsNullOrWhiteSpace(tag) ? null : tag;
        }
        catch (Exception ex) { Log.Warn($"github: latest tag de {repo}: {ex.Message}"); return null; }
    }

    /// <summary>
    /// URLs dos assets de um release, lidas da pagina `expanded_assets`.
    /// </summary>
    public static async Task<List<string>> AssetsAsync(HttpClient http, string repo, string tag,
                                                       CancellationToken ct = default)
    {
        var urls = new List<string>();
        try
        {
            var html = await http.GetStringAsync(
                $"https://github.com/{repo}/releases/expanded_assets/{Uri.EscapeDataString(tag)}", ct);
            foreach (Match m in Regex.Matches(html, "href=\"(/[^\"]+/releases/download/[^\"]+)\""))
            {
                var url = "https://github.com" + m.Groups[1].Value;
                if (HostOk(url) && !urls.Contains(url)) urls.Add(url);
            }
        }
        catch (Exception ex) { Log.Warn($"github: assets de {repo}@{tag}: {ex.Message}"); }
        return urls;
    }

    /// <summary>
    /// O primeiro asset do release mais recente cujo nome casa com <paramref name="padrao"/>.
    ///
    /// Tenta a API quando ha GITHUB_TOKEN (cota de 5000/hora), e cai na pagina publica em
    /// qualquer outro caso — inclusive quando a API responde 403 por cota estourada, que e
    /// exatamente a situacao em que o caminho sem cota importa.
    /// </summary>
    public static async Task<string?> LatestAssetAsync(HttpClient http, string repo, Regex padrao,
                                                       CancellationToken ct = default)
    {
        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    $"https://api.github.com/repos/{repo}/releases/latest");
                req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
                using var resp = await http.SendAsync(req, ct);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync(ct);
                    var achado = Regex.Matches(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+)\"")
                        .Select(m => m.Groups[1].Value)
                        .FirstOrDefault(u => HostOk(u) && padrao.IsMatch(Path.GetFileName(u)));
                    if (achado is not null) return achado;
                }
            }
            catch (Exception ex) { Log.Warn($"github api ({repo}): {ex.Message}; usando a pagina"); }
        }

        var tag = await TagEstavelAsync(http, repo, ct);
        if (tag is null) return null;
        var assets = await AssetsAsync(http, repo, tag, ct);
        return assets.FirstOrDefault(u => padrao.IsMatch(Path.GetFileName(u)));
    }

    /// <summary>
    /// Um release se diz beta pelo NOME, e nao so pela caixinha do GitHub.
    ///
    /// `releases/latest` promete devolver so release estavel, e cumpre — desde que quem publicou
    /// tenha marcado "This is a pre-release". Quando esquece, a v0.12.1-beta.2 vira "latest" e
    /// desce para a maquina de todo mundo. Foi assim que o Feeder 0.12.1-beta.2 chegou aqui e
    /// matou o device D3D12 do Saints Row The Third dois segundos depois do primeiro quadro,
    /// enquanto a 0.12.0 rodava o jogo inteiro sem uma remocao.
    ///
    /// Entao o nome tambem conta. Nao e heuristica frouxa: o sufixo -beta/-alpha/-rc/-preview e
    /// convencao de versionamento semantico, e quem o escreve esta dizendo exatamente isto.
    /// </summary>
    public static bool EhPreRelease(string tag) => PreReleaseRegex().IsMatch(tag);

    [GeneratedRegex(@"[-.](alpha|beta|rc|preview|dev|snapshot|nightly)([-.]|\d|$)",
                    RegexOptions.IgnoreCase)]
    private static partial Regex PreReleaseRegex();

    /// <param name="Tag">A tag como o repositorio a escreve.</param>
    /// <param name="PreRelease">O nome dela se anuncia como beta/rc/alpha.</param>
    public record Release(string Tag, bool PreRelease);

    /// <summary>
    /// Os releases de um repositorio, do mais novo para o mais antigo.
    ///
    /// Lida da PAGINA, e nao da API: a API sem token da 60 chamadas por hora para a maquina
    /// inteira, e uma lista que a tela abre a cada visita gastaria essa cota por nada.
    ///
    /// A pagina ja vem na ordem certa — nao ha ordenacao a fazer aqui, e tentar ordenar por
    /// numero seria inventar um esquema de versao que o repositorio nao prometeu seguir.
    /// </summary>
    public static async Task<List<Release>> ListarReleasesAsync(HttpClient http, string repo,
                                                                int max = 30,
                                                                CancellationToken ct = default)
    {
        var lista = new List<Release>();
        try
        {
            var html = await http.GetStringAsync($"https://github.com/{repo}/releases", ct);
            foreach (Match m in Regex.Matches(html, $@"/{Regex.Escape(repo)}/releases/tag/([^""'\s]+)"))
            {
                var tag = WebUtility.HtmlDecode(m.Groups[1].Value);
                if (lista.Any(r => r.Tag == tag)) continue;   // a pagina repete o link por release
                lista.Add(new Release(tag, EhPreRelease(tag)));
                if (lista.Count >= max) break;
            }
        }
        catch (Exception ex) { Log.Warn($"github: releases de {repo}: {ex.Message}"); }
        return lista;
    }

    /// <summary>
    /// A tag estavel mais recente: a `latest`, ou — quando ela se diz beta — a primeira da
    /// lista de releases que nao se diga.
    /// </summary>
    public static async Task<string?> TagEstavelAsync(HttpClient http, string repo,
                                                      CancellationToken ct = default)
    {
        var tag = await LatestTagAsync(http, repo, ct);
        if (tag is not null && !EhPreRelease(tag)) return tag;
        if (tag is not null) Log.Warn($"github: {repo} aponta {tag} como latest, mas o nome diz beta; procurando a estavel");

        try
        {
            var html = await http.GetStringAsync($"https://github.com/{repo}/releases", ct);
            // A pagina lista os releases do mais novo para o mais antigo, e cada um traz um link
            // para a propria tag. A ORDEM da pagina e a resposta; nao ha o que ordenar aqui.
            foreach (Match m in Regex.Matches(html, $@"/{Regex.Escape(repo)}/releases/tag/([^""'\s]+)"))
            {
                var candidata = WebUtility.HtmlDecode(m.Groups[1].Value);
                if (!EhPreRelease(candidata)) return candidata;
            }
        }
        catch (Exception ex) { Log.Warn($"github: lista de releases de {repo}: {ex.Message}"); }

        // Nenhuma estavel encontrada: fica com a que o proprio GitHub chamou de latest, que e
        // melhor do que nao instalar nada — e o aviso acima ja ficou no log.
        return tag;
    }
}
