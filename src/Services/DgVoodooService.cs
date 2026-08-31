using System.IO;
using System.IO.Compression;
using System.Net.Http;
using RenoDXLauncher.Localization;

namespace RenoDXLauncher.Services;

/// <summary>
/// dgVoodoo2: traduz Direct3D 9 para Direct3D 11, e com isso um jogo DX9 vira o caso comum.
///
/// Por que nao da para atender D3D9 direto, nas palavras do guia do Feeder: o ReShade em D3D9
/// para no Shader Model 3, entao NENHUM provedor de motion vectors compila; e o D3D9 nao tem
/// handle NT compartilhado nem fence, que e como as texturas atravessam para o device D3D12 onde
/// o pass neural roda. Nao e questao de esforco — as duas pecas nao existem naquela API.
///
/// Traduzindo para D3D11 antes, os dois problemas somem de uma vez: shaders SM5 compilam e as
/// texturas passam a ser compartilhaveis.
///
/// O dgVoodoo e de terceiro (dege-diosg) e tem release publica com URL estavel, entao segue a
/// mesma regra da ponte e do Feeder: baixado da fonte, nunca empacotado por nos.
/// </summary>
public static class DgVoodooService
{
    private const string Repo = "dege-diosg/dgVoodoo2";

    /// <summary>
    /// A 2.83.2, de um repositorio de preservacao — e nao a mais nova.
    ///
    /// Isto contraria o instinto e foi medido: as versoes 2.87.x dao ACCESS VIOLATION ao
    /// inicializar em GPU Blackwell (RTX 50). Reproduzido fora de qualquer jogo, com um programa
    /// de 40 linhas que so chama `Direct3DCreate9Ex` — 2.87.1, 2.87.2 e 2.87.3 morrem no mesmo
    /// offset; 2.81.3 e 2.83.2 criam o device normalmente, na mesma maquina, mesmo driver, mesmo
    /// Windows. Com a 2.83.2 o Saints Row 2 abre e roda DLSS 5; com a 2.87.3 nem inicia.
    ///
    /// O autor REMOVEU as versoes antigas do download — uma issue do proprio repositorio diz
    /// "works fine with 2.81.3 and older, which were intentionally removed" — e por isso o
    /// GitHub dele so tem 2.87.x. O manifesto do RHI chega a mesma conclusao por outro caminho:
    /// "requires dgVoodoo2 v2.81.3 or v2.87.3".
    ///
    /// Quando sair uma versao que corrija a regressao, basta trocar esta constante.
    /// </summary>
    private const string ZipUrl =
        "https://raw.githubusercontent.com/masterotaku/dgVoodoo-binaries/main/dgVoodoo2_83_2.zip";

    /// <summary>Ultimo recurso, se o mirror nao responder. E a versao quebrada em Blackwell, mas
    /// um jogo que nao abre e melhor do que uma instalacao que nem se monta.</summary>
    private const string ZipUrlFallback =
        "https://github.com/dege-diosg/dgVoodoo2/releases/latest/download/dgVoodoo2_87_3.zip";

    private static readonly string[] AllowedHosts =
    [
        "github.com", "api.github.com",
        "objects.githubusercontent.com", "release-assets.githubusercontent.com",
        // o mirror de preservacao das versoes que o autor tirou do ar
        "raw.githubusercontent.com",
    ];

    private static bool HostOk(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
        && u.Scheme == Uri.UriSchemeHttps
        && AllowedHosts.Contains(u.Host, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A versao FIXA da preservacao, com a release oficial so como ultimo recurso.
    ///
    /// Aqui houve uma inversao deliberada. A primeira versao deste metodo perguntava a API qual
    /// era a release mais nova, para nao envelhecer sozinha — o que e a decisao certa em quase
    /// todo componente e e errada neste. As 2.87.x regrediram em GPU Blackwell (ver
    /// <see cref="ZipUrl"/>), entao "sempre a mais nova" significaria "sempre a quebrada" para
    /// quem tem RTX 50, que e exatamente o publico que instala DLSS 5.
    ///
    /// Se o mirror sair do ar, a release oficial ainda deixa o launcher montar a instalacao — um
    /// jogo que nao abre e reversivel; uma instalacao que nao se monta deixa o usuario sem nada.
    /// </summary>
    private static async Task<string> ResolverZipAsync(HttpClient http, CancellationToken ct)
    {
        try
        {
            using var resp = await http.GetAsync(ZipUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.IsSuccessStatusCode && HostOk(ZipUrl))
            {
                Log.Info("dgvoodoo: usando a 2.83.2 (as 2.87.x quebram em Blackwell)");
                return ZipUrl;
            }
            Log.Warn($"dgvoodoo: mirror respondeu {(int)resp.StatusCode}; caindo na release oficial");
        }
        catch (Exception ex) { Log.Warn($"dgvoodoo: mirror indisponivel ({ex.Message})"); }
        return ZipUrlFallback;
    }

    private const string ConfFile = "dgVoodoo.conf";
    private const string D3d9File = "D3D9.dll";
    private const string CplFile = "dgVoodooCpl.exe";

    public static string LibraryDir { get; } = Path.Combine(AppPaths.DataDir, "dgvoodoo");
    private static string LibraryConf { get; } = Path.Combine(LibraryDir, ConfFile);
    private static string LibraryX64 { get; } = Path.Combine(LibraryDir, "x64", D3d9File);
    private static string LibraryX86 { get; } = Path.Combine(LibraryDir, "x86", D3d9File);
    private static string LibraryCpl { get; } = Path.Combine(LibraryDir, CplFile);

    public static bool InLibrary =>
        File.Exists(LibraryConf) && File.Exists(LibraryX64) && File.Exists(LibraryX86)
        && File.Exists(LibraryCpl);

    /// <summary>O dgVoodoo ja esta na pasta do jogo?</summary>
    public static bool IsDeployed(string targetDir) =>
        File.Exists(Path.Combine(targetDir, D3d9File)) && File.Exists(Path.Combine(targetDir, ConfFile));

    /// <summary>
    /// Este jogo precisa da traducao?
    ///
    /// So quando o executavel e D3D9 de verdade: importa d3d9.dll e nao alcanca nenhuma API
    /// moderna. Um build da Unreal linka d3d9, d3d11 e d3d12 de uma vez, e envolver esse jogo
    /// seria trocar o caminho nativo por um emulado sem ganho nenhum.
    /// </summary>
    public static bool Applies(string? exePath)
    {
        if (exePath is null) return false;
        var pe = PeUtils.Inspect(exePath);
        if (pe is null) return false;

        // Duas formas de um jogo ser D3D9, e exigir so a primeira custou caro.
        //
        // O `Bully.exe` NAO importa d3d9.dll: ele importa `d3dx9_38.dll` e pega o d3d9 por
        // LoadLibrary. Como a tabela de importacao nao mencionava d3d9, o launcher recusava um
        // jogo que a comunidade demonstrou funcionar por esta mesma rota (PR #9 do Feeder:
        // "32-bit, Gamebryo, 2008, real D3D9 through dgVoodoo2").
        //
        // A D3DX9 e a biblioteca auxiliar do D3D9 e de mais nada — quem a linka renderiza em
        // D3D9. E um sinal tao bom quanto o import direto, e cobre os jogos que resolvem a API
        // em tempo de execucao.
        var importaD3d9 = pe.Imports.Any(i => i.Equals("d3d9.dll", StringComparison.OrdinalIgnoreCase));
        var importaD3dx9 = pe.Imports.Any(i => i.StartsWith("d3dx9_", StringComparison.OrdinalIgnoreCase));
        if (!importaD3d9 && !importaD3dx9) return false;

        // Quem linka a D3DX9 esta decidido: e D3D9. A varredura de strings abaixo existe para o
        // caso ambiguo (importa d3d9 e pode renderizar em outra coisa) e aqui so causaria falso
        // negativo — o Bully carrega as strings "d3d10.dll" e "dxgi.dll" sem renderizar em
        // nenhuma das duas.
        if (importaD3dx9 && !pe.Imports.Any(i =>
                i.Equals("d3d10.dll", StringComparison.OrdinalIgnoreCase)
                || i.Equals("d3d11.dll", StringComparison.OrdinalIgnoreCase)
                || i.Equals("d3d12.dll", StringComparison.OrdinalIgnoreCase)))
            return true;

        // A tabela de importacao nao basta, e por dois motivos opostos.
        //
        // O Just Cause 2 IMPORTA d3d9.dll e mesmo assim renderiza em Direct3D 10: as strings
        // "d3d10.dll" e "dxgi.dll" estao no binario, carregadas por LoadLibrary, e o import de
        // d3d9 e um caminho de fallback que ele nunca usa. Envolve-lo trocaria o caminho nativo
        // por um emulado, sem ganho e com risco.
        //
        // D3D10 conta como moderna aqui: ela ja tem shaders SM4 e recursos compartilhaveis, que
        // e o que faltava ao D3D9 e o unico motivo de existir esta traducao.
        // dxgi.dll NAO entra nesta lista. Ele nao e uma API de renderizacao: jogos D3D9 tardios
        // o usam para enumerar adaptadores e modos de tela, e o Bayonetta e um deles. Trata-lo
        // como "moderna" recusava a traducao justamente nos jogos que ela existe para atender.
        //
        // Quem decide sao os devices de verdade — D3D10 para cima.
        foreach (var api in new[] { "d3d10.dll", "d3d10_1.dll", "d3d11.dll", "d3d12.dll" })
        {
            if (pe.Imports.Any(i => i.Equals(api, StringComparison.OrdinalIgnoreCase))) return false;
            if (ContemTexto(exePath, api)) return false;
        }
        return true;
    }

    /// <summary>Procura uma string ASCII no arquivo sem carrega-lo inteiro na memoria.</summary>
    private static bool ContemTexto(string path, string alvo)
    {
        try
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(alvo);
            using var fs = File.OpenRead(path);
            var buf = new byte[1 << 20];
            var carry = bytes.Length - 1;
            var anterior = new byte[carry];
            var temAnterior = false;
            int n;
            while ((n = fs.Read(buf, 0, buf.Length)) > 0)
            {
                var janela = temAnterior ? anterior.Concat(buf.Take(n)).ToArray() : buf.Take(n).ToArray();
                if (System.Text.Encoding.ASCII.GetString(janela)
                        .Contains(alvo, StringComparison.OrdinalIgnoreCase)) return true;
                if (n >= carry) { Array.Copy(buf, n - carry, anterior, 0, carry); temAnterior = true; }
            }
        }
        catch (Exception ex) { Log.Warn($"dgvoodoo probe {path}: {ex.Message}"); }
        return false;
    }

    public static async Task FetchAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (InLibrary) return;
        Directory.CreateDirectory(LibraryDir);
        progress?.Report(L.T("DgVoodoo_Fetching"));

        var zip = Path.Combine(LibraryDir, "dgvoodoo.zip");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RenoDXLauncher/1.0");
        try
        {
            var zipUrl = await ResolverZipAsync(http, ct);
            using (var resp = await http.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                await using var origem = await resp.Content.ReadAsStreamAsync(ct);
                await using var arquivo = File.Create(zip);
                await origem.CopyToAsync(arquivo, ct);
            }

            using var pacote = ZipFile.OpenRead(zip);
            foreach (var entrada in pacote.Entries)
            {
                if (entrada.Length == 0) continue;
                var rel = entrada.FullName.Replace('\\', '/');
                // zip-slip: o nome vem de um download, e ".." nele escreveria fora da biblioteca.
                if (rel.Contains("..")) continue;

                var destino = rel switch
                {
                    "dgVoodoo.conf" => LibraryConf,
                    "MS/x64/D3D9.dll" => LibraryX64,
                    "MS/x86/D3D9.dll" => LibraryX86,
                    "dgVoodooCpl.exe" => LibraryCpl,
                    _ => null,
                };
                if (destino is null) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(destino)!);
                entrada.ExtractToFile(destino, overwrite: true);
            }

            if (!InLibrary) throw new InvalidOperationException(L.T("DgVoodoo_Incomplete"));
        }
        finally
        {
            try { File.Delete(zip); } catch { }
        }
    }

    /// <summary>
    /// Poe o wrapper na pasta do jogo e escreve a configuracao que o pass neural exige.
    ///
    /// Tres ajustes, e nenhum e preferencia — sem eles o guia do Feeder diz que nao funciona:
    ///
    ///   DisableAndPassThru=false  o padrao historico era true, e nesse modo o dgVoodoo repassa
    ///                             tudo ao D3D9 real e nao faz absolutamente nada;
    ///   VRAM>=1024                o padrao de 256 MB derruba o jogo;
    ///   OutputAPI=d3d11_fl11_0    "bestavailable" pode cair num nivel que nao serve ao contrato.
    /// </summary>
    public static void Deploy(string targetDir, bool jogo64Bits, IProgress<string>? progress = null)
    {
        if (!InLibrary) throw new InvalidOperationException(L.T("DgVoodoo_NotInLibrary"));

        var origem = jogo64Bits ? LibraryX64 : LibraryX86;
        var destino = Path.Combine(targetDir, D3d9File);
        // O jogo pode ter um d3d9.dll proprio (outro wrapper, um mod antigo). Guardar antes de
        // sobrescrever e o que permite desfazer.
        var backup = destino + ".renodx-bak";
        if (File.Exists(destino) && !File.Exists(backup)) File.Copy(destino, backup);
        File.Copy(origem, destino, overwrite: true);

        EscreverConf(targetDir);

        // O painel nao participa da execucao — o wrapper le o .conf direto. Ele vai junto porque
        // e a unica forma de o usuario ligar a marca d'agua, que o guia chama de "sua prova de
        // que o dgVoodoo esta mesmo rodando". Num jogo que abre em tela preta ou nem abre, essa
        // e a diferenca entre diagnosticar e adivinhar. Ele so edita o .conf ao lado dele.
        try
        {
            if (File.Exists(LibraryCpl))
                File.Copy(LibraryCpl, Path.Combine(targetDir, CplFile), overwrite: true);
        }
        catch (Exception ex) { Log.Warn($"dgvoodoo cpl: {ex.Message}"); }

        progress?.Report(L.T("DgVoodoo_Deployed"));
        Log.Info($"dgvoodoo: D3D9.dll ({(jogo64Bits ? "x64" : "x86")}) implantado em {targetDir}");
    }

    /// <summary>Copia o conf de referencia e corrige as tres chaves, preservando o resto do
    /// arquivo — ele tem 22 KB de comentarios que explicam cada opcao.</summary>
    private static void EscreverConf(string targetDir)
    {
        var destino = Path.Combine(targetDir, ConfFile);
        if (!File.Exists(destino)) File.Copy(LibraryConf, destino);

        var linhas = File.ReadAllLines(destino);
        var secao = "";
        for (int i = 0; i < linhas.Length; i++)
        {
            var t = linhas[i].TrimStart();
            if (t.StartsWith('[')) { secao = t.Trim(); continue; }
            if (t.StartsWith(';')) continue; // comentario, nao e chave

            linhas[i] = (secao, Chave(t)) switch
            {
                ("[General]", "OutputAPI") => "OutputAPI = d3d11_fl11_0",
                ("[DirectX]", "DisableAndPassThru") => "DisableAndPassThru = false",
                ("[DirectX]", "VRAM") => "VRAM = 1024",
                _ => linhas[i],
            };
        }
        File.WriteAllLines(destino, linhas);
    }

    private static string Chave(string linha)
    {
        var i = linha.IndexOf('=');
        return i < 0 ? "" : linha[..i].Trim();
    }

    /// <summary>Tira o wrapper e devolve o d3d9.dll que estava ali antes, se havia um.</summary>
    public static void Remove(string targetDir)
    {
        try
        {
            var alvo = Path.Combine(targetDir, D3d9File);
            var backup = alvo + ".renodx-bak";
            if (File.Exists(backup)) { File.Copy(backup, alvo, overwrite: true); File.Delete(backup); }
            else if (File.Exists(alvo)) File.Delete(alvo);

            // O conf e o painel sao nossos; sem o wrapper nao servem a nada e so confundem quem
            // for olhar a pasta depois.
            foreach (var f in new[] { ConfFile, CplFile })
            {
                var p = Path.Combine(targetDir, f);
                if (File.Exists(p)) File.Delete(p);
            }
        }
        catch (Exception ex) { Log.Warn($"dgvoodoo remove {targetDir}: {ex.Message}"); }
    }
}
