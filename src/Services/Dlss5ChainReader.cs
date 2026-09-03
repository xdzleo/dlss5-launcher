using System.IO;
using RenoDXLauncher.Localization;

namespace RenoDXLauncher.Services;

/// <summary>
/// A cadeia do DLSS 5 numa pasta, lida elo a elo.
///
/// Cada elo quebrado produz o MESMO sintoma de fora — o jogo abre e nada acontece — entao um
/// unico "ligado/desligado" nao ajuda ninguem a agir. Mostrar os elos separados e o que
/// transforma "nao funciona" em "falta o ReShade".
///
/// Isto morava na view model, e so a janela sabia responder se um jogo estava inteiro. A
/// leitura e pura — disco e nada mais — entao a linha de comando pode auditar a biblioteca
/// toda com a MESMA verdade que a tela mostra, em vez de uma segunda implementacao que
/// discorda dela em algum caso.
/// </summary>
public static class Dlss5ChainReader
{
    /// <param name="Ok">O elo esta no lugar.</param>
    public record ChainLink(string Label, bool Ok);

    /// <summary>Recolhe o estado de cada elo para os indicadores do cartao.</summary>
    /// <summary>
    /// O que a leitura da pasta descobriu, antes de virar tela.
    ///
    /// Existe para separar LER de MOSTRAR. Ler e disco: uma duzia de File.Exists, dois ini, a
    /// inspecao do PE do executavel, o registro do Windows e uma varredura de conflitos. Mostrar
    /// e mexer em ObservableCollection, que so a thread da interface pode fazer. Enquanto as duas
    /// coisas moravam no mesmo metodo, o disco inteiro acontecia na thread da interface -- e o
    /// clique no jogo travava a janela pelo tempo da leitura.
    /// </summary>
    public record LeituraDaCadeia(List<ChainLink> Elos, bool FeederAtivo, bool PonteAtiva,
                                   bool AvisoSemDlss,
                                   IReadOnlyList<ConflictScanner.Conflito> Conflitos);

    /// <summary>Le a pasta e devolve os fatos. Nada aqui toca na interface — pode (e deve) rodar
    /// fora da thread dela.</summary>
    public static LeituraDaCadeia LerCadeia(string targetDir, string iniPath,
                                             NeuralUpliftService.Detection det, string? exePath,
                                             bool neuralAplicado)
    {
        var elos = new List<ChainLink>();
        var addon = NeuralUpliftService.DeployedGenericAddon(targetDir);
        var early = false;
        if (File.Exists(iniPath) && addon is not null)
        {
            var list = new IniFile(iniPath).Get("ADDON", "LoadFromDllMain", ignoreCase: true) ?? "";
            early = list.Split(',').Any(e => e.Trim()
                .Equals(Path.GetFileName(addon), StringComparison.OrdinalIgnoreCase));
        }

        // No caminho de 32 bits quem carrega o addon e o ReShade do host64\, com o ini DELE — o da
        // raiz nunca lista carga antecipada, porque o processo do jogo nao carrega addon de 64
        // bits. Medir so a raiz deixava este elo vermelho num jogo perfeitamente instalado.
        var iniHost64 = Path.Combine(targetDir, FeederService.Host64Dir, "ReShade.ini");
        if (!early && File.Exists(iniHost64))
        {
            var list = new IniFile(iniHost64).Get("ADDON", "LoadFromDllMain", ignoreCase: true) ?? "";
            early = list.Split(',').Any(e => e.Trim()
                .Equals("renodx-dlss5.addon64", StringComparison.OrdinalIgnoreCase));
        }
        // Num jogo de 32 bits o pass neural nao roda no processo do jogo: roda no host64\, e e LA
        // que o addon e os runtimes moram. O proprio DeployBits32Async os tira da raiz de
        // proposito — sao 271 MB que um processo de 32 bits nao carrega.
        //
        // A cadeia media so a raiz, entao um jogo de 32 bits corretamente instalado exibia os elos
        // "addon" e "neural" em vermelho para sempre, Dlss5Ready nunca virava true, e o interruptor
        // continuava dizendo "instalar" depois de instalar. O Hitman: Absolution avaliou 7200
        // frames com DLSS 5 enquanto a interface o mostrava como nao instalado.
        var host64 = Path.Combine(targetDir, FeederService.Host64Dir);
        var noHost64 = Directory.Exists(host64);
        var addonNoHost64 = noHost64 && File.Exists(Path.Combine(host64, "renodx-dlss5.addon64"));
        var runtimeNoHost64 = noHost64
            && File.Exists(Path.Combine(host64, NeuralUpliftService.RuntimeFile));

        // Em jogo Vulkan — nativo ou D3D9 traduzido pelo DXVK — o ReShade entra como CAMADA, e
        // um proxy dxgi.dll nunca e carregado. Medir so o proxy deixava este elo vermelho para
        // sempre numa instalacao correta, e como Dlss5Ready exige a cadeia inteira, o interruptor
        // continuava dizendo "instalar" depois de instalar. Foi o que apareceu no ENSLAVED: a
        // instalacao ia toda para Binaries\Win32, completa, e a interface mostrava desligado.
        var bits64Jogo = exePath is null || PeUtils.Inspect(exePath, readImports: false)?.Is64Bit != false;
        var camadaVk = VulkanLayerService.IsRegistered(targetDir, bits64Jogo);
        elos.Add(new ChainLink("ReShade", det.ReShadeDllName is not null || camadaVk));
        elos.Add(new ChainLink(L.T("Dlss5_Link_Addon"), det.AddonSupportsNr || addonNoHost64));
        elos.Add(new ChainLink(L.T("Dlss5_Link_Neural"), det.RuntimeDeployed || runtimeNoHost64));
        // O Ray Reconstruction so e exigido onde o jogo resolve runtimes na propria pasta. Onde
        // quem resolve e o driver, nao implantamos nada (um runtime parcial na pasta do
        // executavel quebra a resolucao do NGX) — e cobrar o arquivo aqui deixaria a cadeia
        // incompleta para sempre, num jogo que esta certo.
        var rrEsperado = NeuralUpliftService.TemRuntimeLocal(targetDir);
        elos.Add(new ChainLink(L.T("Dlss5_Link_Rr"),
            !rrEsperado || File.Exists(Path.Combine(targetDir, NeuralUpliftService.RayReconstructionFile))));
        elos.Add(new ChainLink(L.T("Dlss5_Link_EarlyLoad"), early || addon is null));
        elos.Add(new ChainLink(L.T("Dlss5_Link_Switch"), neuralAplicado));

        // A ponte e o Feeder entram na cadeia quando sao NECESSARIOS, com o estado que de fato
        // tem — e nao, como antes, apenas quando ja estao na pasta.
        //
        // Aquela regra tinha um buraco no unico lugar que importa: a AUSENCIA da peca, que e
        // exatamente a falha, era o estado que a cadeia nao sabia representar. Faltando a ponte,
        // nenhum elo aparecia, todos os outros ficavam verdes, o interruptor dizia "ligado" e
        // nada rodava dentro do jogo. Foi assim que a ponte do Baldur's Gate ficou renomeada para
        // .teste sem que o launcher notasse.
        var feederAtivo = FeederService.IsDeployed(targetDir);
        var ponteAtiva = NeuralUpliftService.BridgeDeployed(targetDir);

        var alcancaD3d12 = Dlss5Installer.ReachesD3D12(exePath);
        // Ver a nota longa em CheckNeuralAsync: a anulacao por Feeder presente saiu porque o
        // marcador `.renodx-ours` ja impede a deteccao de contar os runtimes que nos copiamos.
        var temDlssNativo = det.HasDlss;

        // A MESMA funcao que o instalador usa para escolher o caminho. Esta tela tinha a propria
        // copia da regra, sem o termo de Vulkan, e cobrava a Ponte num jogo que o instalador
        // tinha mandado para o Feeder -- elo vermelho que nenhum clique resolvia.
        var rota = Dlss5Installer.Rotear(targetDir, exePath, temDlssNativo, alcancaD3d12);

        // E o que JA esta instalado tem precedencia sobre o que a regra escolheria hoje. Ponte e
        // Feeder sao exclusivos, e a resposta pode mudar entre uma instalacao e a seguinte
        // (deteccao corrigida, jogo atualizado). Um caminho instalado e completo nao e um elo
        // faltando: trocar de caminho e reinstalar, e e assim que deve ser pedido.
        var pedePonte = rota.Ponte && !feederAtivo;
        var pedeFeeder = rota.Feeder && !ponteAtiva;

        // O aviso "sem DLSS nativo" segue a AUSENCIA de DLSS, nao a presenca do Feeder.
        //
        // Ele estava preso a feederAtivo, e o texto fala de outra coisa: que os motion vectors
        // sao estimados por shader porque o jogo nao os fornece. Num jogo que TEM DLSS e ficou
        // com o Feeder instalado -- Baldur's Gate 3, por exemplo -- o aviso aparecia dizendo que
        // o jogo nao tem DLSS, contradizendo a propria tela logo acima.
        var avisoSemDlss = feederAtivo && !temDlssNativo;

        if (pedePonte || ponteAtiva)
            elos.Add(new ChainLink(L.T("Dlss5_Link_Bridge"), ponteAtiva));
        if (pedeFeeder || feederAtivo)
            elos.Add(new ChainLink(L.T("Dlss5_Link_Feeder"), feederAtivo));

        // O tradutor de D3D9, quando o jogo precisa de um.
        //
        // Faltava, e a falta era invisivel: o Hitman: Blood Money aparecia com a cadeia INTEIRA
        // verde e "instalado", sem `d3d9.dll` nenhum na pasta e com um proxy dxgi.dll que um jogo
        // D3D9 nunca carrega. Nada rodava, e a tela dizia que estava tudo certo.
        //
        // Sem tradutor nao ha o que enganchar: o ReShade em D3D9 puro para no Shader Model 3 e
        // nenhum provedor de motion vectors compila; e a API nao tem handle compartilhado nem
        // fence, que e por onde as texturas chegam ao device D3D12 do pass.
        if (exePath is not null && DgVoodooService.Applies(exePath)
            && PeUtils.Inspect(exePath, readImports: false)?.Is64Bit == false)
        {
            var temTradutor = DxvkService.IsDeployed(targetDir) || DgVoodooService.IsDeployed(targetDir);
            elos.Add(new ChainLink(L.T("Dlss5_Link_Tradutor"), temTradutor));
        }
        // Direct3D 10: so o DXVK traduz (a 1.10.3, com d3d10.dll proprio — ver
        // DxvkService.D3d10Files). Sem ele a cadeia inteira pode estar verde e o jogo fecha ao
        // criar o device — foi assim no Just Cause 2, quando a resposta do launcher a essa API
        // ainda era uma recusa.
        else if (DxvkService.AppliesD3d10(exePath))
            elos.Add(new ChainLink(L.T("Dlss5_Link_TradutorD3d10"), DxvkService.IsDeployedD3d10(targetDir)));

        // A leitura que explica o que a cadeia sozinha nao explica: o que MAIS esta na pasta.
        return new LeituraDaCadeia(elos, feederAtivo, ponteAtiva, avisoSemDlss,
                                   ConflictScanner.Scan(targetDir, exePath));
    }
}
