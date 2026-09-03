using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using RenoDXLauncher.Localization;

namespace RenoDXLauncher.Services;

/// <summary>
/// Multi Frame Generation acima do teto que a NVIDIA fixa em fabrica.
///
/// O DLSS 4 gera ate tres quadros por quadro renderizado (4x). Esse limite nao e do silicio: sao
/// duas comparacoes em codigo, e da para ler as duas.
///
///   `nvngx_dlssg.dll`  tem um `test dl,dl / je` que pergunta "este dispositivo pode MFG?" e, na
///                      resposta negativa, pula o trecho que ativa os modos acima de 2x. E ele o
///                      portao que deixa a serie RTX 40 inteira em 2x.
///   `sl.dlss_g.dll`    tem um `mov edx,5 / cmp ecx,edx / cmovb edx,ecx` que trava o numero de
///                      quadros gerados no menor entre o pedido e o teto.
///
/// Neutralizar as duas instrucoes na MEMORIA do processo (nunca no arquivo em disco) destrava
/// 2x ate 6x. Numa RTX 40 isso e a diferenca entre ter e nao ter o recurso; numa RTX 50, onde o
/// portao ja abre, e o teto que sobe de 4x para 6x.
///
/// Destravar sozinho nao basta na RTX 40. A Ada tem um defeito de compactacao no meio do
/// intervalo: com mais de um quadro gerado, as amostras colapsam para o centro em vez de ocupar
/// as posicoes temporais pedidas, e o resultado sao quadros quase duplicados — mais FPS no
/// contador e nenhuma fluidez a mais. A correcao (chamada D157 pelo autor) reescreve, em memoria,
/// o programa temporal do slot 9 pelo que a Blackwell usa. Ela exige confirmar que a placa ativa
/// e mesmo Ada, o que e feito pela capacidade de computo via CUDA — e, se a confirmacao falhar,
/// tudo volta ao 2x nativo em vez de entregar quadros errados.
///
/// O binario e nosso: compilado do fonte MIT de dashdogy/RTX40MFG-Unlock (ver `native/mfg/`),
/// com tres mudancas — identidade de add-on do ReShade, nome proprio para os arquivos de controle
/// e de status, e o config lido AO LADO do modulo. As tres existem para a mesma coisa: o mod
/// original e um plugin do Cyber Engine Tweaks, entregue so ao Cyberpunk 2077, e o patch em si
/// nunca foi especifico daquele jogo — ele mira o Streamline. Trocado o carregador pelo ReShade,
/// que este launcher ja instala em qualquer jogo, o mesmo patch atende qualquer jogo com
/// Streamline.
///
/// Vale dizer o que isto NAO e: nao ha aqui promessa de imagem melhor. Modos acima de 2x sao
/// experimentais, e 5x e 6x sao chamados de experimentais pelo proprio autor. O launcher expoe a
/// escolha, diz onde comeca o territorio instavel e sabe desfazer.
/// </summary>
public static class MfgService
{
    /// <summary>O add-on que carrega o patch. Add-on do ReShade, e nao `.asi`: o ReShade e o
    /// carregador que o launcher ja poe em todo jogo, e o `LoadFromDllMain` dele entra cedo o
    /// bastante para o gancho existir antes de o jogo criar a feature de Frame Generation.</summary>
    public const string AddonFile = "renodx-mfg.addon64";

    /// <summary>O que o add-on le. Relido em laco enquanto o jogo roda, entao trocar o
    /// multiplicador vale sem fechar o jogo.</summary>
    public const string ConfigFile = "renodx-mfg.json";

    /// <summary>O que o add-on escreve: o resultado real da sessao. E a unica prova de que o
    /// patch pegou — a interface le daqui em vez de afirmar sucesso por ter copiado arquivos.</summary>
    public const string StatusFile = "renodx-mfg-status.json";

    private const string OursSuffix = ".renodx-ours";

    /// <summary>O plugin de Frame Generation do Streamline: e nele que mora o teto de quadros.
    /// A presenca dele e o que diz que este jogo tem a rota que o patch entende.</summary>
    public const string WrapperFile = "sl.dlss_g.dll";

    /// <summary>O runtime NGX de Frame Generation: e nele que mora o portao de dispositivo.</summary>
    public const string NgxFile = "nvngx_dlssg.dll";

    public static string LibraryDir { get; } = Path.Combine(AppPaths.DataDir, "mfg");
    public static string LibraryAddon => Path.Combine(LibraryDir, AddonFile);

    /// <summary>
    /// Os bytes exatos do add-on que vai embutido no executavel do launcher.
    ///
    /// Conferidos na extracao pelo mesmo motivo do add-on neural: um recurso truncado nao produz
    /// erro nenhum dentro do jogo, so ausencia de efeito — o sintoma mais caro de diagnosticar
    /// deste projeto.
    /// </summary>
    private const string EmbeddedSha256 =
        "8368BFF0D73D962B202106BB7DCC60ED6619D828C15ED12D53AC5C1CF87D7311";
    private const long EmbeddedLength = 92672;

    public const int MinMultiplier = 2;
    public const int MaxMultiplier = 6;

    /// <summary>Acima de 4x quem chama de experimental e o autor do patch, nao nos. A interface
    /// marca a fronteira; ela nao e um bloqueio.</summary>
    public const int MaxSafeMultiplier = 4;

    /// <summary>
    /// Ada. E o piso REAL, e nao uma escolha nossa: Frame Generation do DLSS depende do
    /// acelerador de fluxo optico da geracao Ada, e placa anterior (RTX 30, RTX 20) nao tem a
    /// feature para destravar. Destravar o portao numa Ampere nao produziria 2x — produziria
    /// chamada a um caminho que nao existe.
    /// </summary>
    public const int MinSm = 89;

    /// <summary>
    /// As versoes de `nvngx_dlssg.dll` contra as quais o patch foi validado byte a byte
    /// (ver `native/mfg/source/dlssg_provider_policy.h`).
    ///
    /// O quarto componente nao entra: ele muda sem mudar o trecho de codigo que interessa. Fora
    /// desta lista o add-on recusa por conta propria — ele confere de novo dentro do processo, e
    /// esta lista aqui existe para a interface poder AVISAR antes, em vez de deixar a pessoa
    /// concluir que instalou errado.
    /// </summary>
    public static readonly IReadOnlyList<Version> SupportedProviders =
    [
        new(310, 7, 0), new(310, 7, 128), new(310, 7, 129), new(310, 8, 0),
    ];

    /// <param name="Multiplier">2 a 6. 2x e o que a RTX 40 ja fazia sozinha.</param>
    /// <param name="Dynamic">Deixa o proprio add-on escolher o multiplicador quadro a quadro.</param>
    /// <param name="DynamicTargetFps">Alvo do modo dinamico. 0 = sem alvo.</param>
    /// <param name="Experimental56">Autoriza o modo dinamico a passar de 4x.</param>
    public record Config(int Multiplier = 2, bool Dynamic = false, int DynamicTargetFps = 0,
                         bool Experimental56 = false)
    {
        public Config Sane() => this with
        {
            Multiplier = Math.Clamp(Multiplier, MinMultiplier, MaxMultiplier),
            DynamicTargetFps = Math.Clamp(DynamicTargetFps, 0, 1000),
        };
    }

    /// <summary>O que a ultima sessao do jogo relatou. Tudo opcional: o arquivo so existe depois
    /// de o jogo ter rodado ao menos uma vez com o add-on carregado.</summary>
    public record Status(bool BridgeReady, bool Applied, bool GameFrameGenerationOn,
                         int AppliedMultiplier, string AppliedMode,
                         int PatchedWrapper, int PatchedNgx,
                         double RealFps, double DlssFps);

    /// <param name="Sm">Arquitetura CUDA da placa (89 Ada, 120 Blackwell).</param>
    /// <param name="ProviderVersion">Versao do `nvngx_dlssg.dll` da pasta, quando ha um.</param>
    public record Detection(bool HasStreamlineFg, bool HasNgxInGame, Version? ProviderVersion,
                            bool ProviderSupported, bool Applied, Config Config, int? Sm,
                            string? Blocker, Status? LastRun)
    {
        /// <summary>
        /// A placa consegue Frame Generation? Ada ou mais nova.
        ///
        /// Este e o unico requisito que ESCONDE o cartao em vez de virar aviso dentro dele, e a
        /// diferenca e que nao ha nada a fazer com a resposta. Os outros bloqueios apontam um
        /// caminho — o runtime do jogo pode ser atualizado, o Streamline pode aparecer num patch
        /// do jogo. "Sua placa e de 2020" nao aponta nada: e so um cartao ocupando espaco em todo
        /// jogo da lista para repetir um fato que nao muda.
        /// </summary>
        public bool GpuCapable => Sm >= MinSm;

        /// <summary>O cartao aparece? So onde ha a rota que o patch entende E a placa alcanca —
        /// ou onde ele ja esta instalado, para que desligar continue possivel mesmo depois de
        /// trocar de placa ou de a deteccao mudar de ideia.</summary>
        public bool Offerable => (HasStreamlineFg && GpuCapable) || Applied;

        /// <summary>Esta placa ja fazia MFG sozinha, e o que o patch entrega e o teto maior.</summary>
        public bool JaTinhaMfg => Sm >= 120;
    }

    // ---------- biblioteca ----------

    /// <summary>
    /// Poe o add-on na biblioteca, tirando-o do proprio executavel.
    ///
    /// Embutido, e nao baixado: e um binario que compilamos, de fonte MIT, e nao existe release
    /// publica dele com este nome e esta forma. Vindo junto, instalar MFG nao depende de rede
    /// nem de o usuario achar arquivo nenhum.
    /// </summary>
    public static bool EnsureLibrary()
    {
        try
        {
            if (File.Exists(LibraryAddon) && new FileInfo(LibraryAddon).Length == EmbeddedLength)
                return true;

            var asm = typeof(MfgService).Assembly;
            var nome = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(AddonFile, StringComparison.OrdinalIgnoreCase));
            if (nome is null) { Log.Warn("mfg: recurso embutido nao encontrado"); return false; }

            using var s = asm.GetManifestResourceStream(nome)!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var bytes = ms.ToArray();
            if (bytes.LongLength != EmbeddedLength)
            {
                Log.Warn($"mfg: recurso com {bytes.LongLength} bytes, esperado {EmbeddedLength}");
                return false;
            }
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            if (!hash.Equals(EmbeddedSha256, StringComparison.OrdinalIgnoreCase))
            {
                Log.Warn($"mfg: hash do recurso nao confere ({hash})");
                return false;
            }
            Directory.CreateDirectory(LibraryDir);
            File.WriteAllBytes(LibraryAddon, bytes);
            Log.Info($"mfg: add-on posto na biblioteca ({bytes.Length} bytes)");
            return true;
        }
        catch (Exception ex) { Log.Warn($"mfg: extrair add-on: {ex.Message}"); return false; }
    }

    // ---------- leitura da pasta ----------

    private static readonly EnumerationOptions Varredura = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = true,
        MaxRecursionDepth = 5,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    /// <summary>Primeiro arquivo com este nome na arvore do jogo, ou null.</summary>
    private static string? Achar(string installDir, string nome)
    {
        try
        {
            var raiz = Path.Combine(installDir, nome);
            if (File.Exists(raiz)) return raiz;
            return Directory.EnumerateFiles(installDir, nome, Varredura).FirstOrDefault();
        }
        catch (Exception ex) { Log.Warn($"mfg: procurar {nome}: {ex.Message}"); return null; }
    }

    public static bool IsApplied(string targetDir) =>
        File.Exists(Path.Combine(targetDir, AddonFile));

    /// <summary>Foi o launcher que pos o add-on aqui?</summary>
    public static bool IsOurs(string targetDir) =>
        File.Exists(Path.Combine(targetDir, AddonFile + OursSuffix));

    /// <summary>
    /// A versao do provedor conta so os tres primeiros componentes — o quarto muda sem mexer no
    /// trecho patcheado, e cobra-lo recusaria builds identicos no que importa.
    /// </summary>
    public static bool ProviderIsSupported(Version? v) =>
        v is not null && SupportedProviders.Any(s =>
            s.Major == v.Major && s.Minor == v.Minor && s.Build == v.Build);

    public static Detection Detect(string installDir, string targetDir,
                                   NeuralUpliftService.HostCapability host)
    {
        var wrapper = Achar(installDir, WrapperFile);
        var ngx = Achar(installDir, NgxFile);
        var versao = ngx is not null ? DlssRuntimeService.ReadVersion(ngx) : null;
        var aplicado = IsApplied(targetDir);
        var sm = host.Sm;

        string? bloqueio =
            sm is null ? L.T("Mfg_Blocked_Gpu", host.GpuName ?? "?")
            : sm < MinSm ? L.T("Mfg_Blocked_Arch", host.GpuName ?? "?", CudaFatbin.Rotulo(sm.Value))
            : wrapper is null && !aplicado ? L.T("Mfg_Blocked_NoStreamline")
            : ngx is not null && !ProviderIsSupported(versao)
                ? L.T("Mfg_Blocked_Provider", versao?.ToString() ?? "?",
                      string.Join(", ", SupportedProviders.Select(v => $"{v.Major}.{v.Minor}.{v.Build}")))
            : null;

        return new Detection(wrapper is not null, ngx is not null, versao,
                             ProviderIsSupported(versao), aplicado,
                             ReadConfig(targetDir), sm, bloqueio, ReadStatus(targetDir));
    }

    // ---------- configuracao ----------

    private static string ConfigPath(string targetDir) => Path.Combine(targetDir, ConfigFile);

    public static Config ReadConfig(string targetDir)
    {
        try
        {
            var p = ConfigPath(targetDir);
            if (!File.Exists(p)) return new Config();
            using var doc = JsonDocument.Parse(File.ReadAllText(p));
            var r = doc.RootElement;
            int Num(string nome, int padrao) =>
                r.TryGetProperty(nome, out var e) && e.TryGetInt32(out var v) ? v : padrao;
            var modo = r.TryGetProperty("mode", out var m) ? m.GetString() : "fixed";
            var exp = r.TryGetProperty("dynamicExperimental56", out var x)
                      && x.ValueKind == JsonValueKind.True;
            return new Config(Num("multiplier", 2), modo == "dynamic",
                              Num("dynamicTargetFrameRate", 0), exp).Sane();
        }
        catch (Exception ex) { Log.Warn($"mfg: ler config: {ex.Message}"); return new Config(); }
    }

    /// <summary>
    /// Escreve o controle que o add-on le.
    ///
    /// A mao, e nao por serializador: quem le do outro lado e um varredor de substring em C, com
    /// forma fixa. Um serializador que decidisse emitir `2.0`, reordenar as chaves ou omitir um
    /// campo com valor padrao produziria um arquivo que o add-on recusa inteiro — e a recusa dele
    /// e silenciosa, some so o efeito.
    /// </summary>
    public static void WriteConfig(string targetDir, Config config)
    {
        var c = config.Sane();
        Directory.CreateDirectory(targetDir);
        var json = "{\n"
                 + $"  \"mode\": \"{(c.Dynamic ? "dynamic" : "fixed")}\",\n"
                 + $"  \"multiplier\": {c.Multiplier},\n"
                 + $"  \"dynamicTargetFrameRate\": {c.DynamicTargetFps},\n"
                 + $"  \"dynamicExperimental56\": {(c.Experimental56 ? "true" : "false")},\n"
                 + "  \"version\": 6\n"
                 + "}\n";
        File.WriteAllText(ConfigPath(targetDir), json);
    }

    /// <summary>O relato da ultima sessao, ou null quando o jogo ainda nao rodou com o add-on.</summary>
    public static Status? ReadStatus(string targetDir)
    {
        try
        {
            var p = Path.Combine(targetDir, StatusFile);
            if (!File.Exists(p)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(p));
            var r = doc.RootElement;
            bool Flag(string n) => r.TryGetProperty(n, out var e) && e.ValueKind == JsonValueKind.True;
            int Num(string n) => r.TryGetProperty(n, out var e) && e.TryGetInt32(out var v) ? v : 0;
            var modo = r.TryGetProperty("appliedMode", out var m) ? m.GetString() ?? "fixed" : "fixed";
            // Os dois contadores vem em milesimos de quadro por segundo.
            return new Status(Flag("bridgeReady"), Flag("applied"), Flag("gameFrameGenerationOn"),
                              Num("appliedMultiplier"), modo,
                              Num("patchedWrapperCandidates"), Num("patchedNgxCandidates"),
                              Num("realFpsMilli") / 1000.0, Num("dlssFpsMilli") / 1000.0);
        }
        catch (Exception ex) { Log.Warn($"mfg: ler status: {ex.Message}"); return null; }
    }

    // ---------- instalar e desinstalar ----------

    /// <summary>O ReShade precisa estar na pasta para carregar o add-on. Quem o instala e o
    /// chamador (o mesmo <c>ReShadeService</c> que o resto do launcher usa).</summary>
    public static bool NeedsReShade(string targetDir) =>
        AddonService.GetState(targetDir, null).ReShadeDllName is null;

    /// <summary>
    /// Poe o add-on, o controle e a carga antecipada na pasta do jogo.
    ///
    /// A carga antecipada nao e opcional aqui, e por um motivo diferente do add-on neural: o
    /// interposer do Streamline sobe junto com o processo, e um add-on carregado no momento
    /// normal do ReShade chegaria depois de a feature de Frame Generation ja existir — tarde
    /// demais para o gancho ver a criacao dela.
    /// </summary>
    public static void Apply(string targetDir, string iniPath, Config config,
                             IProgress<string>? progress = null)
    {
        if (!EnsureLibrary()) throw new InvalidOperationException(L.T("Mfg_Error_NoAddon"));

        progress?.Report(L.T("Mfg_Applying"));
        Directory.CreateDirectory(targetDir);
        var destino = Path.Combine(targetDir, AddonFile);
        File.Copy(LibraryAddon, destino, overwrite: true);
        File.WriteAllText(destino + OursSuffix, DateTime.UtcNow.ToString("o"));
        WriteConfig(targetDir, config);

        var ini = new IniFile(iniPath);
        NeuralUpliftService.AddToEarlyLoad(ini, AddonFile);
        ini.Save();
        Log.Info($"mfg: instalado em {targetDir} ({config.Multiplier}x, " +
                 $"{(config.Dynamic ? "dinamico" : "fixo")})");
    }

    /// <summary>
    /// Tira o que foi posto e nao toca no resto.
    ///
    /// O status e apagado junto de proposito: ele descreve uma sessao que rodou COM o patch, e
    /// deixa-lo para tras faria a interface relatar um resultado antigo ao lado de um cartao
    /// desligado.
    /// </summary>
    public static void Remove(string targetDir, string iniPath)
    {
        foreach (var nome in new[] { AddonFile, AddonFile + OursSuffix, ConfigFile, StatusFile })
        {
            var p = Path.Combine(targetDir, nome);
            try { if (File.Exists(p)) File.Delete(p); }
            catch (Exception ex) { Log.Warn($"mfg: apagar {nome}: {ex.Message}"); }
        }
        try
        {
            if (File.Exists(iniPath))
            {
                var ini = new IniFile(iniPath);
                NeuralUpliftService.RemoveFromEarlyLoad(ini, AddonFile);
                ini.Save();
            }
        }
        catch (Exception ex) { Log.Warn($"mfg: limpar carga antecipada: {ex.Message}"); }
        Log.Info($"mfg: removido de {targetDir}");
    }
}
