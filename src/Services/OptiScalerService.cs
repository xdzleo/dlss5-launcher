using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
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

    /// <summary>
    /// O extrator, FIXADO em versao e em hash.
    ///
    /// O 7zr.exe oficial nao e assinado (o Authenticode do binario responde NotSigned), entao a
    /// unica prova de origem possivel e o SHA-256 do proprio arquivo — e um hash so faz sentido
    /// com a versao tambem fixa, porque o `/a/7zr.exe` do site troca de conteudo a cada release.
    /// Por isso a URL principal e a do release versionado no GitHub do autor (ip7z/7zip), que nao
    /// muda de conteudo; o site oficial fica como segundo caminho e tem de bater no mesmo hash.
    ///
    /// Sem isso o que rodava era "o que estiver em %AppData% com esse nome": um download
    /// interrompido deixava um exe truncado que era executado em toda instalacao seguinte.
    ///
    /// Conferido em 2026-09-02: 7-Zip 26.02 (2026-06-25), 602.112 bytes, mesmo conteudo nas
    /// duas URLs. Ao subir de versao, atualizar os quatro valores juntos.
    /// </summary>
    private const string SevenZipVersion = "26.02";
    private const string SevenZipUrl = "https://github.com/ip7z/7zip/releases/download/26.02/7zr.exe";
    private const string SevenZipUrlFallback = "https://www.7-zip.org/a/7zr.exe";
    private const string SevenZipSha256 = "56b8cc9f4971cef253644fafe54063ed7fdca551d4dee0f8c6baa81b855acd72";
    private const long SevenZipSize = 602112;

    /// <summary>O nome de proxy que o OptiScaler usa. `version.dll` e o slot que ele documenta e
    /// o que a comunidade usa: nao colide com dxgi.dll, que fica livre para o ReShade.</summary>
    private const string ProxyName = "version.dll";
    private const string IniName = "OptiScaler.ini";
    private const string OursSuffix = ".renodx-ours";
    private const string BackupSuffix = ".renodx-bak";

    /// <summary>
    /// Marca que foi ESTE launcher que pos o OptiScaler na pasta.
    ///
    /// E o que a desinstalacao consulta antes de chamar <see cref="Remove"/>, e o que o scanner
    /// de conflitos consulta para nao acusar a propria instalacao. Fica ao lado do ini, e nao do
    /// proxy, porque o nome do proxy e um detalhe que pode mudar; o ini nao.
    /// </summary>
    public const string OursMarker = IniName + OursSuffix;

    private static readonly string[] AllowedHosts =
    [
        "github.com", "api.github.com",
        "objects.githubusercontent.com", "release-assets.githubusercontent.com",
        "www.7-zip.org", "7-zip.org",
    ];

    public static string LibraryDir { get; } = Path.Combine(AppPaths.DataDir, "optiscaler");
    private static string LibraryDll => Path.Combine(LibraryDir, "OptiScaler.dll");
    private static string LibraryIni => Path.Combine(LibraryDir, IniName);
    private static string SevenZip => Path.Combine(LibraryDir, "7zr.exe");

    public static bool InLibrary => File.Exists(LibraryDll);

    public static bool IsDeployed(string targetDir) =>
        File.Exists(Path.Combine(targetDir, ProxyName))
        && File.Exists(Path.Combine(targetDir, IniName));

    /// <summary>Foi este launcher que pos o OptiScaler aqui? (ver <see cref="OursMarker"/>)</summary>
    public static bool IsOurs(string targetDir) => File.Exists(Path.Combine(targetDir, OursMarker));

    /// <summary>
    /// Foi este launcher que pos o OptiScaler aqui, contando as instalacoes de ANTES da marca?
    ///
    /// O build anterior implantava sem escrever marca nenhuma, e a desinstalacao, que so
    /// consultava a marca antes de chamar <see cref="Remove"/>, deixava para sempre o OptiScaler
    /// que o proprio launcher tinha posto. A assinatura que sobra desse build e o proxy identico
    /// byte a byte ao da biblioteca, ou a copia guardada `.renodx-bak` ao lado do proxy — que so
    /// este launcher cria. <see cref="IsOurs"/> continua respondendo so pela marca, para quem
    /// precisa da resposta estrita.
    /// </summary>
    public static bool IsOursOrLegacy(string targetDir)
    {
        var proxy = Path.Combine(targetDir, ProxyName);
        return IsOurs(targetDir)
               || File.Exists(proxy + OursSuffix)
               || File.Exists(proxy + BackupSuffix)
               || Iguais(proxy, LibraryDll);
    }

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
        await GarantirSevenZipAsync(http, progress, ct);

        // 2. o asset da release — o nome carrega data e build, entao fixa-lo numa URL
        //    "latest/download" quebraria na proxima versao.
        //
        //    Resolvido pela PAGINA de release, nao pela API: a API anonima limita a 60
        //    requisicoes por hora por IP, e quem instala em varios jogos estoura isso e recebe
        //    403 em tudo — sem nenhuma pista de que a causa e cota, nao rede.
        var url = await GitHubReleaseService.LatestAssetAsync(http, Repo, new Regex(@"\.7z$"), ct);
        if (url is null || !HostOk(url)) throw new InvalidOperationException(L.T("OptiScaler_NoAsset"));

        var pacote = Path.Combine(LibraryDir, "optiscaler.7z");
        try
        {
            var anunciado = await BaixarAsync(http, url, pacote, ct);
            await ConferirPacoteAsync(http, url, pacote, anunciado, ct);

            // O hash do extrator e conferido de novo aqui, e nao so no download: entre um e outro
            // o arquivo ficou em %AppData%, e o que vai receber Process.Start tem de ser o que
            // foi conferido.
            if (!ConfereSevenZip(SevenZip, out var motivo))
                throw new InvalidOperationException(L.T("OptiScaler_7zBadHash", motivo));

            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(SevenZip)
            {
                Arguments = $"x -y -o\"{LibraryDir}\" \"{pacote}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) throw new InvalidOperationException(L.T("OptiScaler_Incomplete"));
            await p.WaitForExitAsync(ct);
            // 7-Zip: 0 e sucesso, 1 e aviso nao fatal (algum arquivo travado), 2 para cima e erro
            // de verdade. Ignorar o codigo fazia uma extracao falha aparecer so como "nao
            // produziu OptiScaler.dll", sem dizer que foi o extrator que reclamou.
            if (p.ExitCode > 1) throw new InvalidOperationException(L.T("OptiScaler_ExtractFailed", p.ExitCode));
            if (p.ExitCode == 1) Log.Warn("optiscaler: 7zr.exe terminou com aviso (codigo 1)");

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
                var ini = Directory.EnumerateFiles(LibraryDir, IniName, SearchOption.AllDirectories)
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
    /// Deixa na biblioteca um 7zr.exe que bate no hash fixado — baixando se nao houver, e
    /// trocando o que houver se nao bater.
    ///
    /// O que ja esta na biblioteca e conferido a cada uso, nao so no download: um download
    /// interrompido deixava um exe truncado que "existia" e era executado para sempre, e um build
    /// de antes de o hash ser fixo tem de dar lugar ao fixado.
    /// </summary>
    private static async Task GarantirSevenZipAsync(HttpClient http, IProgress<string>? progress, CancellationToken ct)
    {
        if (File.Exists(SevenZip))
        {
            if (ConfereSevenZip(SevenZip, out var motivoLocal)) return;
            Log.Warn($"optiscaler: 7zr.exe da biblioteca descartado ({motivoLocal})");
            File.Delete(SevenZip);
        }

        progress?.Report(L.T("OptiScaler_Fetching7z"));
        string? motivoHash = null;
        Exception? falhaRede = null;
        foreach (var url in new[] { SevenZipUrl, SevenZipUrlFallback })
        {
            try
            {
                await BaixarAsync(http, url, SevenZip, ct);
                if (ConfereSevenZip(SevenZip, out var motivo))
                {
                    Log.Info($"optiscaler: 7zr.exe {SevenZipVersion} conferido por SHA-256 ({url})");
                    return;
                }
                motivoHash = motivo;
                Log.Warn($"optiscaler: 7zr.exe de {url} recusado ({motivo})");
                try { File.Delete(SevenZip); } catch { }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                falhaRede = ex;
                Log.Warn($"optiscaler: 7zr.exe de {url}: {ex.Message}");
            }
        }
        // Um hash errado e mais grave (e mais raro) do que rede fora do ar, e e o que o usuario
        // precisa ler se aconteceu — um proxy ou antivirus reescrevendo o download.
        if (motivoHash is not null) throw new InvalidOperationException(L.T("OptiScaler_7zBadHash", motivoHash));
        throw falhaRede ?? new InvalidOperationException(L.T("OptiScaler_Incomplete"));
    }

    /// <summary>Tamanho, cabecalho MZ e SHA-256 do 7zr.exe, contra os valores fixados.</summary>
    private static bool ConfereSevenZip(string caminho, out string motivo)
    {
        try
        {
            var tamanho = new FileInfo(caminho).Length;
            if (tamanho != SevenZipSize) { motivo = $"tamanho {tamanho} != {SevenZipSize}"; return false; }
            if (!TemMagia(caminho, "MZ"u8.ToArray())) { motivo = "sem cabecalho MZ"; return false; }
            var hash = Sha256(caminho);
            if (!hash.Equals(SevenZipSha256, StringComparison.OrdinalIgnoreCase))
            { motivo = $"SHA-256 {hash} != {SevenZipSha256}"; return false; }
            motivo = "ok";
            return true;
        }
        catch (Exception ex) { motivo = ex.Message; return false; }
    }

    /// <summary>
    /// O .7z e o que a release publicou?
    ///
    /// O GitHub publica o SHA-256 de cada asset na pagina `expanded_assets` — a mesma pagina, sem
    /// cota, que ja resolve a URL. Quando ele esta la, e conferido; quando nao esta (release
    /// antiga, pagina mudou de formato), sobra o que da para conferir sem ele — cabecalho do
    /// formato e tamanho contra o Content-Length — e o log diz que o hash NAO foi conferido, para
    /// nao parecer que foi.
    /// </summary>
    private static async Task ConferirPacoteAsync(HttpClient http, string url, string pacote, long anunciado,
                                                  CancellationToken ct)
    {
        var tamanho = new FileInfo(pacote).Length;
        // Um OptiScaler real tem dezenas de MB; algo com poucos KB e uma pagina de erro salva como
        // arquivo, e um tamanho diferente do anunciado e um download que parou no meio.
        if (tamanho < 1L * 1024 * 1024 || (anunciado > 0 && tamanho != anunciado))
            throw new InvalidOperationException(L.T("OptiScaler_ArchiveCorrupt"));
        if (!TemMagia(pacote, new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }))
            throw new InvalidOperationException(L.T("OptiScaler_ArchiveCorrupt"));

        var nome = Uri.UnescapeDataString(Path.GetFileName(url));
        var publicado = await DigestPublicadoAsync(http, url, nome, ct);
        if (publicado is null)
        {
            Log.Warn($"optiscaler: {nome} sem digest publicado na release; conferidos SO formato e tamanho ({tamanho} bytes)");
            return;
        }
        var hash = Sha256(pacote);
        if (!hash.Equals(publicado, StringComparison.OrdinalIgnoreCase))
        {
            Log.Warn($"optiscaler: {nome} SHA-256 {hash} != publicado {publicado}");
            throw new InvalidOperationException(L.T("OptiScaler_ArchiveBadHash"));
        }
        Log.Info($"optiscaler: {nome} conferido pelo SHA-256 publicado na release");
    }

    /// <summary>O SHA-256 que a pagina de assets da release mostra para este arquivo, ou null.</summary>
    private static async Task<string?> DigestPublicadoAsync(HttpClient http, string url, string nome, CancellationToken ct)
    {
        try
        {
            var m = Regex.Match(url, @"/releases/download/([^/]+)/[^/]+$");
            if (!m.Success) return null;
            var tag = Uri.UnescapeDataString(m.Groups[1].Value);
            var pagina = $"https://github.com/{Repo}/releases/expanded_assets/{Uri.EscapeDataString(tag)}";
            if (!HostOk(pagina)) return null;
            var html = await http.GetStringAsync(pagina, ct);
            // O botao "copiar digest" carrega o nome do asset no aria-label e o hash no value.
            var d = Regex.Match(html,
                @"digest for\s+" + Regex.Escape(nome) + @"""[^>]*value=""sha256:([0-9a-fA-F]{64})""");
            return d.Success ? d.Groups[1].Value : null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Warn($"optiscaler: digest da release: {ex.Message}"); return null; }
    }

    private static bool TemMagia(string caminho, byte[] magia)
    {
        using var fs = File.OpenRead(caminho);
        var buf = new byte[magia.Length];
        return fs.Read(buf, 0, buf.Length) == buf.Length && buf.AsSpan().SequenceEqual(magia);
    }

    private static string Sha256(string caminho)
    {
        using var fs = File.OpenRead(caminho);
        return Convert.ToHexString(SHA256.HashData(fs));
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
        var backup = destino + BackupSuffix;
        var marca = destino + OursSuffix;
        // O jogo pode ter um version.dll proprio (raro, mas acontece com mods antigos). Guardar
        // antes de sobrescrever — desde que nao seja o NOSSO, de uma instalacao anterior: sem a
        // marca, uma reinstalacao guardava o proprio OptiScaler como "original" e a remocao o
        // devolvia em vez de tira-lo.
        if (File.Exists(destino) && !File.Exists(backup) && !File.Exists(marca) && !Iguais(destino, LibraryDll))
            File.Copy(destino, backup);
        File.Copy(LibraryDll, destino, overwrite: true);
        Marcar(marca);

        var iniDest = Path.Combine(targetDir, IniName);
        var iniBackup = iniDest + BackupSuffix;
        if (File.Exists(iniDest))
        {
            // Um ini que ja estava aqui e do usuario — um OptiScaler proprio, afinado a mao — e
            // continua valendo, porque o nosso OptiScaler le o mesmo arquivo. Fica no lugar, mas
            // com uma copia guardada: e ela que a remocao devolve, em vez de apagar o ini como se
            // fosse nosso. Um ini identico ao da biblioteca e nosso de uma instalacao anterior.
            if (!File.Exists(iniBackup) && !IsOurs(targetDir) && !Iguais(iniDest, LibraryIni))
                File.Copy(iniDest, iniBackup);
        }
        else if (File.Exists(LibraryIni)) File.Copy(LibraryIni, iniDest);
        Marcar(Path.Combine(targetDir, OursMarker));

        progress?.Report(L.T("OptiScaler_Deployed"));
        Log.Info($"optiscaler: {ProxyName} implantado em {targetDir}");
    }

    /// <summary>
    /// Tira o OptiScaler e devolve o que havia antes.
    ///
    /// So sai o que e nosso: o que tem copia guardada volta ao nome original; o que tem marca (ou
    /// e identico ao da biblioteca, de uma instalacao anterior a marca) e apagado; o que nao e
    /// nem uma coisa nem outra fica, porque apagar sem copia nao se desfaz.
    ///
    /// Uma copia guardada que e o NOSSO proprio arquivo nao e o estado de antes de nos: o build
    /// anterior, ao reinstalar por cima de uma instalacao sem marca, guardava o OptiScaler que ele
    /// mesmo tinha posto como se fosse do usuario, e devolve-la seria reinstalar. Ela e descartada,
    /// e o arquivo que ela "protegia" sai pela mesma prova.
    /// </summary>
    public static void Remove(string targetDir)
    {
        try
        {
            // Decidido antes de mexer em qualquer arquivo: a assinatura das instalacoes de antes
            // da marca e o proprio proxy, que sai logo abaixo.
            var nosso = IsOursOrLegacy(targetDir);

            var alvo = Path.Combine(targetDir, ProxyName);
            var backup = alvo + BackupSuffix;
            var marca = alvo + OursSuffix;
            if (File.Exists(backup) && Iguais(backup, LibraryDll))
            {
                Log.Info($"optiscaler remove: {ProxyName}{BackupSuffix} e o nosso proprio arquivo; descartado");
                File.Delete(backup);
            }
            if (File.Exists(backup)) { File.Copy(backup, alvo, overwrite: true); File.Delete(backup); }
            else if (File.Exists(alvo) && (nosso || File.Exists(marca))) File.Delete(alvo);
            else if (File.Exists(alvo)) Log.Warn($"optiscaler remove: {ProxyName} em {targetDir} nao e nosso; fica");
            if (File.Exists(marca)) File.Delete(marca);

            var ini = Path.Combine(targetDir, IniName);
            var iniBackup = ini + BackupSuffix;
            var iniMarca = Path.Combine(targetDir, OursMarker);
            var iniEraNosso = false;
            if (File.Exists(iniBackup) && Iguais(iniBackup, LibraryIni))
            {
                Log.Info($"optiscaler remove: {IniName}{BackupSuffix} e o nosso proprio arquivo; descartado");
                File.Delete(iniBackup);
            }
            if (File.Exists(iniBackup)) { File.Copy(iniBackup, ini, overwrite: true); File.Delete(iniBackup); }
            else if (File.Exists(ini) && (File.Exists(iniMarca) || Iguais(ini, LibraryIni)))
            { File.Delete(ini); iniEraNosso = true; }
            // Instalacao de antes da marca com um ini que ja nao bate com o da biblioteca: o
            // OptiScaler o reescreve quando o usuario salva no overlay, e nao ha copia guardada
            // que diga o que havia antes. Sem o proxy ele e inerte, e apagar sem copia nao se
            // desfaz — fica, e o log diz por que.
            else if (File.Exists(ini) && nosso)
                Log.Warn($"optiscaler remove: {IniName} em {targetDir} foi alterado depois de instalado e nao tem copia; fica");
            if (File.Exists(iniMarca)) File.Delete(iniMarca);

            // O log e de quem rodou. Se o ini era do usuario, o OptiScaler tambem era, e o log
            // fica com ele.
            var log = Path.Combine(targetDir, "OptiScaler.log");
            if (iniEraNosso && File.Exists(log)) File.Delete(log);
        }
        catch (Exception ex) { Log.Warn($"optiscaler remove {targetDir}: {ex.Message}"); }
    }

    private static void Marcar(string caminho)
    {
        try { File.WriteAllText(caminho, DateTime.UtcNow.ToString("o")); }
        catch (Exception ex) { Log.Warn($"optiscaler mark {Path.GetFileName(caminho)}: {ex.Message}"); }
    }

    /// <summary>Os dois arquivos existem e sao iguais byte a byte?</summary>
    private static bool Iguais(string a, string b)
    {
        try
        {
            if (!File.Exists(a) || !File.Exists(b)) return false;
            if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
            using var fa = File.OpenRead(a);
            using var fb = File.OpenRead(b);
            var ba = new byte[1 << 16];
            var bb = new byte[1 << 16];
            int na;
            while ((na = fa.Read(ba, 0, ba.Length)) > 0)
            {
                var nb = fb.ReadAtLeast(bb.AsSpan(0, na), na, throwOnEndOfStream: false);
                if (nb != na || !ba.AsSpan(0, na).SequenceEqual(bb.AsSpan(0, nb))) return false;
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Baixa para um `.part` e so renomeia no fim: um download interrompido nao pode deixar um
    /// arquivo pela metade com o nome do arquivo bom. Devolve o Content-Length anunciado (ou -1).
    /// </summary>
    private static async Task<long> BaixarAsync(HttpClient http, string url, string destino, CancellationToken ct)
    {
        if (!HostOk(url)) throw new InvalidOperationException(L.T("OptiScaler_BadHost", url));
        Directory.CreateDirectory(Path.GetDirectoryName(destino)!);
        var parcial = destino + ".part";
        try
        {
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            var anunciado = resp.Content.Headers.ContentLength ?? -1;
            await using (var origem = await resp.Content.ReadAsStreamAsync(ct))
            await using (var arquivo = File.Create(parcial))
                await origem.CopyToAsync(arquivo, ct);
            File.Move(parcial, destino, overwrite: true);
            return anunciado;
        }
        catch
        {
            try { File.Delete(parcial); } catch { }
            throw;
        }
    }
}
