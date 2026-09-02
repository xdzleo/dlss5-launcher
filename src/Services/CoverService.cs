using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using RenoDXLauncher.Models;

namespace RenoDXLauncher.Services;

/// <summary>
/// Cover art, local-first. Modern Steam stores the library art as
/// <c>librarycache/&lt;appid&gt;/&lt;hash&gt;/library_capsule.jpg</c> (older builds used flat
/// <c>&lt;appid&gt;_library_600x900.jpg</c>); Xbox/Game Pass ships its own images inside the game
/// folder, named by MicrosoftGame.config's ShellVisuals. Only when nothing local exists do we
/// hit the Steam CDN — which 404s for plenty of newer appids.
/// </summary>
public static partial class CoverService
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("RenoDXLauncher/1.0");
        return c;
    }

    [GeneratedRegex(@"<ShellVisuals[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ShellVisualsRegex();

    public static async Task<string?> GetCoverAsync(GameInfo game, int? steamAppIdHint)
    {
        try
        {
            if (game.LocalCoverPath != null && File.Exists(game.LocalCoverPath))
                return game.LocalCoverPath;

            if (FindLocalCover(game, steamAppIdHint) is { } local) return local;

            var appId = game.SteamAppId ?? steamAppIdHint;

            // Sem appid: descobre um PELO NOME.
            //
            // Jogo que nao veio de loja nenhuma — um repack numa pasta, uma pasta adicionada a mao
            // — nao tem appid, e ate aqui a busca simplesmente desistia e o card ficava com as
            // iniciais num retangulo cinza. Sao justamente os jogos em que a capa mais ajuda:
            // "Metal Gear Solid V The Phantom Pain" numa pasta de repack e uma linha de texto
            // longa, enquanto a capa e reconhecida de relance.
            //
            // O catalogo da Steam serve de indice mesmo para quem nao comprou ali: quase todo jogo
            // de PC tem uma pagina, e a arte de biblioteca esta num CDN publico. E o mesmo que o
            // Playnite faz — resolver o nome num provedor de metadados e baixar a arte de la.
            appId ??= await ResolverAppIdPorNomeAsync(game.Name);
            if (appId is null) return null;

            Directory.CreateDirectory(AppPaths.CoversDir);
            var cached = Path.Combine(AppPaths.CoversDir, $"steam_{appId}.jpg");
            if (File.Exists(cached)) return cached;
            var miss = cached + ".miss";
            if (File.Exists(miss) && DateTime.UtcNow - File.GetLastWriteTimeUtc(miss) < TimeSpan.FromDays(7))
                return null;

            foreach (var url in new[]
            {
                $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{appId}/library_600x900.jpg",
                $"https://shared.steamstatic.com/store_item_assets/steam/apps/{appId}/library_600x900.jpg",
                $"https://steamcdn-a.akamaihd.net/steam/apps/{appId}/library_600x900_2x.jpg",
                $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg",
            })
            {
                if (await BaixarAsync(url, cached) is { } ok) return ok;
            }

            // Nenhum caminho SEM hash serviu. Vale a pena perguntar a loja.
            //
            // A Steam passou a guardar a arte sob um hash por asset, e os enderecos acima — os
            // antigos, sem hash — so respondem para jogos ja lancados. Um titulo que ainda nao
            // saiu tem pagina e tem arte, mas so a API sabe o hash dela: o Mortal Shell II e o
            // LEGO Batman foram resolvidos para os appids certos e mesmo assim ficavam sem capa.
            //
            // O que volta e o header (460x215), e nao o retrato: cada asset tem o seu proprio
            // hash, e o do retrato nao esta nesta resposta. O card ja aceita essa proporcao — ela
            // era o ultimo item da lista acima —, e uma capa deitada e melhor do que um retangulo
            // cinza com as iniciais.
            if (await ArteDaLojaAsync(appId.Value) is { } doStore
                && await BaixarAsync(doStore, cached) is { } ok2) return ok2;

            await File.WriteAllBytesAsync(miss, Array.Empty<byte>());
        }
        catch (Exception ex) { Log.Warn($"cover {game.Name}: {ex.Message}"); }
        return null;
    }

    /// <summary>Cover already on disk, put there by the store itself.</summary>
    private static string? FindLocalCover(GameInfo game, int? steamAppIdHint)
    {
        var appId = game.SteamAppId ?? steamAppIdHint;
        if (appId is not null && StoreScanners.SteamInstallPath is { } steam)
        {
            var root = Path.Combine(steam, "appcache", "librarycache");
            // modern layout: librarycache/<appid>/<hash>/library_capsule.jpg
            var appFolder = Path.Combine(root, appId.Value.ToString());
            if (Directory.Exists(appFolder))
            {
                foreach (var name in new[] { "library_capsule.jpg", "library_600x900.jpg", "library_capsule.png" })
                {
                    var hit = SafeFind(appFolder, name);
                    if (hit != null) return hit;
                }
            }
            // legacy flat layout
            var flat = Path.Combine(root, $"{appId}_library_600x900.jpg");
            if (File.Exists(flat)) return flat;
        }

        if (game.Store == GameStore.Xbox) return FindXboxCover(game.InstallDir);
        return null;
    }

    /// <summary>Xbox/Game Pass ships art inside the game folder; MicrosoftGame.config names it.
    /// Prefer the biggest/most pictorial one — a cropped splash reads far better than initials.</summary>
    private static string? FindXboxCover(string installDir)
    {
        try
        {
            if (!Directory.Exists(installDir)) return null;
            var names = new List<string>();
            var cfg = Path.Combine(installDir, "MicrosoftGame.config");
            if (File.Exists(cfg))
            {
                var m = ShellVisualsRegex().Match(File.ReadAllText(cfg));
                if (m.Success)
                {
                    // 480x480 art crops best into a portrait tile; the 16:9 splash is the fallback
                    foreach (var attr in new[]
                             { "Square480x480Logo", "SplashScreenImage", "Square150x150Logo", "StoreLogo" })
                    {
                        var am = Regex.Match(m.Value, attr + @"\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase);
                        if (am.Success) names.Add(am.Groups[1].Value);
                    }
                }
            }
            // common names shipped by Xbox titles, used when the config lists none
            names.AddRange(new[] { "SplashScreen.png", "background_launcher.png", "WideLogo.png", "Logo.png", "StoreLogo.png" });

            foreach (var n in names)
            {
                var p = Path.Combine(installDir, n.Replace('/', '\\'));
                if (File.Exists(p)) return p;
            }
        }
        catch (Exception ex) { Log.Warn($"xbox cover {installDir}: {ex.Message}"); }
        return null;
    }

    private static string? SafeFind(string folder, string fileName)
    {
        try
        {
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                MaxRecursionDepth = 2,
            };
            return Directory.EnumerateFiles(folder, fileName, options).FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>Baixa e grava, ou devolve null. Escreve num temporario e move: um arquivo cortado
    /// no meio ficaria cacheado para sempre como imagem quebrada.</summary>
    private static async Task<string?> BaixarAsync(string url, string destino)
    {
        try
        {
            var bytes = await Http.GetByteArrayAsync(url);
            if (bytes.Length < 500) return null;
            var temp = destino + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes);
            File.Move(temp, destino, overwrite: true);
            return destino;
        }
        catch { return null; }
    }

    /// <summary>O endereco da arte segundo a propria loja — o unico lugar que conhece o hash do
    /// asset. `filters=basic` mantem a resposta pequena.</summary>
    private static async Task<string?> ArteDaLojaAsync(int appId)
    {
        try
        {
            var json = await Http.GetStringAsync(
                $"https://store.steampowered.com/api/appdetails?appids={appId}&filters=basic");
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(appId.ToString(), out var no)) return null;
            if (!no.TryGetProperty("success", out var s) || !s.GetBoolean()) return null;
            if (!no.TryGetProperty("data", out var d)) return null;
            if (!d.TryGetProperty("header_image", out var h)) return null;
            var url = h.GetString();
            return string.IsNullOrWhiteSpace(url) ? null : url;
        }
        catch (Exception ex) { Log.Warn($"arte da loja {appId}: {ex.Message}"); return null; }
    }

    // ---------------------------------------------------------------- nome -> appid

    /// <summary>Tags de release que atrapalham a busca: "(2026)", "[DODI]", "-CODEX", "v1.0.3",
    /// "Repack". O nome que a loja conhece nao tem nenhuma delas.</summary>
    [GeneratedRegex(@"[\[\(\{][^\]\)\}]*[\]\)\}]|[-_.](repack|multi\d*|proper|readnfo|codex|fitgirl|dodi|elamigos|plaza|skidrow|razor1911)\b|\bv?\d+(\.\d+){2,}\b",
                    RegexOptions.IgnoreCase)]
    private static partial Regex RuidoDeReleaseRegex();

    /// <summary>
    /// Acha o appid da Steam a partir do NOME do jogo.
    ///
    /// O endpoint de sugestao da comunidade responde JSON e nao pede chave nem cota registrada —
    /// o que importa porque a API de releases ja nos custou 403 por cota anonima uma vez, e essa
    /// licao esta no changelog da 1.59.
    ///
    /// A resposta e cacheada em disco pelos DOIS lados. O acerto poupa a rede; o erro poupa MAIS,
    /// porque um nome que nao existe na Steam ("WinBox", o nome de uma pasta de repack que ficou
    /// estranho) seria consultado de novo a cada abertura do launcher, para sempre.
    /// </summary>
    private static async Task<int?> ResolverAppIdPorNomeAsync(string nome)
    {
        if (string.IsNullOrWhiteSpace(nome)) return null;

        var limpo = RuidoDeReleaseRegex().Replace(nome, " ");
        limpo = Regex.Replace(limpo, @"[_\.]+", " ");
        limpo = Regex.Replace(limpo, @"\s{2,}", " ").Trim(' ', '-', '–');
        if (limpo.Length < 3) return null;

        try
        {
            Directory.CreateDirectory(AppPaths.CoversDir);
            var chave = Regex.Replace(limpo.ToLowerInvariant(), @"[^a-z0-9]+", "_").Trim('_');
            var cacheNome = Path.Combine(AppPaths.CoversDir, $"nome_{chave}.appid");
            if (File.Exists(cacheNome))
            {
                var txt = (await File.ReadAllTextAsync(cacheNome)).Trim();
                // Vazio = "procurei e nao existe". Guardado por uma semana, como o .miss do CDN;
                // depois disso a busca segue adiante e regrava o arquivo, senao um jogo que
                // entrou na Steam depois da primeira tentativa ficaria sem capa para sempre.
                if (txt.Length == 0)
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheNome) < TimeSpan.FromDays(7))
                        return null;
                }
                else
                    return int.TryParse(txt, out var v) ? v : null;
            }

            var url = "https://steamcommunity.com/actions/SearchApps/" + Uri.EscapeDataString(limpo);
            var json = await Http.GetStringAsync(url);

            // A resposta e uma lista ordenada por relevancia; o primeiro cujo nome bate de forma
            // razoavel e a escolha. Aceitar o primeiro sem conferir traria "Metal Gear Rising"
            // para quem procurou "Metal Gear Solid V".
            var appId = EscolherMelhor(json, limpo);
            await File.WriteAllTextAsync(cacheNome, appId?.ToString() ?? "");
            if (appId is not null) Log.Info($"capa: \"{nome}\" resolvido para appid {appId}");
            return appId;
        }
        catch (Exception ex)
        {
            Log.Warn($"resolver appid de \"{nome}\": {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// O melhor candidato da lista, ou nenhum.
    ///
    /// A comparacao e feita sobre o nome NORMALIZADO (so letras e digitos, minusculas) porque o
    /// que separa "Marvel's Spider-Man 2" de "Marvels Spider Man 2" e so pontuacao. Primeiro a
    /// lista inteira e varrida por igualdade exata; so depois vale um conter o outro, e mesmo
    /// assim o pedaco que sobra nao pode comecar por numero nem por algarismo romano. Sem essa
    /// regra "darksoulsiii" continha "darksoulsii" e "portal2" continha "portal", e a lista da
    /// Steam nao e exata-primeiro: um vizinho da franquia vencia, e a capa errada ficava gravada
    /// no cache — uma capa errada e pior do que nenhuma, porque parece certa.
    /// </summary>
    private static int? EscolherMelhor(string json, string procurado)
    {
        static string Norm(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]+", "");
        var alvo = Norm(procurado);
        if (alvo.Length < 3) return null;

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return null;

        var candidatos = new List<(int Id, string Nome, string Norm)>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("appid", out var a) || !item.TryGetProperty("name", out var n))
                continue;
            var nome = n.GetString() ?? "";
            var cand = Norm(nome);
            if (cand.Length < 3) continue;
            var raw = a.ValueKind == System.Text.Json.JsonValueKind.Number
                ? a.GetInt32().ToString() : a.GetString();
            if (int.TryParse(raw, out var id)) candidatos.Add((id, nome, cand));
        }

        foreach (var c in candidatos)
            if (c.Norm == alvo) return c.Id;

        foreach (var c in candidatos)
            if (EhMesmoJogo(alvo, procurado, c.Norm, c.Nome)) return c.Id;
        return null;
    }

    /// <summary>Um algarismo romano como palavra inteira ("III", "IV", "XII") NO COMECO do resto
    /// cru: so a fronteira de palavra diz se o "i" que sobra e "III" ou o inicio de "Intergrade".
    /// Ancorado no inicio de proposito — testar o nome inteiro recusava "Final Fantasy VII Remake
    /// Intergrade" por causa do "VII" que os dois nomes compartilham.</summary>
    [GeneratedRegex(@"^(?=[ivx])x{0,3}(ix|iv|v?i{0,3})(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex AlgarismoRomanoRegex();

    /// <summary>
    /// Um nome contem o outro e o resto e subtitulo ou edicao — nao uma sequencia. Vale nas duas
    /// direcoes: a loja pode acrescentar ": Definitive Edition" ao que o usuario procurou, e a
    /// pasta do usuario pode trazer "Deluxe" que a loja nao tem.
    /// </summary>
    private static bool EhMesmoJogo(string alvo, string alvoCru, string cand, string candCru)
    {
        string longo, curto, longoCru;
        if (cand.Contains(alvo)) { longo = cand; curto = alvo; longoCru = candCru; }
        else if (alvo.Contains(cand)) { longo = alvo; curto = cand; longoCru = alvoCru; }
        else return false;
        if (longo.Length == curto.Length) return true;

        var inicioResto = longo.IndexOf(curto, StringComparison.Ordinal) + curto.Length;
        var resto = longo[inicioResto..];
        if (resto.Length == 0) return true;
        if (char.IsDigit(resto[0])) return false;
        if (!"ivx".Contains(resto[0])) return true;

        // O algarismo romano tem de ser julgado no RESTO cru, e nao no nome inteiro: "Diablo IV"
        // e "Diablo IV: Vessel of Hatred" partilham o "IV", e o que decide e o "Vessel" que sobra.
        // Sem o mapeamento nao ha como julgar; recusar e o lado seguro, porque uma capa errada
        // e pior do que nenhuma.
        var restoCru = RestoCru(longoCru, inicioResto);
        if (restoCru is null) return false;
        return !AlgarismoRomanoRegex().IsMatch(restoCru);
    }

    /// <summary>
    /// O pedaco do nome cru a partir do <paramref name="indiceNormalizado"/>-esimo caractere que
    /// sobrevive a normalizacao (minusculas, so [a-z0-9]). Refaz o mesmo filtro de
    /// <see cref="EscolherMelhor"/> caractere a caractere, por isso o indice bate; nulo se o nome
    /// cru for mais curto do que o normalizado diz (nao deveria acontecer).
    /// </summary>
    private static string? RestoCru(string cru, int indiceNormalizado)
    {
        var minusculo = cru.ToLowerInvariant();
        var vistos = 0;
        for (var i = 0; i < minusculo.Length; i++)
        {
            var c = minusculo[i];
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9')) continue;
            if (vistos == indiceNormalizado) return cru[i..];
            vistos++;
        }
        return null;
    }
}
