using System.Net.Http;
using System.IO;
using Microsoft.Win32;
using RenoDXLauncher.Localization;
using RenoDXLauncher.Models;

namespace RenoDXLauncher.Services;

/// <summary>
/// Neural Uplift (DLSS-NR): NVIDIA's neural post-process, driven by a RenoDX addon.
///
/// What it is: the DLSS 5 runtime ships a network NVIDIA calls CG2R ("CG to Real"). Unlike
/// Super Resolution it does not scale anything — it rewrites the finished frame to look less
/// synthetic, with a dedicated knob for skin (DLSSNR.SkinStructureStrength) because character
/// faces are what the model was built for. It needs exactly four buffers — colour, depth,
/// motion vectors, output — which is why an addon that already feeds Ray Reconstruction can
/// drive it with almost no extra work.
///
/// Why the launcher has to help: NVIDIA has not shipped nvngx_dlssnr.dll in any driver or
/// public SDK, so the addon LoadLibrary()s it from the GAME folder and silently does nothing
/// when it is absent. That one missing file is the whole difference between "the toggle works"
/// and "the toggle is there and nothing happens", and it is not something the addon can fix.
/// So: the user drops the runtime into the launcher's library once, and every eligible game
/// gets a copy on demand.
///
/// Why detection reads the addon binary: NR support is not a property of the launcher's mod
/// catalog — it is a property of the build the user happens to have. The addons that carry it
/// today are hand-built and passed around Discord, and more will land as maintainers pick the
/// feature up. Scanning the installed .addon64 for the NGX parameter names means a build that
/// gained NR support this morning is offered this afternoon, with no launcher update and no
/// catalog entry. <see cref="DlssFixService"/> can gate on <see cref="ModKind"/> because the
/// SDR-to-HDR upgrade is a property of the mod; this cannot.
/// </summary>
public static class NeuralUpliftService
{
    /// <summary>The DLSS 5 runtime. Loaded by the addon from the game folder, by name.</summary>
    public const string RuntimeFile = "nvngx_dlssnr.dll";

    /// <summary>Driver branch that first carried the DLSSNR plumbing. Below it the NGX feature
    /// creation fails and the addon falls back to doing nothing.</summary>
    public const int MinDriverBranch = 616;

    /// <summary>The addon's master switch, in the preset section every RenoDX mod loads at boot.</summary>
    private const string EnableKey = "NeuralUplift";

    /// <summary>An NGX parameter name the addon must contain to be able to drive DLSSNR. Present
    /// in any build that creates the feature; absent from every ordinary HDR addon.</summary>
    private static readonly byte[] Marker = "DLSSNR.Output"u8.ToArray();

    /// <summary>Where the user parks the runtime once so every game can be served from it.</summary>
    public static string LibraryDir { get; } = Path.Combine(AppPaths.DataDir, "neural");
    public static string LibraryRuntime { get; } = Path.Combine(LibraryDir, RuntimeFile);

    // ---------- generic addon ----------

    /// <summary>
    /// A game-agnostic addon that drives DLSSNR on its own. It hooks the NGX exports the game
    /// already calls, so it needs nothing from the game's own RenoDX mod — which is what makes
    /// "turn neural rendering on in ANY game with DLSS" possible instead of only in the handful
    /// of titles whose hand-built mod happens to carry NR.
    ///
    /// It runs the neural pass INLINE, on the game's own command list, right after the game's
    /// DLSS output exists and before the game's post-processing and UI. Composing at present
    /// time instead would overwrite the finished frame and destroy the HUD.
    ///
    /// The BUILD deployed under this name is the community one (see <see cref="FetchAddonAsync"/>),
    /// not an in-house one. The name is the launcher's own on purpose: it is the file this app
    /// wrote and may therefore remove again, which is not true of a copy the user placed by hand
    /// under the upstream name.
    /// </summary>
    public const string GenericAddonFile = "renodx-neural.addon64";
    public static string LibraryAddon { get; } = Path.Combine(LibraryDir, GenericAddonFile);

    /// <summary>
    /// Every file name a game-agnostic NR addon goes by. The build passed around the community
    /// is called <c>renodx-dlss5.addon64</c>; ours is <c>renodx-neural.addon64</c>. Checking only
    /// our name made the launcher blind to a game that already had the other one deployed: it
    /// read the enable flag from the wrong section, reported the feature off while it was
    /// demonstrably on, and offered to install a second addon beside the working one.
    /// </summary>
    private static readonly string[] GenericAddonNames = [GenericAddonFile, "renodx-dlss5.addon64"];

    /// <summary>The generic NR addon deployed in this game folder, whichever build it is.</summary>
    public static string? DeployedGenericAddon(string targetDir) =>
        GenericAddonNames.Select(n => Path.Combine(targetDir, n)).FirstOrDefault(File.Exists);

    /// <summary>
    /// Is DLSS 5 applied anywhere inside this install?
    ///
    /// Answers without needing the game's target directory, which is derived from the chosen
    /// executable and is therefore not known until the background pass that resolves it has run.
    /// The startup sweep runs in parallel with that pass, so asking it for a target directory got
    /// null for most games — and every one of them was then reported as "swapped, restore me?",
    /// including the ones whose swap was the DLSS 5 install itself.
    ///
    /// Finding the addon is the reliable way in: it lives in the same folder as the ini and the
    /// runtimes, by construction, because that is where the addon loads them from.
    /// </summary>
    public static bool IsAppliedAnywhere(string installDir)
    {
        try
        {
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                MaxRecursionDepth = 10,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };
            foreach (var name in GenericAddonNames)
            {
                foreach (var found in Directory.EnumerateFiles(installDir, name, options))
                {
                    var dir = Path.GetDirectoryName(found);
                    if (dir is null) continue;
                    if (IsApplied(dir, Path.Combine(dir, "ReShade.ini"))) return true;
                }
            }
        }
        catch (Exception ex) { Log.Warn($"neural applied scan {installDir}: {ex.Message}"); }
        return false;
    }

    /// <summary>
    /// Bring a deployed addon up to the library's build, keeping the file name it already has.
    ///
    /// Without this the launcher could install the addon and never update it: a folder that
    /// already had one counted as "can drive NR", so applying skipped the copy entirely and the
    /// game stayed on whatever build landed there first. The community build ships new versions
    /// often — 3.3.4 fixed a fully black frame in HDR — and being stuck one version back with no
    /// way to move is worse than not having the feature.
    ///
    /// The old build is kept beside it: this replaces a working file, and a version that turns
    /// out worse has to be a rename away from coming back.
    /// </summary>
    private static void RefreshDeployedAddon(string deployedPath, IProgress<string>? progress)
    {
        try
        {
            if (!File.Exists(LibraryAddon)) return;
            var deployed = new FileInfo(deployedPath);
            var library = new FileInfo(LibraryAddon);
            if (deployed.Length == library.Length
                && File.ReadAllBytes(deployedPath).AsSpan().SequenceEqual(File.ReadAllBytes(LibraryAddon)))
                return;

            var backup = deployedPath + BackupSuffix;
            if (!File.Exists(backup)) File.Copy(deployedPath, backup);
            File.Copy(LibraryAddon, deployedPath, overwrite: true);
            progress?.Report(L.T("Neural_DeployingAddon"));
            Log.Info($"neural addon refreshed: {deployedPath} ({deployed.Length} -> {library.Length} bytes)");
        }
        catch (Exception ex) { Log.Warn($"neural addon refresh {deployedPath}: {ex.Message}"); }
    }

    /// <summary>Suffix for the build being replaced, so a worse version is a rename from coming back.</summary>
    private const string BackupSuffix = ".renodx-bak";

    /// <summary>
    /// Marker saying the neural runtime in this folder was put here by the launcher.
    ///
    /// Its absence is what protects a copy the user already had. A file name cannot answer
    /// "did we write this?", and for this particular file guessing wrong destroys 158 MB that
    /// NVIDIA distributes nowhere.
    /// </summary>
    private static string RuntimeMark(string targetDir) =>
        Path.Combine(targetDir, RuntimeFile + ".renodx-ours");

    private static void MarkRuntimeOurs(string targetDir)
    {
        try { File.WriteAllText(RuntimeMark(targetDir), DateTime.UtcNow.ToString("o")); }
        catch (Exception ex) { Log.Warn($"neural mark: {ex.Message}"); }
    }

    private static bool RuntimeIsOurs(string targetDir) => File.Exists(RuntimeMark(targetDir));

    private static void ClearRuntimeMark(string targetDir)
    {
        try { if (File.Exists(RuntimeMark(targetDir))) File.Delete(RuntimeMark(targetDir)); }
        catch (Exception ex) { Log.Warn($"neural mark clear: {ex.Message}"); }
    }

    /// <summary>Ray Reconstruction's runtime. Separate from Super Resolution and from the neural
    /// one, and the addon reaches it the same way: by name, from beside the executable.</summary>
    public const string RayReconstructionFile = "nvngx_dlssd.dll";

    /// <summary>
    /// Put Ray Reconstruction's runtime beside the addon.
    ///
    /// The addon's denoiser control offers Ray Reconstruction, and RR is a different runtime from
    /// Super Resolution — <c>nvngx_dlssd.dll</c>, which the community guide lists as required
    /// alongside the neural one. Deploying only the neural runtime left that option present and
    /// dead: measured on this machine, eight of eleven games with the addon had no
    /// <c>nvngx_dlssd.dll</c> next to it.
    ///
    /// A game that has the file somewhere else in its install is not covered by that: the addon
    /// loads by name from the executable's folder, the same place it looks for the neural
    /// runtime. Inert where the game never asks for RR — NGX loads a runtime only when the
    /// feature is created.
    /// </summary>
    private static void DeployRayReconstruction(string targetDir, IProgress<string>? progress)
    {
        try
        {
            var source = Path.Combine(DlssRuntimeService.LibraryDir, RayReconstructionFile);
            if (!File.Exists(source)) return;

            // Um jogo sem runtime nenhum na pasta resolve NGX pelo driver, e a pasta do
            // executavel tem prioridade sobre ele. Plantar so o RR ali deixa um conjunto PARCIAL
            // — RR local, Super Resolution do driver, versoes que nao combinam — que e o mesmo
            // "conjunto incoerente" que CheckHealth trata como erro grave. O comentario acima
            // dizia que o arquivo seria inerte; ele nao e, porque muda de onde o NGX resolve.
            //
            // Custou o DLSS do Dragon Ball Sparking! ZERO, um jogo que nunca teve runtime local.
            if (!TemRuntimeLocal(targetDir))
            {
                Log.Info($"neural: {targetDir} nao tem runtime local; RR nao implantado (o driver resolve)");
                return;
            }

            var dest = Path.Combine(targetDir, RayReconstructionFile);
            if (File.Exists(dest) && new FileInfo(dest).Length == new FileInfo(source).Length) return;
            // Same bar as every other runtime this launcher writes: NVIDIA's signature decides.
            if (!DlssRuntimeService.IsGenuine(source, out var why))
            {
                Log.Warn($"neural RR runtime rejected: {why}");
                return;
            }
            // `overwrite: false` lanca quando ja existe backup — e o catch la embaixo engolia,
            // levando junto a copia da linha seguinte. Resultado: depois da primeira troca, o
            // runtime de RR nunca mais era atualizado, e o unico sinal era um Log.Warn.
            var novo = !File.Exists(dest);
            var backup = dest + BackupSuffix;
            if (!novo && !File.Exists(backup)) File.Copy(dest, backup);
            File.Copy(source, dest, overwrite: true);
            // Marca so quando o arquivo nao existia: desligar o recurso devolve a pasta ao que
            // era, sem apagar um RR que o jogo ja trazia.
            if (novo) MarkOurs(targetDir, RayReconstructionFile);
            progress?.Report(L.T("Neural_DeployingRR"));
            Log.Info($"neural: {RayReconstructionFile} deployed to {targetDir}");
        }
        catch (Exception ex) { Log.Warn($"neural RR deploy {targetDir}: {ex.Message}"); }
    }

    /// <summary>
    /// Tira um Ray Reconstruction que versoes antigas deste launcher deixaram numa pasta que
    /// nunca teve runtime proprio.
    ///
    /// Ate pouco tempo o RR era copiado para todo jogo, sem marcador e sem condicao. O estrago
    /// nao e so ocupar 79 MB: a deteccao varre a pasta procurando runtime de DLSS, encontra esse
    /// arquivo e conclui que o JOGO tem DLSS. A partir dai o instalador escolhe a ponte em vez do
    /// Feeder e manda o usuario ligar, nas opcoes, um DLSS que o jogo nao tem.
    ///
    /// So sai com as tres condicoes juntas: e byte a byte o mesmo da nossa biblioteca, nao ha
    /// marcador dizendo que e nosso (senao o caminho normal ja daria conta), e nao existe nenhum
    /// outro runtime que seja do jogo. Um jogo com DLSS de verdade nunca satisfaz a terceira.
    /// </summary>
    public static void CleanOrphanRayReconstruction(string targetDir, IProgress<string>? progress = null)
    {
        try
        {
            var alvo = Path.Combine(targetDir, RayReconstructionFile);
            if (!File.Exists(alvo) || File.Exists(alvo + OursSuffix)) return;
            if (OutroRuntimeDoJogo(targetDir)) return;

            var biblioteca = Path.Combine(DlssRuntimeService.LibraryDir, RayReconstructionFile);
            if (!File.Exists(biblioteca)) return;
            if (new FileInfo(alvo).Length != new FileInfo(biblioteca).Length) return;
            if (!MesmoConteudo(alvo, biblioteca)) return;

            var backup = alvo + BackupSuffix;
            if (!File.Exists(backup)) File.Copy(alvo, backup);
            File.Delete(alvo);
            progress?.Report(L.T("Neural_OrphanRrRemoved"));
            Log.Info($"neural: {RayReconstructionFile} orfao removido de {targetDir}");
        }
        catch (Exception ex) { Log.Warn($"neural orphan RR {targetDir}: {ex.Message}"); }
    }

    /// <summary>Existe algum runtime de DLSS na pasta que NAO tenha sido escrito por nos?</summary>
    private static bool OutroRuntimeDoJogo(string targetDir)
    {
        foreach (var f in Directory.GetFiles(targetDir, "*.dll"))
        {
            var nome = Path.GetFileName(f);
            if (nome.Equals(RayReconstructionFile, StringComparison.OrdinalIgnoreCase)) continue;
            var dlss = nome.StartsWith("nvngx_", StringComparison.OrdinalIgnoreCase)
                       || nome.StartsWith("sl.", StringComparison.OrdinalIgnoreCase);
            if (dlss && !File.Exists(f + OursSuffix)) return true;
        }
        return false;
    }

    private static bool MesmoConteudo(string a, string b)
    {
        using var fa = File.OpenRead(a);
        using var fb = File.OpenRead(b);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fa))
            == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fb));
    }

    /// <summary>
    /// Troca um d3dcompiler_47.dll velho da pasta do jogo pelo do Windows.
    ///
    /// Alguns jogos trazem a propria copia do compilador de shaders, e ela pode ser MUITO antiga
    /// — o NARUTO X BORUTO traz a v6.3.9600, de 2013. A pasta do executavel tem prioridade na
    /// busca de DLL, entao o addon neural carrega essa e nao a do sistema. Ele compila o shader
    /// de encode com alvo `cs_5_1`, que so existe a partir do compilador do Windows 10, e o
    /// resultado e:
    ///
    ///     proxy encode compilation failed: error X3506: unrecognized compiler target 'cs_5_1'
    ///
    /// O Feeder entrega os frames, o log fica limpo do lado dele, e o filtro neural simplesmente
    /// nao roda. So acontece em jogo que traz a propria copia — Sonic e Sparking usam a do
    /// Windows e nunca viram isso.
    ///
    /// Troca so o mesmo nome por versao mais nova, com backup. Um jogo que pede outro nome
    /// (d3dcompiler_43) nao e tocado: renomear compilador para satisfazer um import e chute.
    /// </summary>
    private static void UpgradeShaderCompiler(string targetDir, IProgress<string>? progress)
    {
        const string nome = "d3dcompiler_47.dll";
        try
        {
            var local = Path.Combine(targetDir, nome);
            if (!File.Exists(local)) return;

            var sistema = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), nome);
            if (!File.Exists(sistema)) return;

            var vLocal = System.Diagnostics.FileVersionInfo.GetVersionInfo(local);
            var vSistema = System.Diagnostics.FileVersionInfo.GetVersionInfo(sistema);
            // cs_5_1 chegou com o compilador do Windows 10; o divisor de agua e a major version.
            if (vLocal.FileMajorPart >= vSistema.FileMajorPart) return;

            var backup = local + BackupSuffix;
            if (!File.Exists(backup)) File.Copy(local, backup);
            File.Copy(sistema, local, overwrite: true);
            MarkOurs(targetDir, nome);
            progress?.Report(L.T("Neural_CompilerUpgraded", vLocal.FileVersion ?? "?", vSistema.FileVersion ?? "?"));
            Log.Info($"neural: {nome} {vLocal.FileVersion} -> {vSistema.FileVersion} em {targetDir}");
        }
        catch (Exception ex) { Log.Warn($"neural compiler upgrade {targetDir}: {ex.Message}"); }
    }

    /// <summary>
    /// A pasta ja resolve runtimes de DLSS localmente?
    ///
    /// Nao conta o que este launcher escreveu: senao a primeira instalacao criaria a condicao
    /// que autoriza a segunda, e a resposta viraria "sim" para sempre a partir de um "nao".
    /// </summary>
    /// <summary>
    /// O runtime neural esta na pasta e inteiro?
    ///
    /// Sao 158 MB copiados de uma vez: uma copia interrompida (jogo aberto no meio, disco cheio,
    /// maquina desligada) deixa um arquivo com o nome certo e conteudo pela metade. Existir nao
    /// prova nada — o tamanho da copia na biblioteca prova.
    /// </summary>
    private static bool RuntimeIntact(string targetDir)
    {
        var deployed = Path.Combine(targetDir, RuntimeFile);
        if (!File.Exists(deployed)) return false;
        try
        {
            if (!File.Exists(LibraryRuntime)) return true;
            return new FileInfo(deployed).Length == new FileInfo(LibraryRuntime).Length;
        }
        catch { return true; }
    }

    /// <summary>
    /// Devolve a chave de ligado a 1 se ela tiver sido zerada. True quando houve o que corrigir.
    ///
    /// O addon escreve 0 aqui quando alguem desliga pelo overlay ou pela tecla F6, e nao volta
    /// sozinho. Escrever so quando esta 0 evita tocar no arquivo a cada abertura de jogo.
    /// </summary>
    public static bool ReassertEnabled(string iniPath)
    {
        if (!File.Exists(iniPath)) return false;
        var ini = new IniFile(iniPath);
        var atual = ini.Get(GenericSection, GenericEnableKey, ignoreCase: true);
        if (atual is null || atual.Trim() is "1" or "1.0" or "1.000000") return false;
        ini.Set(GenericSection, GenericEnableKey, "1");
        ini.Save();
        Log.Info($"neural: NeuralUplift estava '{atual}' em {iniPath}; devolvido para 1");
        return true;
    }

    /// <summary>
    /// Como <see cref="ReassertEnabled"/>, mas partindo da pasta do jogo: acha o ReShade.ini ao
    /// lado do addon, que e onde ele fica. Usado na varredura da abertura, que so tem o diretorio.
    /// </summary>
    public static bool ReassertEnabledIn(string installDir)
    {
        try
        {
            foreach (var ini in Directory.EnumerateFiles(installDir, "ReShade.ini", new EnumerationOptions
                     {
                         IgnoreInaccessible = true,
                         RecurseSubdirectories = true,
                         MaxRecursionDepth = 10,
                         AttributesToSkip = FileAttributes.ReparsePoint,
                     }))
            {
                // So onde a cadeia esta de fato instalada: um ReShade.ini de outro mod nao e nosso
                // para reescrever.
                var pasta = Path.GetDirectoryName(ini);
                if (pasta is null || DeployedGenericAddon(pasta) is null) continue;
                if (ReassertEnabled(ini)) return true;
            }
        }
        catch (Exception ex) { Log.Warn($"reassert em {installDir}: {ex.Message}"); }
        return false;
    }

    public static bool TemRuntimeLocal(string targetDir)
    {
        try
        {
            foreach (var f in Directory.GetFiles(targetDir, "*.dll"))
            {
                var nome = Path.GetFileName(f);
                var dlss = nome.StartsWith("nvngx_", StringComparison.OrdinalIgnoreCase)
                           || nome.StartsWith("sl.", StringComparison.OrdinalIgnoreCase);
                if (!dlss) continue;
                if (File.Exists(f + OursSuffix)) continue; // foi o launcher que pos
                return true;
            }
        }
        catch (Exception ex) { Log.Warn($"neural local runtime probe: {ex.Message}"); }
        return false;
    }

    private const string OursSuffix = ".renodx-ours";

    private static void MarkOurs(string targetDir, string fileName)
    {
        try { File.WriteAllText(Path.Combine(targetDir, fileName + OursSuffix), DateTime.UtcNow.ToString("o")); }
        catch (Exception ex) { Log.Warn($"neural mark {fileName}: {ex.Message}"); }
    }

    /// <summary>
    /// ReShade 6.8's early-load list. An addon named here is loaded from the proxy's
    /// <c>DllMain</c>, at process start, instead of at device creation.
    ///
    /// It is not a nicety. Several games initialize their DLSS SDK BEFORE creating the D3D
    /// device — Streamline's interposer loads at process start — so by the time ReShade would
    /// normally load an addon, NGX is already up and the addon's hooks are too late to see it.
    /// The community build reports that as "error 225" and its installer's whole INI step exists
    /// to set this one key. Harmless in a game that does not need it.
    /// </summary>
    private const string AddonSection = "ADDON";
    private const string EarlyLoadKey = "LoadFromDllMain";

    /// <summary>Add a file to the early-load list, keeping whatever is already there — other
    /// addons (renodx-dlssfix is a real case) are listed in the same key.</summary>
    private static void AddToEarlyLoad(IniFile ini, string addonFile)
    {
        var current = ini.Get(AddonSection, EarlyLoadKey, ignoreCase: true) ?? "";
        var entries = current.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (entries.Any(e => e.Equals(addonFile, StringComparison.OrdinalIgnoreCase))) return;
        entries.Add(addonFile);
        ini.Set(AddonSection, EarlyLoadKey, string.Join(',', entries));
    }

    private static void RemoveFromEarlyLoad(IniFile ini, string addonFile)
    {
        var current = ini.Get(AddonSection, EarlyLoadKey, ignoreCase: true);
        if (current is null) return;
        var entries = current.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(e => !e.Equals(addonFile, StringComparison.OrdinalIgnoreCase))
            .ToList();
        // An empty key is not the same as no key: ReShade reads "" as an empty list either way,
        // but leaving the key behind documents that we were here. Remove it when nothing is left.
        if (entries.Count == 0) ini.RemoveKey(AddonSection, EarlyLoadKey);
        else ini.Set(AddonSection, EarlyLoadKey, string.Join(',', entries));
    }

    /// <summary>Records which launcher build wrote <see cref="LibraryAddon"/>. Its absence is the
    /// signal that the file came from the user, not from us.</summary>
    private static string AddonStamp { get; } = Path.Combine(LibraryDir, "addon.stamp");

    /// <summary>
    /// The community build of the neural addon, and the exact bytes we expect.
    ///
    /// This is what the launcher deploys. The in-house addon was tried and is not shippable: it
    /// black-screens the game on launch, because it installs its hooks from a present callback
    /// while the render thread is already inside the functions being patched. A build that works
    /// everywhere beats one that is ours.
    ///
    /// The file is not signed by anybody — there is no certificate to check — so it is pinned by
    /// content hash instead. That is a stronger guarantee than a URL: the host can be replaced,
    /// the release can be re-tagged, and the bytes still have to be the ones this was tested
    /// against or nothing is installed.
    /// </summary>
    /// <summary>
    /// A ponte que faz o addon — que so engancha D3D12 — rodar em jogo DirectX 11.
    ///
    /// Ela cria um SEGUNDO device D3D12 ao lado do jogo e reproduz nele o mesmo contrato de DLSS
    /// que o jogo entrega ao NGX: copia cor e motion vectors para texturas compartilhadas,
    /// converte a profundidade num compute shader, sincroniza as duas filas com uma fence
    /// compartilhada, deixa o addon avaliar, e copia o resultado de volta. Cada tamanho,
    /// deslocamento e escalar sai do proprio bloco de parametros NGX do jogo.
    ///
    /// Sem ela, um jogo DX11 com DLSS nativo — Baldur's Gate 3 e o caso testado — nao tinha onde
    /// receber o pass, e o launcher so podia dizer "este jogo nao roda D3D12".
    ///
    /// Fonte publica (MIT, C++). Nao e assinada por ninguem, entao vale a mesma regra do addon:
    /// fixada por hash de conteudo, e nada e instalado se os bytes nao forem estes.
    /// </summary>
    public const string BridgeFile = "dlss5-dx11-bridge.addon64";
    /// <summary>
    /// A ponte vem sempre da ultima release, nao de uma tag fixa.
    ///
    /// Antes a URL apontava para a v1.0.21 e o download era conferido contra um SHA-256 escrito
    /// aqui. Fixar o hash protege de um binario trocado, mas congela a versao: quando saiu a
    /// v1.0.26, cinco versoes depois, o launcher continuava instalando a 1.0.21 e ninguem tinha
    /// como notar — e a ponte e o que faz o Baldur's Gate funcionar.
    ///
    /// O que substitui o hash e a verificacao de CONTEUDO, a mesma do caminho manual: precisa ser
    /// um PE que se declare a ponte. Isso nao prova autoria como um hash provaria, mas o hash so
    /// provava a autoria de UMA versao — e a alternativa real nao era "hash de todas", era ficar
    /// parado numa de agosto.
    /// </summary>
    private const string BridgeUrl =
        "https://github.com/NIGos/dlss5-dx11-bridge/releases/latest/download/dlss5-dx11-bridge.addon64";

    public static string LibraryBridge { get; } = Path.Combine(LibraryDir, BridgeFile);

    /// <summary>A ponte esta instalada neste jogo?</summary>
    /// <summary>
    /// A ponte esta na pasta E integra?
    ///
    /// "Existe" nao basta. Um download interrompido, uma copia parcial ou uma versao antiga
    /// deixam um arquivo com o nome certo que o ReShade carrega e que nao funciona — e o launcher
    /// dizia "pronto" para todos esses casos. Quando ha copia na biblioteca, o tamanho dela e a
    /// referencia; sem biblioteca, so resta acreditar no que esta la.
    /// </summary>
    public static bool BridgeDeployed(string targetDir)
    {
        var deployed = Path.Combine(targetDir, BridgeFile);
        if (!File.Exists(deployed)) return false;
        try
        {
            if (!File.Exists(LibraryBridge)) return true;
            return new FileInfo(deployed).Length == new FileInfo(LibraryBridge).Length;
        }
        catch { return true; }
    }

    /// <summary>
    /// Tira a ponte da pasta. Ponte e Feeder resolvem o mesmo problema por caminhos opostos e o
    /// autor do Feeder diz para nao rodar os dois: um reproduz o contrato de DLSS do jogo, o
    /// outro fabrica um do zero, e os dois juntos alimentam o mesmo pass em duplicidade.
    /// </summary>
    public static void RemoveBridge(string targetDir)
    {
        try
        {
            var p = Path.Combine(targetDir, BridgeFile);
            if (File.Exists(p)) File.Delete(p);
        }
        catch (Exception ex) { Log.Warn($"remover ponte {targetDir}: {ex.Message}"); }
    }

    /// <summary>Traz a ponte para a biblioteca se ela ainda nao estiver la.</summary>
    public static async Task<bool> FetchBridgeAsync(IProgress<string>? progress = null,
                                                    CancellationToken ct = default)
    {
        if (File.Exists(LibraryBridge)) return false;
        try
        {
            progress?.Report(L.T("Dlss5_Bridge_Fetching"));
            var bytes = await BaixarPonteAsync(ct);
            Directory.CreateDirectory(LibraryDir);
            await File.WriteAllBytesAsync(LibraryBridge, bytes, ct);
            Log.Info($"dlss5 bridge fetched ({bytes.Length} bytes)");
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex) { Log.Warn($"dlss5 bridge fetch: {ex.Message}"); return false; }
    }

    /// <summary>
    /// Busca a ultima ponte e, se for diferente da que temos, guarda e leva aos jogos que ja a
    /// usam. Devolve quantos jogos foram atualizados, ou -1 quando nao havia nada novo.
    ///
    /// Substituir arquivo dentro de jogo aberto nao da certo, entao esses ficam de fora e pegam
    /// a versao nova na proxima passada.
    /// </summary>
    public static async Task<int> UpdateBridgeAsync(IEnumerable<string> installDirs,
                                                    IProgress<string>? progress = null,
                                                    CancellationToken ct = default)
    {
        if (!File.Exists(LibraryBridge)) return -1;
        var bytes = await BaixarPonteAsync(ct);
        if (bytes.Length == new FileInfo(LibraryBridge).Length
            && Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
               == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(LibraryBridge))))
            return -1;

        await File.WriteAllBytesAsync(LibraryBridge, bytes, ct);
        Log.Info($"dlss5 bridge atualizada na biblioteca ({bytes.Length} bytes)");

        var n = 0;
        foreach (var dir in installDirs.Where(d => d is not null && Directory.Exists(d)))
        {
            foreach (var alvo in Directory.EnumerateFiles(dir, BridgeFile, new EnumerationOptions
                     {
                         IgnoreInaccessible = true,
                         RecurseSubdirectories = true,
                         MaxRecursionDepth = 10,
                         AttributesToSkip = FileAttributes.ReparsePoint,
                     }))
            {
                var pasta = Path.GetDirectoryName(alvo);
                if (pasta is null || AddonService.IsGameRunning(pasta)) continue;
                try { File.WriteAllBytes(alvo, bytes); n++; }
                catch (Exception ex) { Log.Warn($"atualizar ponte em {alvo}: {ex.Message}"); }
            }
        }
        progress?.Report(L.T("Dlss5_Bridge_Updated", n));
        return n;
    }

    /// <summary>
    /// Baixa a ponte e confere que e mesmo ela.
    ///
    /// A checagem e por conteudo: um PE que se declara a ponte na descricao que o ReShade le.
    /// Sem isso, uma pagina de erro devolvida com 200, ou um asset renomeado na release, viraria
    /// um arquivo com o nome certo que o jogo carrega e que nao faz nada.
    /// </summary>
    private static async Task<byte[]> BaixarPonteAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RenoDXLauncher/1.0");
        var bytes = await http.GetByteArrayAsync(BridgeUrl, ct);
        if (bytes.Length < 4096 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
            throw new InvalidOperationException(L.T("Neural_Fetch_BadAddon", bytes.Length));
        var texto = System.Text.Encoding.ASCII.GetString(bytes);
        if (!texto.Contains("DX11 Bridge", StringComparison.OrdinalIgnoreCase)
            && !texto.Contains("dlss5-dx11-bridge", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(L.T("Settings_Bridge_NotBridge"));
        return bytes;
    }

    /// <summary>
    /// Traz uma ponte que o usuario baixou para a biblioteca e a leva aos jogos que ja a tem.
    ///
    /// A ponte TEM release publica com URL estavel, ao contrario do addon — mas fixar o hash de
    /// uma versao significa que a proxima nao entra sozinha. Entao existe este caminho manual
    /// pelo mesmo motivo do addon: quando sair uma versao nova, apontar para o arquivo resolve,
    /// sem esperar que alguem edite a constante no codigo.
    ///
    /// A validacao aqui e por CONTEUDO, nao por nome: precisa ser um PE que se declare a ponte.
    /// </summary>
    public static int ImportBridge(string sourcePath, IEnumerable<string> installDirs)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException(sourcePath);
        var bytes = File.ReadAllBytes(sourcePath);
        if (bytes.Length < 4096 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
            throw new InvalidOperationException(L.T("Settings_Bridge_NotBridge"));
        // O binario se identifica na descricao que o ReShade le. Sem isso, qualquer .addon64
        // entraria como "ponte" e o jogo simplesmente nao ganharia D3D12 nenhum.
        var texto = System.Text.Encoding.ASCII.GetString(bytes);
        if (!texto.Contains("DX11 Bridge", StringComparison.OrdinalIgnoreCase)
            && !texto.Contains("dlss5-dx11-bridge", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(L.T("Settings_Bridge_NotBridge"));

        Directory.CreateDirectory(LibraryDir);
        File.WriteAllBytes(LibraryBridge, bytes);
        Log.Info($"dlss5 bridge imported from {sourcePath}");

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            MaxRecursionDepth = 10,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        var n = 0;
        foreach (var dir in installDirs.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, BridgeFile, options))
                {
                    if (new FileInfo(f).Length == bytes.Length
                        && File.ReadAllBytes(f).AsSpan().SequenceEqual(bytes)) continue;
                    if (AddonService.IsGameRunning(Path.GetDirectoryName(f)!)) continue;
                    var backup = f + BackupSuffix;
                    if (!File.Exists(backup)) File.Copy(f, backup);
                    File.WriteAllBytes(f, bytes);
                    n++;
                }
            }
            catch (Exception ex) { Log.Warn($"bridge propagate {dir}: {ex.Message}"); }
        }
        return n;
    }

    /// <summary>Coloca a ponte ao lado do addon. Inerte num jogo que ja e D3D12.</summary>
    public static void DeployBridge(string targetDir, IProgress<string>? progress = null)
    {
        if (!File.Exists(LibraryBridge)) return;
        var dest = Path.Combine(targetDir, BridgeFile);
        if (File.Exists(dest) && new FileInfo(dest).Length == new FileInfo(LibraryBridge).Length) return;
        File.Copy(LibraryBridge, dest, overwrite: true);
        progress?.Report(L.T("Dlss5_Bridge_Deployed"));
        Log.Info($"dlss5 bridge deployed to {targetDir}");
    }

    private const string CommunityAddonUrl =
        "https://github.com/zhubaohi/FF7R-DLSS5/releases/download/v1/renodx-dlss5-v2.5.addon64";
    private const string CommunityAddonSha256 =
        "87AEF9DDD937C7241E6BF8D8EFEA0045D63559135E254C60DAB316DB3D3A4AEE";
    private const long CommunityAddonLength = 391168;

    /// <summary>
    /// Put the launcher's own copy of the generic addon into the library.
    ///
    /// Without this the whole feature was unreachable in the case it exists for: a game with no
    /// RenoDX mod of its own can only be driven by the generic addon, the generic addon was only
    /// ever reachable through a manual import, and nothing in the app offered that import. So
    /// <see cref="Detection.Offerable"/> came back false and the card never appeared — an
    /// "automatic installer" whose first step was for the user to go find a DLL.
    ///
    /// A copy the user imported themselves is left alone: only a file this launcher wrote (which
    /// is what the stamp records) is replaced, and only when this build is newer than the one
    /// that wrote it.
    /// </summary>
    /// <summary>
    /// Bring a community NR addon already on this machine into the library, preferring it over
    /// the embedded copy.
    ///
    /// Where it looks is not a guess: the build passed around the RenoDX Discord is installed by
    /// dropping it into RHI's <c>Custom\Addons</c> folder, which is where anyone who followed the
    /// community instructions already has it — and it is also already deployed inside any game
    /// they set up by hand. Either is a newer, better-tested build than the one we ship, and
    /// making the user go find a file they already have is the definition of a manual step.
    /// </summary>
    /// <returns>The path it imported from, or null when no copy exists.</returns>
    public static string? AutoDiscoverAddon(IEnumerable<string> gameDirs)
    {
        // A copy the user placed in the library themselves already won: leave it.
        if (File.Exists(LibraryAddon) && !File.Exists(AddonStamp)) return null;

        var roots = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "RHI", "Custom", "Addons"),
        };
        roots.AddRange(gameDirs.Where(Directory.Exists));

        foreach (var root in roots.Where(Directory.Exists))
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(root, "*.addon64", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileName(f);
                    // Any name is fine as long as the bytes prove it can drive NR — the build is
                    // renamed constantly as versions go around (renodx-dlss5-v2.5.addon64 and so on).
                    if (!GenericAddonNames.Contains(name, StringComparer.OrdinalIgnoreCase)
                        && !name.StartsWith("renodx-dlss5", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!AddonSupportsNeuralRendering(f)) continue;

                    Directory.CreateDirectory(LibraryDir);
                    File.Copy(f, LibraryAddon, overwrite: true);
                    try { if (File.Exists(AddonStamp)) File.Delete(AddonStamp); } catch { }
                    Log.Info($"neural generic addon auto-discovered at {f}");
                    return f;
                }
            }
            catch (Exception ex) { Log.Warn($"neural addon search {root}: {ex.Message}"); }
        }
        return null;
    }

    public static async Task<bool> FetchAddonAsync(IProgress<string>? progress = null,
                                                   CancellationToken ct = default)
    {
        // A copy already here won — whether the user put it there or a previous fetch did.
        if (File.Exists(LibraryAddon)) return false;

        try
        {
            progress?.Report(L.T("Neural_FetchingAddon"));
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("RenoDXLauncher/1.0");
            var bytes = await http.GetByteArrayAsync(CommunityAddonUrl, ct);

            // Nothing signs this file, so the bytes are the whole guarantee. A mismatch is not a
            // warning to log and continue past: it means the thing on the other end is not what
            // this was tested against, and installing it would put an unknown DLL in a game.
            if (bytes.Length != CommunityAddonLength)
                throw new InvalidOperationException(L.T("Neural_Fetch_BadAddon", bytes.Length));
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
            if (!hash.Equals(CommunityAddonSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(L.T("Neural_Fetch_BadAddon", hash));
            if (!AddonSupportsNeuralRendering2(bytes))
                throw new InvalidOperationException(L.T("Neural_Import_NotNrAddon"));

            Directory.CreateDirectory(LibraryDir);
            await File.WriteAllBytesAsync(LibraryAddon, bytes, ct);
            // Stamped so a later launcher build may replace it; a hand import clears the stamp.
            await File.WriteAllTextAsync(AddonStamp, CommunityAddonSha256, ct);
            Log.Info("neural addon fetched from the community release and verified by hash");
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex) { Log.Warn($"neural addon fetch: {ex.Message}"); return false; }
    }

    private static bool AddonSupportsNeuralRendering2(byte[] bytes) => IndexOf(bytes, Marker) >= 0;

    /// <summary>
    /// The generic addon keeps its own settings block; it is not a RenoDX preset mod. These must
    /// match the build actually shipped in the library — writing the enable flag into the wrong
    /// section leaves the addon deployed and switched off, which reads as "the toggle does nothing".
    /// </summary>
    private const string GenericSection = "RenoDX.DLSS5";
    private const string GenericEnableKey = "NeuralUplift";

    /// <summary>An older in-house build used its own section. Cleared on remove so a leftover
    /// enable flag cannot switch a future build back on behind the user's back.</summary>
    private const string LegacySection = "RENODX-NEURAL";
    private const string LegacyEnableKey = "Enabled";

    // ---------- host ----------

    /// <summary>What this PC can do, independent of any game.</summary>
    /// <param name="GpuName">Marketing name of the NVIDIA adapter, for the "why not" message.</param>
    /// <param name="DriverBranch">616 for 616.56. 0 when no NVIDIA adapter was found.</param>
    public record HostCapability(string? GpuName, int DriverBranch, bool Blackwell, bool RuntimeInLibrary)
    {
        /// <summary>
        /// A exigencia de Blackwell vale para o runtime da NVIDIA, cujos kernels sao sm_120 e
        /// so isso. Um build da comunidade com os kernels recompilados cobre as arquiteturas
        /// anteriores — nesse caso a GPU deixa de ser o limite, e quem trouxe o arquivo ja sabe
        /// o que trouxe.
        /// </summary>
        public bool GpuOk => Blackwell || RuntimeIsCommunityBuild;

        public bool Ready => GpuOk && DriverBranch >= MinDriverBranch && RuntimeInLibrary;

        /// <summary>The single reason this host cannot run it, or null when it can. Ordered by
        /// what the user can actually act on: a missing file is fixable, a GPU is not.</summary>
        public string? Blocker =>
            !GpuOk ? L.T("Neural_Blocked_Gpu", GpuName ?? "?")
            : DriverBranch < MinDriverBranch ? L.T("Neural_Blocked_Driver", DriverBranch, MinDriverBranch)
            : !RuntimeInLibrary ? L.T("Neural_Blocked_Runtime", RuntimeFile)
            : null;
    }

    /// <summary>
    /// Read the NVIDIA adapter from the display-class registry key. WMI would be the obvious
    /// route but it costs a NuGet dependency (System.Management) and a slow first query; this
    /// key is where the driver itself writes the values.
    /// </summary>
    public static HostCapability ProbeHost()
    {
        string? name = null;
        int branch = 0;
        try
        {
            const string display = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
            using var root = Registry.LocalMachine.OpenSubKey(display);
            foreach (var sub in root?.GetSubKeyNames() ?? [])
            {
                // instance keys are 0000, 0001, ...; everything else here is config, not an adapter
                if (sub.Length != 4 || !sub.All(char.IsDigit)) continue;
                using var key = root!.OpenSubKey(sub);
                var desc = key?.GetValue("DriverDesc") as string;
                if (desc is null || !desc.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) continue;
                name = desc;
                branch = ParseDriverBranch(key?.GetValue("DriverVersion") as string);
                break;
            }
        }
        catch (Exception ex) { Log.Warn($"neural host probe: {ex.Message}"); }

        return new HostCapability(name, branch, IsBlackwell(name), File.Exists(LibraryRuntime));
    }

    /// <summary>
    /// Windows reports the NVIDIA driver as 32.0.16.1656; the branch users know is 616.56. The
    /// last five digits of the version are the branch and the minor, so 616 is the first three
    /// of those five — the leading components are the WDDM version, not NVIDIA's.
    /// </summary>
    public static int ParseDriverBranch(string? driverVersion)
    {
        if (string.IsNullOrEmpty(driverVersion)) return 0;
        var digits = new string(driverVersion.Where(char.IsDigit).ToArray());
        if (digits.Length < 5) return 0;
        return int.TryParse(digits[^5..][..3], out var branch) ? branch : 0;
    }

    /// <summary>
    /// The CG2R kernels are compiled for sm_120 only — Blackwell. This is not a marketing lock
    /// that a driver update lifts: the machine code for older architectures is not in the DLL.
    /// </summary>
    public static bool IsBlackwell(string? gpuName)
    {
        if (gpuName is null) return false;
        // "NVIDIA GeForce RTX 5090" / "... RTX 5070 Ti Laptop GPU"
        var i = gpuName.IndexOf("RTX ", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return false;
        var rest = gpuName[(i + 4)..].TrimStart();
        return rest.Length >= 4 && rest[0] == '5' && rest.Take(4).All(char.IsDigit);
    }

    // ---------- game ----------

    /// <param name="AddonSupportsNr">The installed addon build can create the DLSSNR feature.</param>
    /// <param name="HasDlss">Game ships a DLSS runtime — the addon needs DLSS active to have
    /// motion vectors and depth already flowing.</param>
    /// <param name="RuntimeDeployed">nvngx_dlssnr.dll is already next to the addon.</param>
    /// <param name="GenericAddonInLibrary">The game-agnostic addon is available to deploy.</param>
    /// <param name="ReShadeDllName">The ReShade proxy already in the game folder, or null. The
    /// generic addon is a ReShade addon, so without a host there is nothing to load it.</param>
    /// <param name="GameModDrivesNr">The game's OWN RenoDX mod carries NR. Distinct from
    /// <paramref name="AddonSupportsNr"/>, which also counts a generic addon deployed in the
    /// folder — the two were one flag, and conflating them wrote the switch into the wrong ini
    /// section for every game that already had a generic addon.</param>
    public record Detection(bool AddonSupportsNr, bool HasDlss, bool RuntimeDeployed, HostCapability Host,
                            bool GenericAddonInLibrary = false, string? ReShadeDllName = null,
                            bool GameModDrivesNr = false)
    {
        /// <summary>
        /// The generic addon is what drives NR — unless the game's OWN mod carries it.
        ///
        /// This used to be `!AddonSupportsNr`, and `AddonSupportsNr` had been widened to also mean
        /// "a generic addon is already deployed here". The two together said: a folder that
        /// already has the generic addon is a folder whose own mod drives NR — so `Apply` wrote
        /// the master switch into the RenoDX preset section, which the generic addon does not
        /// read. Installing then reported failure because the switch it had just written was in
        /// a section nothing consults. Measured: a game left with `[RenoDX.DLSS5] NeuralUplift=0`
        /// and `[renodx-preset1] NeuralUplift=1.000000`, the loaded addon off, the launcher's
        /// "on" in the wrong place.
        /// </summary>
        public bool UsesGeneric => !GameModDrivesNr;

        /// <summary>Offer the card when DLSS is present and SOMETHING can drive the feature —
        /// either the game's own mod, or the generic addon from the library.</summary>
        public bool Offerable => HasDlss && (AddonSupportsNr || GenericAddonInLibrary);

        /// <summary>Blocker specific to the generic path, on top of <see cref="HostCapability.Blocker"/>.
        /// A missing ReShade is NOT listed here: applying installs it, so reporting it as a blocker
        /// would stop the one action that fixes it.</summary>
        public string? GenericBlocker =>
            !UsesGeneric ? null
            : !GenericAddonInLibrary ? L.T("Neural_Blocked_Addon", GenericAddonFile)
            : null;

        /// <summary>Applying has to deploy ReShade first — the generic addon is a ReShade addon.</summary>
        public bool NeedsReShade => UsesGeneric && ReShadeDllName is null;
    }

    /// <summary>Does this addon build know how to drive DLSSNR? Answered from its bytes, because
    /// no catalog records it (see the class remarks).</summary>
    public static bool AddonSupportsNeuralRendering(string addonPath)
    {
        try
        {
            if (!File.Exists(addonPath)) return false;
            // addons are single-digit MB; the cap is here so a mistaken path cannot slurp a
            // multi-gigabyte file into memory
            if (new FileInfo(addonPath).Length > 64L * 1024 * 1024) return false;
            return IndexOf(File.ReadAllBytes(addonPath), Marker) >= 0;
        }
        catch (Exception ex) { Log.Warn($"neural addon scan {addonPath}: {ex.Message}"); return false; }
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        var limit = haystack.Length - needle.Length;
        for (var i = 0; i <= limit; i++)
        {
            var j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }

    /// <summary>Inspect one game: can anything here drive NR, and is the title a candidate.
    /// <paramref name="addonPath"/> may be null — a game with no RenoDX mod at all is still
    /// offerable through the generic addon.</summary>
    public static Detection Detect(string installDir, string targetDir, string? addonPath)
    {
        // A generic NR addon already in the folder drives the feature on its own, exactly like a
        // game mod that carries NR — so it counts, and the launcher stops offering to deploy a
        // second one beside it.
        // Duas perguntas diferentes, que estavam numa variavel so:
        //   gameModDrivesNr — o mod DO JOGO sabe acionar NR (decide em que secao do ini a chave
        //                     mestra vai);
        //   supports        — existe alguma coisa nesta pasta capaz de acionar (decide se o
        //                     cartao aparece).
        // O addon "do jogo" nao pode ser o generico. `AddonService.GetState` devolve o
        // `renodx-*.addon64` que encontrar na pasta, e o generico casa com esse padrao — entao
        // perguntar so "esse arquivo tem o marcador de NR?" respondia sim para ele proprio e a
        // conflacao voltava por outra porta: o Control, cujo mod proprio esta desativado, era
        // classificado como "o mod do jogo aciona o filtro".
        var gameModDrivesNr = addonPath != null
                              && !GenericAddonNames.Contains(Path.GetFileName(addonPath),
                                                             StringComparer.OrdinalIgnoreCase)
                              && AddonSupportsNeuralRendering(addonPath);
        var supports = gameModDrivesNr || DeployedGenericAddon(targetDir) != null;
        var hasDlss = false;
        try
        {
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                // Unreal parks the runtime at Engine\Plugins\Runtime\Nvidia\DLSS\Binaries\
                // ThirdParty\Win64 — eight levels down. A depth of 4 found it in none of them,
                // which silently hid the card in most DLSS titles, since UE is most of them.
                MaxRecursionDepth = 10,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };
            foreach (var f in Directory.EnumerateFiles(installDir, "nvngx_dls*.dll", options))
            {
                var name = Path.GetFileName(f).ToLowerInvariant();
                // SR or RR both work: either one means the addon has depth+mvec to hand over
                if (name is not ("nvngx_dlss.dll" or "nvngx_dlssd.dll")) continue;

                // ...desde que o arquivo seja DO JOGO. Um runtime que este launcher copiou nao
                // e evidencia de nada: e o resultado da nossa propria acao. Contando os nossos,
                // um jogo sem DLSS nenhum passava a "ter DLSS" logo depois da primeira
                // instalacao, e o launcher trocava o Feeder pela ponte na segunda passada — ou
                // pior, mandava o usuario ligar nas opcoes um DLSS que o jogo nao tem.
                //
                // O marcador sobrevive ao arquivo que ele descreve sumir, e e por isso que ele
                // e a memoria certa: apagar o addon do Feeder nao faz o jogo ganhar DLSS.
                if (File.Exists(f + OursSuffix)) continue;

                hasDlss = true;
                break;
            }
        }
        catch (Exception ex) { Log.Warn($"neural detect {installDir}: {ex.Message}"); }

        var (reshadeDll, _) = ReShadeService.Detect(targetDir);
        return new Detection(supports, hasDlss, RuntimeIntact(targetDir),
                             ProbeHost(), File.Exists(LibraryAddon), reshadeDll, gameModDrivesNr);
    }

    /// <summary>Applied = runtime deployed AND the addon's switch is on. Either half alone is a
    /// half-configured state the user would read as "it is on" while nothing happens.</summary>
    public static bool IsApplied(string targetDir, string iniPath, string? addonPath = null)
    {
        if (!File.Exists(Path.Combine(targetDir, RuntimeFile))) return false;
        if (!File.Exists(iniPath)) return false;

        // Runtime presente e chave ligada, sem NENHUM addon que saiba acionar a feature, e um
        // estado que le como "ligado" e se comporta como desligado — o pior dos dois. Acontece
        // de verdade: um jogo cujo mod RenoDX nao tem NR fica com a chave gravada de uma
        // tentativa anterior e o runtime na pasta, e nada carrega o filtro.
        var canDrive = (addonPath != null && AddonSupportsNeuralRendering(addonPath))
                       || DeployedGenericAddon(targetDir) != null;
        if (!canDrive) return false;

        var ini = new IniFile(iniPath);
        // Whichever addon drives it, the switch lives in the same place: [RenoDX.DLSS5].
        //
        // This used to pick the section from which addon FILE was present, and got it wrong for
        // a game running the community build under its own name — it fell through to the preset
        // key, found nothing, and reported "off" for a game whose ini plainly said NeuralUplift=1.
        // A UI that says off while the feature runs is worse than one that says nothing.
        var value = ini.Get(GenericSection, GenericEnableKey, ignoreCase: true)
                    ?? ini.Get(SettingsService.PresetSection, EnableKey, ignoreCase: true)
                    ?? ini.Get(LegacySection, LegacyEnableKey, ignoreCase: true);
        if (value is null) return false;
        // RenoDX writes floats ("1.000000"); compare numerically so "1", "1.0" and "1.000000" agree
        return double.TryParse(value.Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) && v != 0;
    }

    // ---------- apply / remove ----------

    /// <summary>Copy the runtime next to the addon and turn the addon's switch on. When the
    /// game's own mod cannot drive NR, the generic addon is deployed alongside it.</summary>
    public static void Apply(string targetDir, string iniPath, bool useGenericAddon = false,
                             IProgress<string>? progress = null)
    {
        if (AddonService.IsGameRunning(targetDir))
            throw new InvalidOperationException(L.T("Error_GameRunning"));
        if (!File.Exists(LibraryRuntime))
            throw new InvalidOperationException(L.T("Neural_Blocked_Runtime", RuntimeFile));
        if (useGenericAddon && !File.Exists(LibraryAddon))
            throw new InvalidOperationException(L.T("Neural_Blocked_Addon", GenericAddonFile));

        var deployed = Path.Combine(targetDir, RuntimeFile);
        // 158 MB: skip the copy when the same build is already there, or every toggle costs a
        // visible pause on a mechanical drive.
        //
        // A file that was already here is NOT ours, and this is the one runtime with no way back:
        // NVIDIA ships it in no driver and no public SDK. Record that we found it, so removing
        // later leaves it alone; and back it up before replacing, like every other runtime.
        if (!File.Exists(deployed))
        {
            progress?.Report(L.T("Neural_Deploying"));
            File.Copy(LibraryRuntime, deployed, overwrite: true);
            MarkRuntimeOurs(targetDir);
        }
        else if (new FileInfo(deployed).Length != new FileInfo(LibraryRuntime).Length)
        {
            progress?.Report(L.T("Neural_Deploying"));
            var backup = deployed + BackupSuffix;
            if (!File.Exists(backup)) File.Copy(deployed, backup);
            File.Copy(LibraryRuntime, deployed, overwrite: true);
        }

        DeployRayReconstruction(targetDir, progress);
        UpgradeShaderCompiler(targetDir, progress);

        var ini = new IniFile(iniPath);
        if (useGenericAddon)
        {
            progress?.Report(L.T("Neural_DeployingAddon"));
            File.Copy(LibraryAddon, Path.Combine(targetDir, GenericAddonFile), overwrite: true);
            ini.Set(GenericSection, GenericEnableKey, "1");
        }
        // Whichever addon drives it, it has to be up before the game's DLSS SDK is.
        if (DeployedGenericAddon(targetDir) is { } deployedAddon)
        {
            RefreshDeployedAddon(deployedAddon, progress);
            AddToEarlyLoad(ini, Path.GetFileName(deployedAddon));

            // A chave vale sempre que o addon esta na pasta, e nao so quando NOS o implantamos
            // nesta passada. Sem isto, instalar num jogo cujo mod proprio dirige o NR deixava
            // [RenoDX.DLSS5] como estava — inclusive em 0, se alguem tivesse apertado F6 antes.
            // Instalar tem de significar ligado, sem excecao.
            ini.Set(GenericSection, GenericEnableKey, "1");
        }
        if (!useGenericAddon)
        {
            ini.Set(SettingsService.PresetSection, EnableKey, "1.000000");
        }
        ini.Save();
        progress?.Report(L.T("Neural_Applied"));
    }

    /// <summary>
    /// Turn the switch off and take the runtime back out. The 158 MB file is the reason it is
    /// removed rather than left behind: leaving one per game silently costs gigabytes, and the
    /// library copy means restoring it is a click.
    /// </summary>
    public static void Remove(string targetDir, string iniPath)
    {
        if (AddonService.IsGameRunning(targetDir))
            throw new InvalidOperationException(L.T("Error_GameRunning"));

        if (File.Exists(iniPath))
        {
            var ini = new IniFile(iniPath);
            ini.Set(SettingsService.PresetSection, EnableKey, "0.000000");
            ini.Set(GenericSection, GenericEnableKey, "0");
            ini.Set(LegacySection, LegacyEnableKey, "0");
            // Only our own name comes back out of the early-load list. A community build the
            // user deployed themselves keeps its entry, or turning our copy off would stop
            // theirs from loading.
            RemoveFromEarlyLoad(ini, GenericAddonFile);
            ini.Save();
        }

        // Take back both files we may have put here. The generic addon is small, but leaving it
        // behind would keep hooking NGX in a game the user just turned the feature off in.
        //
        // A failure here is reported, not logged and swallowed: if the file survives, the feature
        // is still live in the game while the UI goes back to saying "off" — the exact shape of
        // "turning it off does nothing".
        // Only what this launcher put here. A community build the user deployed themselves is
        // left alone and simply switched off — deleting someone else's addon because we know its
        // file name is not ours to do.
        //
        // The same rule finally applies to the runtime. It used to be deleted unconditionally,
        // which meant a copy that was already in the folder — and folders that already hold one
        // are exactly where AutoDiscoverRuntime says the only copies on a machine live — was
        // destroyed by turning the feature off. That file ships in no driver and no public SDK,
        // so "delete it and re-copy from the library" is not a round trip when the library copy
        // is the same one that came from there.
        // O Feeder sai junto: ele so existe para alimentar este pass, e um addon que continua
        // fabricando motion vectors para um consumidor que nao esta mais la e puro custo por
        // frame. Os shaders de terceiro (iMMERSE) ficam — sao inertes sem a tecnica ligada.
        try { FeederService.Remove(targetDir); }
        catch (Exception ex) { Log.Warn($"feeder remove: {ex.Message}"); }

        var files = new List<string> { GenericAddonFile };
        if (RuntimeIsOurs(targetDir)) files.Add(RuntimeFile);
        // O RR segue a mesma regra, e por muito tempo nao seguia nenhuma: era copiado sempre e
        // nunca retirado, entao ficava para tras em pastas que nunca tiveram runtime local.
        if (File.Exists(Path.Combine(targetDir, RayReconstructionFile + OursSuffix)))
        {
            files.Add(RayReconstructionFile);
            try { File.Delete(Path.Combine(targetDir, RayReconstructionFile + OursSuffix)); }
            catch (Exception ex) { Log.Warn($"neural RR mark clear: {ex.Message}"); }
        }
        else Log.Info($"neural remove: {RuntimeFile} was already in {targetDir}; leaving it");

        var stuck = new List<string>();
        foreach (var name in files)
        {
            var deployed = Path.Combine(targetDir, name);
            try { if (File.Exists(deployed)) File.Delete(deployed); }
            catch (Exception ex)
            {
                Log.Warn($"neural remove {deployed}: {ex.Message}");
                stuck.Add(name);
            }
        }
        ClearRuntimeMark(targetDir);
        if (stuck.Count > 0)
            throw new InvalidOperationException(L.T("Neural_Remove_Locked", string.Join(", ", stuck)));
    }

    /// <summary>
    /// Find the runtime already sitting on this PC and bring it into the library, so the user does
    /// not have to know where it lives. NVIDIA ships it in no driver and no public SDK, so the only
    /// copies are the ones inside games that bundle it and whatever the user already downloaded —
    /// which is exactly what this looks through.
    ///
    /// Deliberately does NOT fetch it from the network: the file is not publicly distributed, and
    /// pulling a 158 MB NVIDIA binary off some mirror is not something the launcher should do on
    /// the user's behalf.
    /// </summary>
    /// <returns>The path it imported from, or null when no copy exists on this machine.</returns>
    public static string? AutoDiscoverRuntime(IEnumerable<string> gameDirs, IProgress<string>? progress = null)
    {
        if (File.Exists(LibraryRuntime)) return null;   // already have it

        var roots = new List<string>();
        // Where a user who went looking for it would have put it
        foreach (var known in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\Downloads",
                     Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                 })
        {
            if (Directory.Exists(known)) roots.Add(known);
        }
        roots.AddRange(gameDirs.Where(Directory.Exists));

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            MaxRecursionDepth = 10,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        foreach (var root in roots)
        {
            progress?.Report(L.T("Neural_Searching", Path.GetFileName(root)));
            try
            {
                foreach (var f in Directory.EnumerateFiles(root, RuntimeFile, options))
                {
                    // same size guard as the manual import: a stub or truncated copy fails inside
                    // the game with no message at all
                    if (new FileInfo(f).Length < 32L * 1024 * 1024) continue;
                    ImportRuntime(f);
                    Log.Info($"neural runtime auto-discovered at {f}");
                    return f;
                }
            }
            catch (Exception ex) { Log.Warn($"neural search {root}: {ex.Message}"); }
        }
        return null;
    }

    /// <summary>
    /// Last resort when no copy of the runtime exists on this machine: fetch the build the RHI
    /// index points at.
    ///
    /// This is the step that decided whether the installer was automatic at all. The runtime
    /// ships in no driver and no public SDK, so a user without a game that bundles it had no way
    /// to get past the "runtime missing" blocker except to go find a 158 MB DLL on their own.
    ///
    /// It is installed only if NVIDIA signed it and the digest is intact — the same check every
    /// runtime this launcher writes has to pass. A tampered mirror produces a refusal here, not a
    /// swapped DLL in a game folder.
    /// </summary>
    /// <returns>The version installed, or null when the index had nothing or the file failed the check.</returns>
    public static async Task<string?> FetchRuntimeAsync(DlssIndexService index,
                                                        IProgress<string>? progress = null,
                                                        CancellationToken ct = default)
    {
        if (File.Exists(LibraryRuntime)) return null;
        var entry = index.Newest(DlssIndexService.KindNeural);
        if (entry is null) { Log.Warn("neural runtime: index has no dlssnr entry"); return null; }

        try
        {
            var dir = await DlssIndexService.FetchAsync(entry, progress, ct);
            var dll = Directory.EnumerateFiles(dir, RuntimeFile, SearchOption.AllDirectories).FirstOrDefault();
            if (dll is null) { Log.Warn($"neural runtime: {RuntimeFile} not in the archive"); return null; }

            if (!DlssRuntimeService.IsGenuine(dll, out var why))
            {
                Log.Warn($"neural runtime rejected: {why}");
                throw new InvalidOperationException(L.T("Neural_Fetch_NotGenuine", why));
            }
            if (new FileInfo(dll).Length < 32L * 1024 * 1024)
                throw new InvalidOperationException(L.T("Neural_Import_TooSmall"));

            Directory.CreateDirectory(LibraryDir);
            File.Copy(dll, LibraryRuntime, overwrite: true);
            Log.Info($"neural runtime fetched from the RHI index ({entry.Version})");
            return entry.Version;
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex) { Log.Warn($"neural runtime fetch: {ex.Message}"); return null; }
    }

    /// <summary>
    /// A versao que o proprio binario declara, ou null.
    ///
    /// Lida do arquivo e nao do nome: esses builds circulam renomeados de todo jeito —
    /// `renodx-dlss5 3,3.4.addon64`, `renodx-dlss5(3).addon64`, `renodx-dlss5++.addon64` — e o
    /// nome nao diz o que ha dentro. A string `vX.Y.Z` diz.
    /// </summary>
    public static string? ReadAddonVersion(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 64L * 1024 * 1024) return null;
            var texto = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(path));
            // A versao do RUNTIME (v310.8.0) tambem aparece; a do addon e a que nao e 310.
            return System.Text.RegularExpressions.Regex.Matches(texto, @"\bv(\d+\.\d+\.\d+)\b")
                .Select(m => m.Groups[1].Value)
                .FirstOrDefault(v => !v.StartsWith("310.", StringComparison.Ordinal));
        }
        catch (Exception ex) { Log.Warn($"neural version {path}: {ex.Message}"); return null; }
    }

    /// <summary>
    /// Leva o build da biblioteca para todo jogo que ja tem o addon instalado.
    ///
    /// Sem isto, trocar o addon na biblioteca so valia para instalacoes NOVAS: um jogo ja
    /// configurado ficava no build que chegou primeiro ate alguem aplicar nele de novo, um por
    /// um. Quando a correcao e de frame preto, ficar uma versao atras nao e detalhe.
    /// </summary>
    /// <returns>Quantos jogos foram atualizados.</returns>
    public static int PropagateAddon(IEnumerable<string> installDirs, IProgress<string>? progress = null)
    {
        if (!File.Exists(LibraryAddon)) return 0;
        var alvo = File.ReadAllBytes(LibraryAddon);
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            MaxRecursionDepth = 10,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        var n = 0;
        foreach (var dir in installDirs.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var nome in GenericAddonNames)
            {
                IEnumerable<string> achados;
                try { achados = Directory.EnumerateFiles(dir, nome, options).ToList(); }
                catch (Exception ex) { Log.Warn($"neural propagate {dir}: {ex.Message}"); continue; }

                foreach (var f in achados)
                {
                    try
                    {
                        if (new FileInfo(f).Length == alvo.Length
                            && File.ReadAllBytes(f).AsSpan().SequenceEqual(alvo)) continue;
                        if (AddonService.IsGameRunning(Path.GetDirectoryName(f)!))
                        {
                            progress?.Report(L.T("Error_GameRunning"));
                            continue;
                        }
                        var backup = f + BackupSuffix;
                        if (!File.Exists(backup)) File.Copy(f, backup);
                        File.WriteAllBytes(f, alvo);
                        n++;
                        Log.Info($"neural propagate: {f}");
                    }
                    catch (Exception ex) { Log.Warn($"neural propagate {f}: {ex.Message}"); }
                }
            }
        }
        return n;
    }

    /// <summary>Bring the generic addon into the library. Validated the same way as the runtime:
    /// a wrong file here fails silently inside the game.</summary>
    public static void ImportAddon(string sourcePath)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException(sourcePath);
        if (!AddonSupportsNeuralRendering(sourcePath))
            throw new InvalidOperationException(L.T("Neural_Import_NotNrAddon"));

        Directory.CreateDirectory(LibraryDir);
        File.Copy(sourcePath, LibraryAddon, overwrite: true);
        // Drop the stamp: from here the library copy is the user's, and a launcher update must
        // not quietly replace the build they went and got.
        try { if (File.Exists(AddonStamp)) File.Delete(AddonStamp); }
        catch (Exception ex) { Log.Warn($"neural addon stamp: {ex.Message}"); }
        Log.Info($"neural generic addon imported from {sourcePath}");
    }

    /// <summary>Install the runtime into the library from wherever the user found it. Strict about
    /// what it accepts: a wrong file here fails inside the game with no message at all.</summary>
    public static void ImportRuntime(string sourcePath)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException(sourcePath);
        if (!Path.GetFileName(sourcePath).Equals(RuntimeFile, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(L.T("Neural_Import_WrongName", RuntimeFile));

        var info = new FileInfo(sourcePath);
        // the real runtime is ~158 MB; anything tiny is a stub, a rename, or a truncated download
        if (info.Length < 32L * 1024 * 1024)
            throw new InvalidOperationException(L.T("Neural_Import_TooSmall"));

        Directory.CreateDirectory(LibraryDir);
        File.Copy(sourcePath, LibraryRuntime, overwrite: true);

        // A NVIDIA compila os kernels CG2R so para sm_120, e por isso o launcher recusa GPU que
        // nao seja Blackwell. Existem builds da comunidade com os kernels recompilados para as
        // arquiteturas anteriores; nelas a assinatura NAO fecha, porque o binario foi alterado
        // depois de assinado. Registrar isso e o que permite levantar aquele bloqueio sem fingir
        // que o arquivo e o original — e sem escondê-lo de quem for usar.
        try
        {
            if (DlssRuntimeService.IsGenuine(LibraryRuntime, out var porque))
            {
                if (File.Exists(RuntimeCustomMark)) File.Delete(RuntimeCustomMark);
            }
            else
            {
                File.WriteAllText(RuntimeCustomMark, porque);
                Log.Warn($"neural runtime importado SEM assinatura valida: {porque}");
            }
        }
        catch (Exception ex) { Log.Warn($"neural runtime mark: {ex.Message}"); }

        Log.Info($"neural runtime imported from {sourcePath} ({info.Length / (1024 * 1024)} MB)");
    }

    /// <summary>Marca que o runtime na biblioteca foi trazido pelo usuario e nao tem assinatura
    /// valida da NVIDIA.</summary>
    private static string RuntimeCustomMark { get; } = Path.Combine(LibraryDir, "runtime.custom");

    /// <summary>
    /// O runtime da biblioteca e um build da comunidade, sem assinatura?
    ///
    /// Isso muda duas coisas: o bloqueio por GPU deixa de valer (os kernels podem cobrir as
    /// arquiteturas antigas) e a interface passa a dizer, sem rodeios, que o arquivo nao e o
    /// que a NVIDIA assinou.
    /// </summary>
    public static bool RuntimeIsCommunityBuild => File.Exists(RuntimeCustomMark);

    // ---------- knobs ----------

    /// <summary>
    /// The addon's NR settings, for games whose build has them. These are not in the launcher's
    /// generated manifest and will not be until the feature lands in renodx upstream: the
    /// manifest is extracted from published source, and these builds are not published. Without
    /// this list the user gets a working toggle and no way to tune it — and NR at full intensity
    /// is exactly the setting people want to back off from first.
    /// </summary>
    public static IReadOnlyList<SettingDef> Knobs { get; } =
    [
        new SettingDef
        {
            Key = "NRIntensity", Type = "float", Default = 1.0, Min = 0.0, Max = 1.0,
            Label = "NR intensity", Section = "Neural Uplift", IniSection = GenericSection,
            Tooltip = "How much of the neural result is blended over the frame.",
        },
        new SettingDef
        {
            Key = "NRSkinStructure", Type = "float", Default = -1.0, Min = -1.0, Max = 1.0,
            Label = "NR skin structure strength", Section = "Neural Uplift", IniSection = GenericSection,
            Tooltip = "Detail rebuilt on skin. -1 = follow local structure strength (default).",
        },
        new SettingDef
        {
            Key = "NRLocalStructure", Type = "float", Default = 1.0, Min = 0.0, Max = 1.0,
            Label = "NR local structure strength", Section = "Neural Uplift", IniSection = GenericSection,
            Tooltip = "Detail rebuilt across the frame generally.",
        },
        new SettingDef
        {
            Key = "NRLocalTone", Type = "float", Default = 1.0, Min = 0.0, Max = 1.0,
            Label = "NR local tone strength", Section = "Neural Uplift", IniSection = GenericSection,
            Tooltip = "How far local contrast is allowed to move.",
        },
        new SettingDef
        {
            Key = "NRColorStrength", Type = "float", Default = 0.0, Min = 0.0, Max = 1.0,
            Label = "NR colour strength", Section = "Neural Uplift", IniSection = GenericSection,
            Tooltip = "0 = keep the game's hue and saturation, take only NR's brightness and "
                    + "structure. 1 = NR's colour too (shifts hues, drains saturation).",
        },
        new SettingDef
        {
            Key = "NRAutoMask", Type = "bool", Default = 1.0,
            Label = "NR auto skin mask", Section = "Neural Uplift", IniSection = GenericSection,
            Tooltip = "Let the model decide where skin is instead of treating the whole frame alike.",
        },
        new SettingDef
        {
            Key = "NRPreset", Type = "int", Default = 0.0, Min = 0.0, Max = 5.0,
            Label = "NR preset", Section = "Neural Uplift", IniSection = GenericSection,
            Tooltip = "Which CG2R weight set to load. Presets differ in look, not just strength.",
        },
        new SettingDef
        {
            Key = "NRStyle", Type = "int", Default = 0.0, Min = 0.0, Max = 3.0,
            Label = "NR style", Section = "Neural Uplift", IniSection = GenericSection,
            Tooltip = "Which look the model aims for within the chosen preset.",
        },
        new SettingDef
        {
            Key = "NRGlobalTone", Type = "float", Default = 1.0, Min = 0.0, Max = 1.0,
            Label = "NR global tone strength", Section = "Neural Uplift", IniSection = GenericSection,
            Tooltip = "How far the model may move the overall exposure of the frame.",
        },
        new SettingDef
        {
            // The single knob that buys back frame time. The pass costs the square of its
            // resolution, which is the loudest complaint about this technology; the result is
            // composed back as a RATIO over the full-resolution frame, so fine structure still
            // comes from the game and 0.70 costs about half for a small difference.
            Key = "NRResolutionScale", Type = "float", Default = 1.0, Min = 0.34, Max = 1.0,
            Label = "NR network resolution", Section = "Neural Uplift", IniSection = GenericSection,
            Tooltip = "Fraction of the frame's resolution the network runs at. 1.0 = full. "
                    + "Lower is much faster and costs little, because the model's edit is "
                    + "transferred as a ratio over the full-resolution frame.",
        },
        new SettingDef
        {
            // The feature can read at one resolution and write at another. On, it reads at the
            // game's RENDER resolution — the same grid depth and motion vectors already live on,
            // so nothing has to be resampled — and writes at the final one.
            Key = "NREnableUpscaling", Type = "bool", Default = 0.0,
            Label = "NR lets the network scale", Section = "Neural Uplift", IniSection = GenericSection,
            Tooltip = "Let the neural feature do the upscale itself, reading at the game's render "
                    + "resolution, instead of running 1:1 over the finished DLSS output.",
        },
        new SettingDef
        {
            Key = "NRDepthMode", Type = "int", Default = 0.0, Min = 0.0, Max = 2.0,
            Label = "NR depth convention", Section = "Neural Uplift", IniSection = GenericSection,
            Tooltip = "0 = the flag the game declared, 1 = force normal, 2 = force inverted. "
                    + "A wrong flag makes the filter read the scene inside out without failing "
                    + "at anything — there is no error, just a wrong result.",
        },
        new SettingDef
        {
            Key = "NRMVecScaleX", Type = "float", Default = 1.0, Min = 0.25, Max = 4.0,
            Label = "NR motion scale X", Section = "Neural Uplift", IniSection = GenericSection,
            Tooltip = "Manual multiplier over the computed motion vector scale. For when the "
                    + "scale the game declared is wrong: the result smears in motion with no "
                    + "error appearing anywhere.",
        },
        new SettingDef
        {
            Key = "NRMVecScaleY", Type = "float", Default = 1.0, Min = 0.25, Max = 4.0,
            Label = "NR motion scale Y", Section = "Neural Uplift", IniSection = GenericSection,
            Tooltip = "See motion scale X.",
        },
        new SettingDef
        {
            Key = "NRTransferStrength", Type = "float", Default = 1.0, Min = 0.0, Max = 1.0,
            Label = "NR transfer strength", Section = "Neural Uplift", IniSection = GenericSection,
            Tooltip = "How much of the model's edit reaches the frame. 0 = the game's frame "
                    + "untouched, useful for an A/B against the same scene.",
        },
        new SettingDef
        {
            Key = "NRUseExtras", Type = "bool", Default = 1.0,
            Label = "NR use the game's extra inputs", Section = "Neural Uplift", IniSection = GenericSection,
            Tooltip = "Pass the reactive mask and HUD-less colour through when the game supplies "
                    + "them. Off isolates the effect to the four required buffers.",
        },
        new SettingDef
        {
            Key = "NeuralUpH", Type = "bool", Default = 1.0,
            Label = "NR proxy tonemap (HDR)", Section = "Neural Uplift", IniSection = GenericSection,
            Tooltip = "In HDR, NR edits an SDR proxy of the frame and its edit is transferred onto "
                    + "the HDR frame. Off = raw linear scRGB into the model (mottled dark clouds). "
                    + "Ignored in SDR, where the frame already is what NR wants.",
        },
    ];
}
