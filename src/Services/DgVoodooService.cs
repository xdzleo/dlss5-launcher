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

    /// <summary>Marca o que este launcher escreveu; `.renodx-bak` guarda o que era de outra pessoa.
    /// Os dois juntos sao o que permite a remocao devolver a pasta ao que era.</summary>
    private const string OursSuffix = ".renodx-ours";
    private const string BackupSuffix = ".renodx-bak";

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

        // Executavel EMPACOTADO: a tabela de importacao nao existe para ser lida.
        //
        // O `HitmanBloodMoney.exe` importa exatamente uma DLL — `kernel32.dll` — e mais nada. Isso
        // nao e um jogo sem API grafica: e a assinatura de um protetor de 2006 (SecuROM, SafeDisc)
        // que remonta os imports em tempo de execucao. Nenhuma varredura estatica vai achar d3d9
        // ali, nem no import nem nas strings, porque o binario esta comprimido.
        //
        // O estrago era silencioso e grave: sem sinal de D3D9, `ReachesD3D12` respondia "sim" (o
        // padrao permissivo do silencio), o jogo era tratado como se alcancasse D3D12, e o
        // launcher instalava o Feeder SEM tradutor nenhum — mais um proxy dxgi.dll que um jogo
        // D3D9 nunca carrega. A cadeia ficava toda verde e nada rodava.
        //
        // Quando o proprio binario nao pode falar, a pasta fala. E a mesma qualidade de evidencia
        // que ja aceitamos do import de D3DX9 ("quem linka a D3DX9 esta decidido"), so que lida de
        // outro lugar.
        if (!importaD3d9 && !importaD3dx9 && EhEmpacotado(pe) && PastaEhD3d9(exePath)) return true;

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
    private static bool ContemTexto(string path, string alvo) =>
        // Uma implementacao so, rapida e com cache, em PeUtils: eram quatro copias identicas
        // varrendo o executavel inteiro, e um clique chamava varias delas.
           PeUtils.ContemTexto(path, alvo);

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
        // sobrescrever e o que permite desfazer — desde que o que esta la nao seja o NOSSO, de
        // uma instalacao anterior. Uma reinstalacao guardava o proprio wrapper como "original",
        // e a remocao o devolvia sem o conf: um dgVoodoo rodando nos padroes que derrubam o jogo.
        Guardar(destino, ehNosso: EhNossoD3d9(targetDir));
        File.Copy(origem, destino, overwrite: true);
        Marcar(destino);

        EscreverConf(targetDir);

        // O painel nao participa da execucao — o wrapper le o .conf direto. Ele vai junto porque
        // e a unica forma de o usuario ligar a marca d'agua, que o guia chama de "sua prova de
        // que o dgVoodoo esta mesmo rodando". Num jogo que abre em tela preta ou nem abre, essa
        // e a diferenca entre diagnosticar e adivinhar. Ele so edita o .conf ao lado dele.
        try
        {
            if (File.Exists(LibraryCpl))
            {
                var cpl = Path.Combine(targetDir, CplFile);
                Guardar(cpl, ehNosso: EhNossoCpl(cpl));
                File.Copy(LibraryCpl, cpl, overwrite: true);
                Marcar(cpl);
            }
        }
        catch (Exception ex) { Log.Warn($"dgvoodoo cpl: {ex.Message}"); }

        progress?.Report(L.T("DgVoodoo_Deployed"));
        Log.Info($"dgvoodoo: D3D9.dll ({(jogo64Bits ? "x64" : "x86")}) implantado em {targetDir}");
    }

    /// <summary>
    /// Copia o conf de referencia e corrige as tres chaves, preservando o resto do arquivo — ele
    /// tem 22 KB de comentarios que explicam cada opcao.
    ///
    /// Um conf que JA estava na pasta e do usuario (um dgVoodoo instalado a mao, afinado): as
    /// tres chaves sao corrigidas nele mesmo, porque o resto e escolha dele e continua valendo,
    /// mas a versao intocada vai para `.renodx-bak` antes. E ela que a remocao devolve — antes a
    /// remocao apagava o conf como se fosse nosso, e era a unica copia que existia.
    /// </summary>
    private static void EscreverConf(string targetDir)
    {
        var destino = Path.Combine(targetDir, ConfFile);
        if (File.Exists(destino)) Guardar(destino, ehNosso: EhNossoConf(destino));
        else File.Copy(LibraryConf, destino);

        File.WriteAllLines(destino, Ajustar(File.ReadAllLines(destino)));
        Marcar(destino);
    }

    /// <summary>As tres chaves que o pass neural exige, sobre as linhas de um conf qualquer.</summary>
    private static string[] Ajustar(string[] linhas)
    {
        var saida = (string[])linhas.Clone();
        var secao = "";
        for (int i = 0; i < saida.Length; i++)
        {
            var t = saida[i].TrimStart();
            if (t.StartsWith('[')) { secao = t.Trim(); continue; }
            if (t.StartsWith(';')) continue; // comentario, nao e chave

            saida[i] = (secao, Chave(t)) switch
            {
                ("[General]", "OutputAPI") => "OutputAPI = d3d11_fl11_0",
                ("[DirectX]", "DisableAndPassThru") => "DisableAndPassThru = false",
                ("[DirectX]", "VRAM") => "VRAM = 1024",
                _ => saida[i],
            };
        }
        return saida;
    }

    private static string Chave(string linha)
    {
        var i = linha.IndexOf('=');
        return i < 0 ? "" : linha[..i].Trim();
    }

    /// <summary>
    /// Este d3d9.dll foi este launcher que pos?
    ///
    /// A marca `.renodx-ours` responde para tudo que foi instalado depois de ela existir. Para o
    /// que veio antes, a assinatura de uma instalacao nossa e o binario identico ao da biblioteca
    /// — e SO ele. A primeira versao exigia tambem o conf identico ao que este launcher escreve,
    /// e isso deixava dgVoodoo para tras: o proprio launcher manda o usuario abrir o
    /// dgVoodooCpl.exe, que reescreve o conf inteiro, e a partir dai a desinstalacao lia a
    /// instalacao como alheia e a deixava na pasta. O conf nao e prova de nada sobre o binario.
    ///
    /// O binario e desta versao exata, que o autor tirou do ar e que chega aqui pelo mirror de
    /// preservacao; um usuario com a mesma copia perde, no pior caso, um arquivo que a biblioteca
    /// reproduz byte a byte — e o conf dele, esse sim, tem copia guardada e volta.
    /// </summary>
    private static bool EhNossoD3d9(string targetDir)
    {
        var d3d9 = Path.Combine(targetDir, D3d9File);
        return File.Exists(d3d9 + OursSuffix) || EhBinarioDaBiblioteca(d3d9);
    }

    /// <summary>E o wrapper que este launcher distribui, em qualquer dos dois bitness?</summary>
    private static bool EhBinarioDaBiblioteca(string d3d9) =>
        Iguais(d3d9, LibraryX64) || Iguais(d3d9, LibraryX86);

    private static bool EhNossoConf(string conf) =>
        File.Exists(conf + OursSuffix) || ConfEhORenderizado(conf);

    private static bool EhNossoCpl(string cpl) =>
        File.Exists(cpl + OursSuffix) || Iguais(cpl, LibraryCpl);

    /// <summary>O conf e, linha a linha, o que EscreverConf produz a partir do de referencia?</summary>
    private static bool ConfEhORenderizado(string conf)
    {
        try
        {
            if (!File.Exists(conf) || !File.Exists(LibraryConf)) return false;
            return File.ReadAllLines(conf).SequenceEqual(Ajustar(File.ReadAllLines(LibraryConf)));
        }
        catch { return false; }
    }

    /// <summary>Guarda como `.renodx-bak` o que ja estava na pasta, se for de outra pessoa e ainda
    /// nao houver copia. A primeira copia e a que vale: e o estado de antes de nos.</summary>
    private static void Guardar(string caminho, bool ehNosso)
    {
        var backup = caminho + BackupSuffix;
        if (File.Exists(caminho) && !File.Exists(backup) && !ehNosso) File.Copy(caminho, backup);
    }

    private static void Marcar(string caminho)
    {
        try { File.WriteAllText(caminho + OursSuffix, DateTime.UtcNow.ToString("o")); }
        catch (Exception ex) { Log.Warn($"dgvoodoo mark {Path.GetFileName(caminho)}: {ex.Message}"); }
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
    /// Tira o wrapper e devolve a pasta ao que era: o d3d9.dll, o conf e o painel que estavam ali
    /// antes voltam do `.renodx-bak`; o que era nosso sai; o que nao e nem uma coisa nem outra
    /// fica, porque apagar sem copia nao se desfaz.
    /// </summary>
    public static void Remove(string targetDir)
    {
        try
        {
            // Decidido antes de mexer em qualquer arquivo: a assinatura das instalacoes de antes
            // da marca e o proprio wrapper, que sai logo abaixo — e o painel segue a mesma
            // decisao, para os dois nao divergirem numa pasta em que so um tem marca.
            var d3d9EraNosso = EhNossoD3d9(targetDir);
            var conf = Path.Combine(targetDir, ConfFile);
            var cpl = Path.Combine(targetDir, CplFile);

            Devolver(Path.Combine(targetDir, D3d9File), d3d9EraNosso, EhBinarioDaBiblioteca);
            // O conf e o painel sem o wrapper nao servem a nada e so confundem quem for olhar a
            // pasta depois — mas so os NOSSOS. Os que tem copia guardada eram do usuario.
            Devolver(conf, d3d9EraNosso || EhNossoConf(conf), ConfEhORenderizado);
            Devolver(cpl, d3d9EraNosso || EhNossoCpl(cpl), c => Iguais(c, LibraryCpl));
        }
        catch (Exception ex) { Log.Warn($"dgvoodoo remove {targetDir}: {ex.Message}"); }
    }

    /// <param name="conteudoEhNosso">Reconhece, pelo conteudo, o que este launcher escreve — e o
    /// que desmascara uma copia guardada que na verdade e nossa.</param>
    private static void Devolver(string caminho, bool ehNosso, Func<string, bool> conteudoEhNosso)
    {
        var backup = caminho + BackupSuffix;
        var marca = caminho + OursSuffix;
        // Uma copia guardada que e o NOSSO proprio arquivo nao e o estado de antes de nos: o
        // build anterior, ao reinstalar por cima de uma instalacao que ele nao reconhecia como
        // sua (conf editado ou ausente), guardava o wrapper que ele mesmo tinha posto como se
        // fosse do usuario. Devolve-la seria reinstalar o dgVoodoo no ato de desinstalar. Ela e
        // descartada, e o arquivo que ela "protegia" sai pela mesma prova, logo abaixo.
        if (File.Exists(backup) && conteudoEhNosso(backup))
        {
            Log.Info($"dgvoodoo remove: {Path.GetFileName(backup)} e o nosso proprio arquivo; descartado");
            File.Delete(backup);
        }
        if (File.Exists(backup)) { File.Copy(backup, caminho, overwrite: true); File.Delete(backup); }
        else if (File.Exists(caminho) && (ehNosso || File.Exists(marca) || conteudoEhNosso(caminho)))
            File.Delete(caminho);
        else if (File.Exists(caminho))
            Log.Warn($"dgvoodoo remove: {Path.GetFileName(caminho)} nao e nosso e nao tem copia; fica");
        if (File.Exists(marca)) File.Delete(marca);
    }

    /// <summary>
    /// A tabela de importacao deste binario esta comprimida?
    ///
    /// Um executavel de jogo real importa dezenas de DLLs — kernel32, user32, a runtime C, a API
    /// grafica, audio, rede. Um que importa uma ou duas e um stub de descompressao: o protetor
    /// remonta o resto so depois de o processo comecar. Nao ha o que ler ali, e tratar esse
    /// silencio como "nao usa API grafica" e ler a ausencia de dados como dado.
    /// </summary>
    private static bool EhEmpacotado(PeUtils.PeInfo pe) => pe.Imports.Count <= 2;

    /// <summary>
    /// A PASTA tem evidencia de Direct3D 9, mesmo que o executavel nao possa ser lido.
    ///
    /// Duas fontes, as duas fortes. A D3DX9 e a biblioteca auxiliar do D3D9 e de mais nada — quem
    /// a distribui renderiza em D3D9 (o Hitman: Blood Money traz `d3dx9_27.dll`). E um utilitario
    /// vizinho que ABRE um device D3D9 para enumerar adaptadores so existe num jogo D3D9: o
    /// `configure.exe` do mesmo Hitman importa `d3d9.dll` de forma limpa, sem empacotamento.
    /// </summary>
    private static bool PastaEhD3d9(string exePath)
    {
        var dir = Path.GetDirectoryName(exePath);
        if (dir is null) return false;
        try
        {
            if (Directory.EnumerateFiles(dir, "d3dx9_*.dll", SearchOption.TopDirectoryOnly).Any())
                return true;

            // Nenhum sinal de API moderna na pasta desfaz a conclusao: um jogo que traz d3d11 ou
            // d3d12 ao lado nao e caso de traducao, por mais empacotado que o exe esteja.
            foreach (var moderna in new[] { "d3d11.dll", "d3d12.dll", "d3d10.dll" })
            {
                var p = Path.Combine(dir, moderna);
                // So conta se for a DLL do SISTEMA redistribuida, e nao um wrapper que NOS pomos.
                if (File.Exists(p) && !File.Exists(p + ".renodx-ours")) return false;
            }

            foreach (var vizinho in Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly))
            {
                if (string.Equals(vizinho, exePath, StringComparison.OrdinalIgnoreCase)) continue;
                var pv = PeUtils.Inspect(vizinho);
                if (pv is null || EhEmpacotado(pv)) continue;
                if (pv.Imports.Any(i => i.Equals("d3d9.dll", StringComparison.OrdinalIgnoreCase)
                                        || i.StartsWith("d3dx9_", StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
        }
        catch (Exception ex) { Log.Warn($"pasta d3d9 {dir}: {ex.Message}"); }
        return false;
    }
}
