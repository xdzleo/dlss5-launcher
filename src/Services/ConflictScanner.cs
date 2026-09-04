using System.Diagnostics;
using System.IO;
using RenoDXLauncher.Localization;

namespace RenoDXLauncher.Services;

/// <summary>
/// O que MAIS esta na pasta do jogo, e o que isso faz com a nossa cadeia.
///
/// Uma pasta de jogo nao e nossa. Antes de o launcher chegar nela ja passaram OptiScaler,
/// fakenvapi, dlssg-to-fsr3, Special K, um ReShade instalado a mao, um dgVoodoo de um tutorial de
/// 2019 — e cada um deles ocupa exatamente os mesmos slots que a gente precisa: o nome do proxy,
/// o hook de NGX, o d3d9.dll. O `bin` do Baldur's Gate 3 tinha QUATRO desses empilhados, com o
/// OptiScaler sentado no dxgi.dll de 25 MB, e o resultado nao era um erro: era o caminho DX11
/// simplesmente nao carregando, sem nada na tela dizendo por que.
///
/// Instalar por cima disso nao conserta e as vezes piora. O que conserta e dizer o que esta ali,
/// o que colide com o que, e deixar o usuario decidir — porque parte dessas ferramentas ele pode
/// querer manter (o dlssg-to-fsr3 e frame generation, e nao concorre com o pass neural).
///
/// Nada aqui apaga arquivo. O maximo e renomear para <see cref="SufixoAfastado"/>, que o usuario
/// desfaz tirando o sufixo — a pasta pode conter meses de configuracao de outra pessoa.
/// </summary>
public static class ConflictScanner
{
    /// <summary>Sufixo dado ao que sai do caminho. Reversivel de proposito: e o mod de outra
    /// pessoa, e a nossa leitura de que ele atrapalha pode estar errada.</summary>
    public const string SufixoAfastado = ".desativado-pelo-dlss5";

    /// <summary>Quanto isso atrapalha.</summary>
    public enum Nivel
    {
        /// <summary>Convive. Aparece so para o usuario saber que esta ali.</summary>
        Info,
        /// <summary>Pode degradar o resultado ou confundir a deteccao.</summary>
        Aviso,
        /// <summary>Impede a cadeia de funcionar. E a resposta para "instalei e nao acontece nada".</summary>
        Bloqueio,
    }

    /// <param name="Caminho">Arquivo (ou pasta) encontrado.</param>
    /// <param name="Ferramenta">De quem ele e, em nome humano.</param>
    /// <param name="Porque">O que ele faz com a NOSSA cadeia — nao o que ele faz em geral.</param>
    /// <param name="PodeAfastar">Se renomear resolve. Falso quando a solucao e outra
    /// (reinstalar o jogo, escolher outro caminho no launcher).</param>
    /// <param name="Rotulo">
    /// O nome a mostrar, quando o caminho nao serve de nome.
    ///
    /// Nem todo achado e um arquivo: "a ponte e o Feeder estao os dois aqui" e sobre a PASTA, e
    /// o caminho dela vira o nome do jogo na coluna do arquivo — a lista dizia "Control", como
    /// se existisse um arquivo com esse nome atrapalhando. Aqui vai o que de fato esta em
    /// conflito.
    /// </param>
    /// <param name="CorrigeReinstalando">
    /// Este achado e um estado da NOSSA propria instalacao, e some refazendo-a.
    ///
    /// Renomear arquivo nao resolve: as pecas em conflito sao as duas nossas, e quem sabe qual
    /// delas o jogo precisa e o instalador — ele escolhe a rota e tira a outra do caminho.
    /// </param>
    public sealed record Conflito(string Caminho, string Ferramenta, string Porque,
                                  Nivel Grau, bool PodeAfastar, string? Rotulo = null,
                                  bool CorrigeReinstalando = false)
    {
        public string Arquivo => Rotulo ?? Path.GetFileName(Caminho);
    }

    /// <summary>Nomes que o Windows resolve na pasta do executavel antes de ir ao sistema — e por
    /// isso a moeda de troca de todo mod de injecao. Quem senta num deles decide quem carrega.</summary>
    private static readonly string[] SlotsDeProxy =
    {
        "dxgi.dll", "d3d12.dll", "d3d11.dll", "d3d10.dll", "d3d9.dll", "d3d8.dll",
        "opengl32.dll", "ddraw.dll", "dinput8.dll", "version.dll", "winmm.dll", "dbghelp.dll",
    };

    /// <summary>Varre a pasta e diz o que ali disputa espaco com a cadeia do DLSS 5.</summary>
    public static IReadOnlyList<Conflito> Scan(string targetDir, string? exePath)
    {
        var achados = new List<Conflito>();
        if (!Directory.Exists(targetDir)) return achados;

        try
        {
            VarrerProxies(targetDir, achados);
            VarrerFerramentas(targetDir, achados);
            VarrerNossasDuplicidades(targetDir, exePath, achados);
            VarrerRestos(targetDir, achados);
        }
        catch (Exception ex) { Log.Warn($"conflitos em {targetDir}: {ex.Message}"); }

        // Bloqueio primeiro: a lista costuma ser longa, e o que impede de funcionar tem de estar
        // na primeira linha, nao na oitava.
        return achados.OrderByDescending(c => c.Grau).ToList();
    }

    // ---------------------------------------------------------------- slots de proxy

    /// <summary>Ferramentas que o proprio launcher instala. Encontra-las numa vaga de proxy e o
    /// funcionamento normal, nao invasao — e a primeira versao desta varredura acusou o nosso
    /// ReShade em 38 das 42 pastas testadas antes de isto existir.</summary>
    private static readonly string[] DaNossaCadeia = { "ReShade", "DXVK", "dgVoodoo2" };

    private static void VarrerProxies(string dir, List<Conflito> achados)
    {
        foreach (var slot in SlotsDeProxy)
        {
            var caminho = Path.Combine(dir, slot);
            if (!File.Exists(caminho)) continue;
            if (EhNosso(caminho)) continue;

            var dono = Identificar(caminho);
            if (dono is null) continue;                         // nao reconhecido: nao acusar sem base
            if (DaNossaCadeia.Contains(dono)) continue;         // e nosso; ver acima
            // O OptiScaler pode ser NOSSO: o launcher o instala como version.dll quando o jogo
            // tem FSR/XeSS proprio e nao tem DLSS, e deixa a marca ao lado do ini. Acusar a
            // propria instalacao seria o mesmo erro que DaNossaCadeia corrige acima — e era o
            // que acontecia: o cartao do jogo mostrava "OptiScaler ocupa version.dll" como
            // conflito no instante seguinte ao da instalacao. As instalacoes do build anterior,
            // que nao escrevia marca, entram pela mesma porta: o proxy identico ao da biblioteca
            // e tao nosso quanto o marcado.
            if (dono == "OptiScaler" && slot.Equals("version.dll", StringComparison.OrdinalIgnoreCase)
                && OptiScalerService.IsOursOrLegacy(dir)) continue;

            // Info, e nao bloqueio. Ocupar a vaga NAO impede o ReShade de carregar: esses
            // injetores encadeiam, e o proprio launcher conta com isso — quando o OptiScaler ja
            // esta no dxgi.dll, o ReShade fica como ReShade64.dll e e carregado por ele
            // (ReShadeService.KnownProxyNames documenta esse caso). Dizer "bloqueio" aqui seria
            // mandar o usuario afastar um mod que ele quer, para resolver um problema que ele
            // talvez nao tenha.
            achados.Add(new Conflito(caminho, dono,
                L.T("Conflito_Porque_Proxy", slot, dono), Nivel.Info, PodeAfastar: true));
        }
    }

    // ---------------------------------------------------------------- ferramentas por assinatura

    private static void VarrerFerramentas(string dir, List<Conflito> achados)
    {
        // Cada entrada: um arquivo que so existe se a ferramenta estiver instalada, o nome dela, o
        // que ela faz COM A GENTE, e o quanto atrapalha.
        //
        // Os graus sao conservadores de proposito. So e Aviso o que tem um mecanismo concreto de
        // atrapalhar (falsificar a NVAPI que o addon consulta; sobrescrever o preset que o
        // launcher acabou de gravar). O resto e Info: esta ali, o usuario deve saber, e nao ha
        // base para mandar tirar. Um scanner que grita em toda pasta e um scanner que o usuario
        // aprende a ignorar.
        (string Padrao, string Nome, string Chave, Nivel Grau)[] assinaturas =
        {
            ("OptiScaler.asi",       "OptiScaler",      "Conflito_Porque_OptiScaler",  Nivel.Aviso),
            ("fakenvapi.dll",        "fakenvapi",       "Conflito_Porque_FakeNvapi",   Nivel.Aviso),
            ("dlssg_to_fsr3*.dll",   "dlssg-to-fsr3",   "Conflito_Porque_DlssgFsr3",   Nivel.Info),
            ("SpecialK*.dll",        "Special K",       "Conflito_Porque_SpecialK",    Nivel.Info),
            ("dlsstweaks.ini",       "DLSSTweaks",      "Conflito_Porque_DlssTweaks",  Nivel.Aviso),
            ("DisplayCommander.ini", "DisplayCommander", "Conflito_Porque_Generico",   Nivel.Info),
        };

        foreach (var (padrao, nome, chave, grau) in assinaturas)
        {
            foreach (var f in Directory.EnumerateFiles(dir, padrao, SearchOption.TopDirectoryOnly))
            {
                if (EhNosso(f)) continue;
                // Um OptiScaler.asi nunca e nosso — o launcher instala o OptiScaler como
                // version.dll, e a isencao para ele fica em VarrerProxies. Um .asi ao lado da
                // nossa instalacao e um SEGUNDO OptiScaler, e esse merece ser acusado.

                achados.Add(new Conflito(f, nome, L.T(chave, nome), grau,
                                         // Um .ini sozinho nao carrega nada: afastar so o ini
                                         // deixa a DLL no lugar e nao resolve, so confunde.
                                         PodeAfastar: f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                                                      || f.EndsWith(".asi", StringComparison.OrdinalIgnoreCase)));
            }
        }
    }

    // ---------------------------------------------------------------- duas metades nossas juntas

    private static void VarrerNossasDuplicidades(string dir, string? exePath, List<Conflito> achados)
    {
        // Ponte + Feeder na mesma pasta: sobra, e nao bloqueio.
        //
        // Era Nivel.Bloqueio, com o texto dizendo que os dois se anulam. As duas coisas medidas
        // no Saints Row The Third desmentem isso: o jogo roda com os dois na pasta (feature 18
        // criada, 60 avaliacoes, nenhum device removido) e roda tambem sem a ponte. Chamar de
        // bloqueio o que nao bloqueia manda a pessoa mexer numa instalacao que esta funcionando.
        //
        // Continua valendo dizer que ha algo sobrando: na rota do Feeder o device D3D12 e criado
        // pelo proprio Feeder, entao a ponte ali nao tem o que fazer. Ela some sozinha na proxima
        // instalacao, e ate la nao atrapalha ninguem — dai o aviso ser informativo.
        if (!Dlss5Installer.MultiApiInstalado(dir)
            && FeederService.IsDeployed(dir) && NeuralUpliftService.BridgeDeployed(dir))
            achados.Add(new Conflito(dir, "DLSS 5 Launcher", L.T("Conflito_Porque_PonteFeeder"),
                                     Nivel.Info, PodeAfastar: false,
                                     Rotulo: $"{NeuralUpliftService.BridgeFile} + {FeederService.AddonFile}",
                                     CorrigeReinstalando: true));

        // dgVoodoo e DXVK disputam o mesmo d3d9.dll — quem ficar por ultimo ganha, e o outro vira
        // um arquivo que nunca carrega. Na rota D3D10 do DXVK nao ha disputa de nome, mas o
        // dgVoodoo ali e a mesma coisa: um D3D9.dll que o jogo carrega como fallback e nunca usa,
        // resto da tentativa que fechava o Just Cause 2.
        if ((DxvkService.IsDeployed(dir) || DxvkService.IsDeployedD3d10(dir)) && DgVoodooService.IsDeployed(dir))
            achados.Add(new Conflito(dir, "DLSS 5 Launcher", L.T("Conflito_Porque_DxvkDgVoodoo"),
                                     Nivel.Bloqueio, PodeAfastar: false,
                                     Rotulo: "DXVK + dgVoodoo2", CorrigeReinstalando: true));

        // ReShade entrando duas vezes no mesmo processo — uma pela camada Vulkan, outra pelo proxy.
        // Carga dupla e a causa conhecida do 0xc0000005 logo no start.
        //
        // A condicao exige que o JOGO seja Vulkan, e nao apenas que a camada esteja registrada.
        // A camada e global: ela mora num caminho unico em AppData e vale para todo aplicativo
        // Vulkan da maquina, entao `IsRegistered` responde "sim" para qualquer pasta assim que
        // ela e instalada uma vez. Testar so por ela acusava carga dupla em 34 das 42 pastas —
        // inclusive em jogos D3D12, onde a camada nunca chega a ser ativada.
        //
        // Num jogo D3D o proxy carrega e a camada nao; num jogo Vulkan a camada carrega, e o
        // proxy so entra junto se o executavel abrir aquela DLL. Os dois ao mesmo tempo, no mesmo
        // processo, e o caso que quebra.
        if (VulkanLayerService.Applies(exePath))
        {
            foreach (var s in SlotsDeProxy)
            {
                var p = Path.Combine(dir, s);
                if (!File.Exists(p) || !EhReShade(p)) continue;

                var como = ExeCarrega(exePath, s);
                if (como == Carga.Nao) continue;

                // O grau segue a forca da evidencia, e nao o tamanho do estrago possivel.
                //
                // Import na tabela e certeza: o Windows abre aquela DLL antes da primeira
                // instrucao do jogo. Uma mencao solta no binario e so indicio — metade dos
                // motores cita "dxgi" sem nunca chamar LoadLibrary nele. Chamar os dois de
                // bloqueio faria o usuario afastar um proxy que funciona; chamar os dois de
                // aviso esconderia um crash garantido. Entao os dois aparecem, cada um com o
                // seu peso.
                achados.Add(new Conflito(p, "ReShade", L.T("Conflito_Porque_ReShadeDuplo"),
                                         como == Carga.Import ? Nivel.Bloqueio : Nivel.Aviso,
                                         PodeAfastar: true));
            }
        }
    }

    // ---------------------------------------------------------------- restos de tentativas

    private static void VarrerRestos(string dir, List<Conflito> achados)
    {
        // Sufixos que so existem porque alguem — nos, um tutorial, o proprio usuario — renomeou um
        // arquivo para "desligar" em vez de tirar. Sozinhos nao carregam nada e sao inofensivos; o
        // problema e que confundem a leitura da pasta, inclusive a nossa.
        string[] sufixos = { "*.duplicado", "*.antigo", "*.era-x64", "*.bak", "*.old", "*.disabled" };
        var n = sufixos.Sum(s =>
        {
            try { return Directory.EnumerateFiles(dir, s, SearchOption.TopDirectoryOnly).Count(); }
            catch { return 0; }
        });
        if (n >= 3)
            achados.Add(new Conflito(dir, "—", L.T("Conflito_Porque_Restos", n), Nivel.Info,
                                     PodeAfastar: false));
    }

    // ---------------------------------------------------------------- identificacao

    /// <summary>De quem e esta DLL, quando da para saber com alguma confianca. `null` = nao sei,
    /// e nesse caso ficamos calados: acusar o d3d9.dll legitimo de um jogo seria pior.</summary>
    private static string? Identificar(string caminho)
    {
        var info = LerVersao(caminho);
        var texto = $"{info.ProdutoOuVazio} {info.DescricaoOuVazio} {info.EmpresaOuVazio}";

        if (texto.Contains("OptiScaler", StringComparison.OrdinalIgnoreCase)) return "OptiScaler";
        if (texto.Contains("Special K", StringComparison.OrdinalIgnoreCase)) return "Special K";
        if (texto.Contains("ReShade", StringComparison.OrdinalIgnoreCase)) return "ReShade";
        if (texto.Contains("DXVK", StringComparison.OrdinalIgnoreCase)) return "DXVK";
        if (texto.Contains("dgVoodoo", StringComparison.OrdinalIgnoreCase)) return "dgVoodoo2";
        if (texto.Contains("Ultimate ASI", StringComparison.OrdinalIgnoreCase)) return "Ultimate ASI Loader";

        // Sem informacao de versao e ao lado do proprio ini: a assinatura mais forte do OptiScaler,
        // que e como ele estava no Baldur's Gate 3.
        var dir = Path.GetDirectoryName(caminho);
        if (dir is not null && File.Exists(Path.Combine(dir, "OptiScaler.ini"))
            && Tamanho(caminho) > 8L * 1024 * 1024)
            return "OptiScaler";

        return null;
    }

    /// <summary>Com que forca de evidencia o executavel abre uma DLL.</summary>
    private enum Carga
    {
        /// <summary>Nem import nem mencao. O arquivo ao lado e inerte para este exe.</summary>
        Nao,
        /// <summary>O nome aparece no binario, mas fora da tabela de importacao. Indicio.</summary>
        Mencao,
        /// <summary>Esta na tabela de importacao: o Windows abre antes do jogo comecar.</summary>
        Import,
    }

    /// <summary>
    /// Este executavel chega a abrir esta DLL?
    ///
    /// Um proxy na pasta so entra no processo se o binario o pedir. O `bg3.exe` do Baldur's Gate
    /// nao importa DLL grafica nenhuma, entao um dxgi.dll ao lado dele e inerte — enquanto o
    /// `bg3_dx11.exe`, na mesma pasta, importa d3d11.dll e carregaria o que estiver com esse nome.
    /// E a diferenca entre um conflito real e um arquivo que so esta ali.
    /// </summary>
    private static Carga ExeCarrega(string? exePath, string dll)
    {
        if (exePath is null) return Carga.Nao;
        var pe = PeUtils.Inspect(exePath);
        if (pe is null) return Carga.Nao;
        if (pe.Imports.Any(i => i.Equals(dll, StringComparison.OrdinalIgnoreCase))) return Carga.Import;
        // Carga tardia por LoadLibrary nao aparece na tabela de importacao, mas o nome do modulo
        // precisa existir em algum lugar do binario para ser passado adiante. E so indicio: citar
        // o nome nao prova que a chamada acontece.
        try
        {
            var alvo = Path.GetFileNameWithoutExtension(dll);
            return VulkanLayerService.ContemTexto(exePath, alvo) ? Carga.Mencao : Carga.Nao;
        }
        catch { return Carga.Nao; }
    }

    private static bool EhReShade(string caminho) =>
        LerVersao(caminho).ProdutoOuVazio.Contains("ReShade", StringComparison.OrdinalIgnoreCase);

    /// <summary>O launcher marca o que ele mesmo copiou. Sem isso a varredura acusaria os nossos
    /// proprios arquivos de serem invasores.</summary>
    private static bool EhNosso(string caminho) =>
        File.Exists(caminho + ".renodx-ours") || File.Exists(caminho + ".renodx-bak");

    private readonly record struct Versao(string? Produto, string? Descricao, string? Empresa)
    {
        public string ProdutoOuVazio => Produto ?? "";
        public string DescricaoOuVazio => Descricao ?? "";
        public string EmpresaOuVazio => Empresa ?? "";
    }

    private static Versao LerVersao(string caminho)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(caminho);
            return new Versao(v.ProductName, v.FileDescription, v.CompanyName);
        }
        catch { return default; }
    }

    private static long Tamanho(string caminho)
    {
        try { return new FileInfo(caminho).Length; } catch { return 0; }
    }

    // ---------------------------------------------------------------- afastar

    /// <summary>
    /// Tira do caminho o que o usuario mandou tirar, renomeando. Devolve quantos sairam.
    ///
    /// Renomear e nao apagar: a leitura de que um mod atrapalha e nossa, e pode estar errada. Se
    /// estiver, tirar o sufixo devolve a pasta ao que era.
    /// </summary>
    public static int Afastar(IEnumerable<Conflito> conflitos, IProgress<string>? progress = null)
    {
        var n = 0;
        foreach (var c in conflitos)
        {
            if (!c.PodeAfastar || !File.Exists(c.Caminho)) continue;
            try
            {
                var destino = c.Caminho + SufixoAfastado;
                // Ja afastado antes e reinstalado depois: o segundo rename bateria no primeiro. O
                // primeiro NAO e apagado — pode ser outro arquivo (outro mod no mesmo slot), e
                // apagar quebraria a promessa desta classe de que tudo aqui se desfaz. O novo
                // ganha um numero; o mais antigo fica com o nome simples.
                for (var i = 2; File.Exists(destino); i++) destino = c.Caminho + SufixoAfastado + "." + i;
                File.Move(c.Caminho, destino);
                progress?.Report(L.T("Conflito_Afastado", c.Arquivo, c.Ferramenta));
                n++;
            }
            catch (Exception ex) { Log.Warn($"afastar {c.Arquivo}: {ex.Message}"); }
        }
        return n;
    }
}
