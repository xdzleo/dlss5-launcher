using System.Diagnostics;
using System.IO;
using RenoDXLauncher.Localization;

namespace RenoDXLauncher.Services;

/// <summary>
/// The NGX runtimes a game ships — Super Resolution, Frame Generation, Ray Reconstruction — and
/// keeping them current.
///
/// Why the launcher touches them at all: a game bundles whichever DLSS build existed when it
/// shipped and, unless the studio patches it, that is what runs forever. Newer runtimes are
/// drop-in — same exports, same NGX contract — so replacing the file is the whole upgrade, and it
/// is the standard fix for the artefacts an old Super Resolution build produces.
///
/// The library is filled from copies already on this machine, never from the network. Every game
/// with DLSS carries a signed NVIDIA runtime, so the newest one the user already owns is a real
/// source. Fetching NVIDIA binaries off a mirror on the user's behalf is not something this app
/// should do.
///
/// Two rules make the swap safe to undo:
///   - the original is moved aside to <c>.renodx-bak</c> before anything is written, and never
///     deleted, so <see cref="Restore"/> always has the studio's build to put back;
///   - nothing is installed unless it is Authenticode-signed by NVIDIA with an intact digest.
///     An unsigned or tampered runtime in a game folder is exactly the shape of an attack, and
///     the file arrives from wherever the user happened to have it.
/// </summary>
public static class DlssRuntimeService
{
    /// <summary>Runtime file names, with the feature each one drives.</summary>
    public static readonly IReadOnlyDictionary<string, string> Runtimes = new Dictionary<string, string>
        (StringComparer.OrdinalIgnoreCase)
    {
        ["nvngx_dlss.dll"]  = "Super Resolution",
        ["nvngx_dlssg.dll"] = "Frame Generation",
        ["nvngx_dlssd.dll"] = "Ray Reconstruction",
    };

    /// <summary>
    /// Super Resolution and Ray Reconstruction stand alone: the game calls them through NGX and a
    /// newer build answers the same contract, so the file can be replaced by itself.
    ///
    /// Frame Generation cannot, and that is not a limitation of this tool — `nvngx_dlssg.dll` is
    /// driven by the Streamline interposer the game ships with, and the two are versioned as a
    /// pair. Measured on this machine: the games carry Streamline 2.7.x with nvngx_dlssg 310.7.129,
    /// while the newer set is Streamline 2.13.0 with 310.8.0. Replacing only the nvngx half leaves
    /// a 2.7 interposer calling a 310.8 runtime — which is how a DLSS swap becomes a crash on
    /// launch. See <see cref="StreamlineSet"/> for the path that does work.
    /// </summary>
    private static readonly HashSet<string> Swappable = new(StringComparer.OrdinalIgnoreCase)
    {
        "nvngx_dlss.dll",   // Super Resolution
        "nvngx_dlssd.dll",  // Ray Reconstruction
    };

    /// <summary>
    /// The Streamline half of a Frame Generation upgrade. These move together with
    /// `nvngx_dlssg.dll`, all-or-nothing: the interposer is what the game links against, and every
    /// plugin next to it has to come from the same SDK build.
    ///
    /// This is a bigger step than swapping Super Resolution, because the interposer is loaded by
    /// the game itself rather than reached through NGX — so it is offered separately instead of
    /// riding along with "update".
    /// </summary>
    private static readonly string[] StreamlineSet =
    [
        "sl.interposer.dll", "sl.common.dll", "sl.dlss.dll", "sl.dlss_g.dll",
        "sl.dlss_nr.dll", "sl.pcl.dll", "sl.reflex.dll", "sl.nis.dll",
    ];

    /// <summary>O Frame Generation nao e trocado — ver <see cref="Swappable"/>.</summary>
    public static bool IsSwappable(string fileName) => Swappable.Contains(fileName);

    /// <summary>Suffix for the studio's original. Distinct enough not to collide with a game's
    /// own backups, and searchable when a user asks what the launcher put in their folder.</summary>
    public const string BackupSuffix = ".renodx-bak";

    /// <summary>NVIDIA's signing subject. Matched on the certificate, not on the file name.</summary>
    private const string NvidiaSubject = "NVIDIA Corporation";

    public static string LibraryDir { get; } = Path.Combine(AppPaths.DataDir, "dlss");

    /// <summary>
    /// Conjunto Streamline coerente, guardado inteiro e nunca peca a peca. E o que permite
    /// consertar um jogo cujo conjunto foi quebrado por outra ferramenta: sem uma referencia
    /// COMPLETA e do mesmo build, "consertar" seria trocar mais uma peca solta — o proprio defeito.
    /// </summary>
    public static string LibrarySetDir { get; } = Path.Combine(LibraryDir, "streamline");

    /// <param name="Feature">Which DLSS feature this runtime drives, for the UI.</param>
    public record RuntimeInfo(string FileName, string Path, Version Version, string Feature)
    {
        public string Display => $"{Feature} {Version.Major}.{Version.Minor}.{Version.Build}";
    }

    // ---------- reading ----------

    /// <summary>Version from the PE resource. DLSS runtimes report their real build here
    /// (310.8.0.0), which is what the DLSS ecosystem calls "the version".</summary>
    public static Version? ReadVersion(string path)
    {
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(path);
            if (vi.FileMajorPart == 0 && vi.FileMinorPart == 0) return null;
            return new Version(vi.FileMajorPart, vi.FileMinorPart, vi.FileBuildPart, vi.FilePrivatePart);
        }
        catch (Exception ex) { Log.Warn($"dlss version {path}: {ex.Message}"); return null; }
    }

    /// <summary>True when the file really is an NVIDIA-signed runtime with an intact digest and a
    /// certificate chain that reaches a trusted root.</summary>
    public static bool IsGenuine(string path, out string detail)
    {
        var r = Authenticode.Verify(path);
        detail = r.Detail;
        if (!r.DigestIntact) return false;
        if (r.Subject is null || !r.Subject.Contains(NvidiaSubject, StringComparison.OrdinalIgnoreCase))
        {
            detail = L.T("Dlss_NotNvidia", r.Subject ?? "?");
            return false;
        }
        // O subject diz quem assinou; a cadeia e quem prova. Sem exigir a cadeia, "assinado pela
        // NVIDIA" era uma comparacao de texto: qualquer certificado auto-assinado com "NVIDIA
        // Corporation" no nome volta do WinVerifyTrust como CERT_E_UNTRUSTEDROOT com o digesto
        // integro, e passava por aqui. Uma CA de verdade nao emite esse nome para outra empresa.
        if (!r.ChainTrusted)
        {
            // A mesma recusa tem duas causas com consertos opostos. Auto-assinado com nome de
            // NVIDIA: nao ha o que fazer, o arquivo e falso. Certificado emitido por uma CA cuja
            // raiz nunca chegou ao repositorio da maquina (offline, ou atualizacao automatica de
            // raizes bloqueada por politica): o arquivo e genuino e o conserto e na maquina. Um
            // "nao assinado" generico escondia o segundo caso atras do primeiro.
            detail = r.RootStoreMissingIssuer
                ? L.T("Dlss_MissingRoot", r.Issuer ?? "?", r.Detail)
                : L.T("Dlss_UntrustedChain", r.Detail);
            return false;
        }
        return true;
    }

    /// <summary>
    /// O arquivo veio de um pacote que a NVIDIA nao lancou?
    ///
    /// O sinal e o vizinho: `nvngx_dlssnr.dll` nao existe em driver nem em SDK publico, entao uma
    /// pasta que o contenha e um drop pre-release inteiro — os outros runtimes ali sao da mesma
    /// leva. Uma build assim tem numero de versao MAIOR, e a regra "maior e melhor" a escolhia como
    /// se fosse uma release: e o jogo recebia um runtime que nunca foi publicado nem testado com
    /// ele. Foi assim que um Black Myth Wukong com o resto em 310.7.129 acabou com um Super
    /// Resolution 310.8.0 ao lado, e passou a travar.
    ///
    /// O runtime pre-release continua servindo para o que ele existe — o filtro neural — mas nao
    /// entra como "atualizacao" de um jogo.
    /// </summary>
    public static bool IsPreRelease(string runtimePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(runtimePath);
            if (dir is null) return false;
            return File.Exists(Path.Combine(dir, NeuralUpliftService.RuntimeFile));
        }
        catch { return false; }
    }

    private static EnumerationOptions Walk => new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = true,
        // Unreal parks these at Engine\Plugins\Runtime\Nvidia\DLSS\Binaries\ThirdParty\Win64
        MaxRecursionDepth = 10,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    /// <summary>The newest runtime of each kind sitting in the library.</summary>
    public static IReadOnlyList<RuntimeInfo> Library()
    {
        var found = new List<RuntimeInfo>();
        if (!Directory.Exists(LibraryDir)) return found;
        foreach (var (name, feature) in Runtimes)
        {
            var p = Path.Combine(LibraryDir, name);
            if (!File.Exists(p)) continue;
            var v = ReadVersion(p);
            if (v != null) found.Add(new RuntimeInfo(name, p, v, feature));
        }
        return found;
    }

    /// <summary>Every runtime the game carries. A game can hold the same one more than once
    /// (an Engine copy and a plugin copy); all of them are returned so all of them get updated.</summary>
    public static IReadOnlyList<RuntimeInfo> DetectInGame(string installDir)
    {
        var found = new List<RuntimeInfo>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(installDir, "nvngx_dls*.dll", Walk))
            {
                var name = Path.GetFileName(f);
                if (!Runtimes.TryGetValue(name, out var feature)) continue;
                var v = ReadVersion(f);
                if (v != null) found.Add(new RuntimeInfo(name, f, v, feature));
            }
        }
        catch (Exception ex) { Log.Warn($"dlss detect {installDir}: {ex.Message}"); }
        return found;
    }

    // ---------- library ----------

    /// <summary>
    /// Sweep the machine for runtimes newer than what the library holds and keep the best of each.
    /// The games themselves are the source: every DLSS title ships a signed NVIDIA build, and the
    /// newest one among them is a legitimate upgrade for the oldest.
    /// </summary>
    /// <returns>How many library entries were added or upgraded.</returns>
    public static int AutoDiscover(IEnumerable<string> searchDirs, IProgress<string>? progress = null)
    {
        var best = new Dictionary<string, (Version v, string path)>(StringComparer.OrdinalIgnoreCase);
        foreach (var cur in Library()) best[cur.FileName] = (cur.Version, cur.Path);

        foreach (var root in searchDirs.Where(Directory.Exists))
        {
            progress?.Report(L.T("Dlss_Searching", Path.GetFileName(root.TrimEnd('\\'))));
            try
            {
                foreach (var f in Directory.EnumerateFiles(root, "nvngx_dls*.dll", Walk))
                {
                    var name = Path.GetFileName(f);
                    if (!Runtimes.ContainsKey(name)) continue;
                    var v = ReadVersion(f);
                    if (v is null) continue;
                    if (best.TryGetValue(name, out var cur) && v <= cur.v) continue;
                    if (!IsGenuine(f, out var why)) { Log.Warn($"dlss skip {f}: {why}"); continue; }
                    if (IsPreRelease(f)) { Log.Info($"dlss skip {f}: build pre-release"); continue; }
                    best[name] = (v, f);
                }
            }
            catch (Exception ex) { Log.Warn($"dlss search {root}: {ex.Message}"); }
        }

        Directory.CreateDirectory(LibraryDir);
        var changed = 0;
        foreach (var (name, (v, path)) in best)
        {
            var dest = Path.Combine(LibraryDir, name);
            // already the library's own copy at that version
            if (string.Equals(path, dest, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                File.Copy(path, dest, overwrite: true);
                Log.Info($"dlss library: {name} -> {v} (from {path})");
                changed++;
            }
            catch (Exception ex) { Log.Warn($"dlss import {path}: {ex.Message}"); }
        }
        return changed;
    }

    // ---------- apply / restore ----------

    /// <param name="Updated">Files replaced with a newer build.</param>
    /// <param name="AlreadyCurrent">Files already at or above the library's version.</param>
    public record ApplyResult(int Updated, int AlreadyCurrent, IReadOnlyList<string> Notes);

    /// <summary>
    /// Bring every runtime in the game up to the library's version. Only ever upgrades: a game
    /// shipping something newer than the library is left alone, because downgrading it would be
    /// a regression the user did not ask for.
    /// </summary>
    public static ApplyResult Apply(string installDir, string targetDir, IProgress<string>? progress = null)
    {
        if (AddonService.IsGameRunning(targetDir))
            throw new InvalidOperationException(L.T("Error_GameRunning"));

        var library = Library().ToDictionary(r => r.FileName, StringComparer.OrdinalIgnoreCase);
        if (library.Count == 0) throw new InvalidOperationException(L.T("Dlss_Library_Empty"));

        int updated = 0, current = 0;
        var notes = new List<string>();

        foreach (var game in DetectInGame(installDir))
        {
            // Frame Generation nunca e trocado — ver Swappable.
            if (!Swappable.Contains(game.FileName)) continue;
            if (!library.TryGetValue(game.FileName, out var lib)) continue;
            if (lib.Version <= game.Version) { current++; continue; }

            // DLSS 1.x nao e uma versao antiga do DLSS atual — e outra API.
            //
            // A geracao 1.0 nao usa motion vectors, tem um modelo treinado por jogo e um contrato
            // de chamada distinto. Trocar essa DLL por uma 310.x nao atualiza nada: DESLIGA o DLSS
            // do jogo, porque a implementacao nova nao atende as chamadas que ele faz. E como o
            // filtro neural entra por cima do DLSS, ele fica sem contrato para capturar — a
            // instalacao termina limpa, o log mostra os hooks armados, e nada acontece na tela.
            //
            // Aconteceu no Final Fantasy XV (nvngx_dlss.dll 1.0.11), um dos poucos titulos dessa
            // geracao. A comparacao de versao sozinha nao protege: 1.0.11 e menor que 310.8, entao
            // a troca parecia um upgrade obvio.
            if (game.Version is { Major: 1 })
            {
                current++;
                notes.Add(L.T("Dlss_Skipped_Gen1", game.Feature, game.Version.ToString()));
                Log.Info($"dlss apply: {game.Path} mantido em {game.Version} (DLSS 1.x, API incompativel)");
                continue;
            }

            var backup = game.Path + BackupSuffix;
            try
            {
                // The studio's build is preserved once and never overwritten: a second Apply must
                // not turn our previous copy into "the original".
                if (!File.Exists(backup)) File.Copy(game.Path, backup);
                progress?.Report(L.T("Dlss_Updating", game.Feature, game.Version, lib.Version));
                File.Copy(lib.Path, game.Path, overwrite: true);
                updated++;
                notes.Add($"{game.Feature}: {game.Version} -> {lib.Version}");
                // Registrado por arquivo: sem isso, depois de um jogo comecar a travar nao ha como
                // saber o que foi trocado, quando, nem por qual ferramenta.
                Log.Info($"dlss apply: {game.Path} {game.Version} -> {lib.Version} " +
                         $"(backup {(File.Exists(backup) ? "ok" : "AUSENTE")})");
            }
            catch (Exception ex)
            {
                Log.Warn($"dlss apply {game.Path}: {ex.Message}");
                notes.Add(L.T("Dlss_Failed", game.FileName, ex.Message));
            }
        }
        return new ApplyResult(updated, current, notes);
    }

    /// <summary>
    /// Upgrade Frame Generation by replacing `nvngx_dlssg.dll` AND the whole Streamline set in one
    /// go, from a folder that holds a matched build (the runtimes and the `sl.*` plugins shipped
    /// together by NVIDIA).
    ///
    /// Refuses unless every file the game has is present in the source and signed by NVIDIA. A
    /// partial swap here is worse than none: it is precisely the mismatch that crashes the game on
    /// launch, and it would look like the upgrade "just broke it".
    /// </summary>
    /// <param name="sourceDir">Folder holding the matched set, e.g. an extracted Streamline drop.</param>
    public static ApplyResult ApplyFrameGeneration(string installDir, string targetDir, string sourceDir,
                                                   IProgress<string>? progress = null)
    {
        if (AddonService.IsGameRunning(targetDir))
            throw new InvalidOperationException(L.T("Error_GameRunning"));
        if (!Directory.Exists(sourceDir))
            throw new InvalidOperationException(L.T("Dlss_Fg_NoSource", sourceDir));

        // O conjunto INTEIRO da origem, nao "o que o jogo ja tem".
        //
        // Trocar so os arquivos existentes era o defeito: nenhum jogo lancado traz
        // `sl.dlss_nr.dll`, o plugin de Neural Rendering do Streamline — ele so existe neste
        // pacote. Substituindo apenas o que ja estava la, o plugin que da acesso a feature nunca
        // chegava ao jogo, e o resultado era um conjunto atualizado que continuava sem saber fazer
        // neural. `sl.dlss.dll` e `sl.nis.dll` faltavam pelo mesmo motivo em varios titulos.
        var source = Directory.EnumerateFiles(sourceDir, "*.dll")
            .Where(f => IsSetMember(Path.GetFileName(f)))
            .ToList();
        if (source.Count == 0)
            throw new InvalidOperationException(L.T("Dlss_Fg_NoSource", sourceDir));

        // Verificacao ANTES de escrever qualquer coisa: ou tudo esta assinado pela NVIDIA, ou nao
        // se mexe em nada. Meia troca e pior que nenhuma.
        //
        // O runtime neural e a excecao, e so ele: no conjunto ele e passageiro, nao membro. Quem
        // o implanta de verdade e o NeuralUpliftService, a partir da biblioteca dele e com a regra
        // dele (assinatura OU hash fixado do build da comunidade). Em RTX 20/30/40 esse build e um
        // binario patcheado, sem assinatura, e o instalador o deixa ao lado do conjunto Streamline
        // do jogo — de onde AutoDiscoverStreamlineSet o levava para a biblioteca. Abortar por
        // causa dele fazia TODO Corrigir de TODO jogo falhar, para sempre, sem nada a consertar
        // no conjunto em si. Pular e registrar e o certo: o conjunto continua coerente sem ele.
        var recusados = new List<(string Path, string Why)>();
        foreach (var src in source)
        {
            if (IsGenuine(src, out var why)) continue;
            if (Path.GetFileName(src).Equals(NeuralUpliftService.RuntimeFile, StringComparison.OrdinalIgnoreCase))
            {
                Log.Warn($"dlss fg: {src} ignorado, nao acompanha o conjunto: {why}");
                recusados.Add((src, why));
                continue;
            }
            throw new InvalidOperationException(L.T("Dlss_NotNvidia", why));
        }
        source.RemoveAll(f => recusados.Any(r => r.Path == f));
        if (source.Count == 0)
        {
            // A pasta existe e tinha arquivo — so que tudo o que tinha foi recusado. Dizer "pasta
            // nao encontrada" aqui mandava a pessoa conferir um caminho que estava certo; o que
            // ela precisa saber e qual arquivo foi recusado e por que.
            var (caminho, motivo) = recusados[0];
            throw new InvalidOperationException(
                L.T("Dlss_Fg_AllRefused", sourceDir, Path.GetFileName(caminho), motivo));
        }

        // TODA pasta que tem um interposer, mais a do executavel.
        //
        // Pegar so o primeiro interposer era o defeito: um jogo Unreal carrega DOIS conjuntos —
        // um em Binaries\Win64 e outro em Engine\Plugins\...\Streamline\...\Win64 — e a busca
        // parava no primeiro, que costuma ser justamente o que ja estava bom. O conjunto
        // realmente quebrado ficava intocado, e o diagnostico continuava vermelho depois de
        // clicar em Corrigir, sem nada no log dizendo por que.
        var destinos = Directory
            .EnumerateFiles(installDir, "sl.interposer.dll", Walk)
            .Where(f => !f.EndsWith(BackupSuffix, StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetDirectoryName)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        // A pasta do executavel entra na lista, mas nao pelos mesmos arquivos: sao dois
        // carregadores diferentes. O JOGO carrega os plugins `sl.*` de onde vive o interposer; o
        // addon neural procura os runtimes NGX ao lado do executavel.
        //
        // Escrever o conjunto inteiro nos dois criava `sl.interposer.dll` e companhia numa pasta
        // que nunca teve conjunto nenhum — e como nao havia arquivo anterior, nao havia backup, e
        // `Restore` nunca vai poder desfazer. Medido: nove arquivos criados na raiz do The Witcher
        // 3, cujo conjunto de verdade vive em `bin\x64_dx12`.
        var exeDirOnlyNgx = !destinos.Contains(targetDir, StringComparer.OrdinalIgnoreCase);
        if (exeDirOnlyNgx) destinos.Add(targetDir);
        Log.Info($"dlss fg: {destinos.Count} destino(s): {string.Join(" | ", destinos)}"
                 + (exeDirOnlyNgx ? $" (em {targetDir}, so runtimes NGX)" : ""));

        int updated = 0;
        var notes = new List<string>();
        foreach (var dir in destinos)
        {
            // Na pasta do executavel que nao tem interposer, so os runtimes NGX — ver acima.
            var ngxOnly = exeDirOnlyNgx && dir.Equals(targetDir, StringComparison.OrdinalIgnoreCase);
            foreach (var src in source)
            {
                var nome = Path.GetFileName(src);
                if (ngxOnly && !Runtimes.ContainsKey(nome)) continue;
                var dest = Path.Combine(dir, nome);
                try
                {
                    // Ja identico: nada a fazer. Evita recopiar os 158 MB do runtime neural a
                    // cada clique. Tamanho E versao, nao so tamanho: duas builds do mesmo plugin
                    // podem ter o mesmo tamanho, e o efeito de pular ali seria deixar exatamente
                    // o arquivo divergente que se veio consertar.
                    if (File.Exists(dest) && new FileInfo(dest).Length == new FileInfo(src).Length
                        && ReadVersion(dest) == ReadVersion(src))
                        continue;

                    var before = File.Exists(dest) ? ReadVersion(dest) : null;
                    var backup = dest + BackupSuffix;
                    if (File.Exists(dest) && !File.Exists(backup)) File.Copy(dest, backup);
                    File.Copy(src, dest, overwrite: true);
                    updated++;
                    Log.Info($"dlss fg: {dest} {(before is null ? "(ausente)" : before.ToString())}"
                             + $" -> {ReadVersion(dest)}");
                }
                catch (Exception ex)
                {
                    Log.Warn($"dlss fg {dest}: {ex.Message}");
                    notes.Add(L.T("Dlss_Failed", Path.GetFileName(dest), ex.Message));
                }
            }

            // Os termos de licenca que a NVIDIA distribui com os binarios acompanham a copia.
            foreach (var lic in Directory.EnumerateFiles(sourceDir, "*.license.txt"))
            {
                try { File.Copy(lic, Path.Combine(dir, Path.GetFileName(lic)), overwrite: true); }
                catch (Exception ex) { Log.Warn($"dlss fg licenca: {ex.Message}"); }
            }
        }

        progress?.Report(L.T("Dlss_Fg_Done", updated));
        notes.Insert(0, L.T("Dlss_Fg_Done", updated));
        return new ApplyResult(updated, 0, notes);
    }

    /// <summary>Faz parte do conjunto que anda junto: os plugins do Streamline e os runtimes NGX
    /// que eles dirigem, incluindo o neural.</summary>
    private static bool IsSetMember(string fileName) =>
        StreamlineSet.Contains(fileName, StringComparer.OrdinalIgnoreCase)
        || Runtimes.ContainsKey(fileName)
        || fileName.Equals(NeuralUpliftService.RuntimeFile, StringComparison.OrdinalIgnoreCase);


    /// <summary>
    /// Procura na maquina uma pasta que contenha um conjunto Streamline COMPLETO e COERENTE — os
    /// `sl.*` todos do mesmo build, mais o `nvngx_dlssg.dll` que anda com eles — e guarda a pasta
    /// inteira na biblioteca.
    ///
    /// Coerencia e o requisito, nao "o mais novo": uma pasta com plugins de builds diferentes e
    /// justamente o defeito que estamos tentando consertar, e copiar isso para a biblioteca
    /// espalharia o problema em vez de curar.
    /// </summary>
    /// <returns>A versao guardada, ou null se nenhum conjunto completo foi encontrado.</returns>
    public static Version? AutoDiscoverStreamlineSet(IEnumerable<string> searchDirs,
                                                     IProgress<string>? progress = null)
    {
        foreach (var root in searchDirs.Where(Directory.Exists))
        {
            progress?.Report(L.T("Dlss_Searching", Path.GetFileName(root.TrimEnd('\\'))));
            IEnumerable<string> interposers;
            try { interposers = Directory.EnumerateFiles(root, "sl.interposer.dll", Walk).ToList(); }
            catch (Exception ex) { Log.Warn($"dlss set scan {root}: {ex.Message}"); continue; }

            foreach (var interposer in interposers)
            {
                var dir = Path.GetDirectoryName(interposer);
                if (dir is null) continue;

                var files = StreamlineSet
                    .Select(n => Path.Combine(dir, n))
                    .Where(File.Exists)
                    .ToList();
                if (files.Count < 4) continue;   // conjunto pela metade nao serve de referencia

                var versions = files.Select(ReadVersion).Where(v => v != null).Distinct().ToList();
                if (versions.Count != 1) continue;              // incoerente: e o defeito, nao a cura
                if (files.Any(f => !IsGenuine(f, out _))) continue;

                var fg = Path.Combine(dir, "nvngx_dlssg.dll");
                if (!File.Exists(fg) || !IsGenuine(fg, out _)) continue;

                try
                {
                    Directory.CreateDirectory(LibrarySetDir);
                    foreach (var f in files.Append(fg))
                        File.Copy(f, Path.Combine(LibrarySetDir, Path.GetFileName(f)), overwrite: true);
                    // o runtime neural e as licencas viajam com o conjunto quando existem
                    foreach (var extra in Directory.EnumerateFiles(dir))
                    {
                        var nome = Path.GetFileName(extra);
                        var neural = nome.Equals(NeuralUpliftService.RuntimeFile, StringComparison.OrdinalIgnoreCase);
                        if (!neural && !nome.EndsWith(".license.txt", StringComparison.OrdinalIgnoreCase)) continue;
                        // A mesma barra dos outros membros. O runtime neural que o instalador deixa
                        // ao lado do conjunto e, fora de Blackwell, o build da comunidade — sem
                        // assinatura, aceito la por hash fixado numa origem conhecida, que aqui
                        // nao existe: uma pasta de jogo nao tem origem. Copia-lo para a biblioteca
                        // envenenava o conjunto: ApplyFrameGeneration recusava a origem inteira e
                        // todo Corrigir passava a falhar. Ele nao faz falta aqui — o
                        // NeuralUpliftService o implanta da biblioteca propria.
                        if (neural && !IsGenuine(extra, out var why))
                        {
                            Log.Info($"dlss set: {nome} de {dir} nao copiado: {why}");
                            continue;
                        }
                        File.Copy(extra, Path.Combine(LibrarySetDir, nome), overwrite: true);
                    }
                    Log.Info($"dlss set: conjunto {versions[0]} guardado de {dir}");
                    return versions[0];
                }
                catch (Exception ex) { Log.Warn($"dlss set copy {dir}: {ex.Message}"); }
            }
        }
        return null;
    }

    // ---------- diagnostico ----------

    /// <param name="Severity">"erro" quebra o jogo; "aviso" merece olhar.</param>
    /// <param name="Kind">Que defeito e, e nao so quao grave. Os dois "erro" pedem respostas
    /// opostas: um conjunto INCOERENTE se conserta reescrevendo o conjunto; um arquivo NAO
    /// ASSINADO nao — trocar tudo por causa dele e uma troca de Frame Generation completa que
    /// ninguem pediu, e ela CRIA arquivo em pasta que nunca teve conjunto.</param>
    public record HealthIssue(string Severity, string Message, string Path, string Kind = KindOther);

    public const string KindIncoherentSet = "conjunto-incoerente";
    public const string KindNotSigned = "nao-assinado";
    public const string KindOther = "outro";

    /// <summary>
    /// Procura estados que QUEBRAM o jogo, sem tentar adivinhar o conserto.
    ///
    /// A distincao importa: nao da para saber, de fora, qual versao de DLSS um jogo aceita — so
    /// que ela e maior ou menor. O que DA para provar sem esse conhecimento e a INCONSISTENCIA
    /// interna: plugins do Streamline que deveriam vir do mesmo SDK e nao vieram, ou um arquivo
    /// que nao e da NVIDIA. Os dois sao defeito com certeza, venham de onde vierem — deste
    /// launcher, do DLSS Swapper, ou de uma troca feita a mao.
    /// </summary>
    public static IReadOnlyList<HealthIssue> CheckHealth(string installDir)
    {
        var issues = new List<HealthIssue>();
        var streamline = new List<(string Path, Version V)>();

        try
        {
            foreach (var f in Directory.EnumerateFiles(installDir, "*.dll", Walk))
            {
                var name = Path.GetFileName(f);
                var isNgx = Runtimes.ContainsKey(name);
                var isSl = StreamlineSet.Contains(name, StringComparer.OrdinalIgnoreCase);
                if (!isNgx && !isSl) continue;

                if (!IsGenuine(f, out var why))
                    issues.Add(new HealthIssue("erro", L.T("Dlss_Health_NotSigned", name, why), f, KindNotSigned));

                if (isSl && ReadVersion(f) is { } v) streamline.Add((f, v));
            }
        }
        catch (Exception ex) { Log.Warn($"dlss health {installDir}: {ex.Message}"); }

        // Plugins do Streamline de builds diferentes DENTRO DA MESMA PASTA: saem juntos do mesmo
        // SDK, entao versoes divergentes lado a lado significam conjunto trocado pela metade — a
        // forma classica de travar na abertura.
        //
        // Por pasta, e nao pelo jogo inteiro. Um Unreal carrega dois conjuntos, em Binaries\Win64
        // e em Engine\Plugins\...\Streamline; comparar os dois juntos produzia uma mensagem que
        // listava o MESMO nome de arquivo nas duas versoes — verdadeira e inutil, porque nao
        // dizia qual pasta consertar. O que quebra o jogo e a incoerencia interna de cada
        // conjunto: o interposer e os plugins que ele carrega tem que vir do mesmo build.
        foreach (var dir in streamline.GroupBy(s => Path.GetDirectoryName(s.Path) ?? "",
                                               StringComparer.OrdinalIgnoreCase))
        {
            var versions = dir.Select(s => s.V).Distinct().ToList();
            if (versions.Count <= 1) continue;
            var detalhe = string.Join(", ", dir
                .GroupBy(s => s.V)
                .Select(g => $"{g.Key} ({string.Join("/", g.Select(x => Path.GetFileName(x.Path)).Distinct())})"));
            issues.Add(new HealthIssue("erro",
                L.T("Dlss_Health_SlMismatch", detalhe) + $"\n{dir.Key}", dir.Key, KindIncoherentSet));
        }

        return issues;
    }

    /// <summary>
    /// Conserta o que <see cref="CheckHealth"/> encontrou, aplicando o conjunto Streamline COMPLETO
    /// da biblioteca — todos os `sl.*` mais o `nvngx_dlssg.dll`, do mesmo build.
    ///
    /// Nao "acerta a peca divergente": troca o conjunto inteiro, porque trocar peca solta e
    /// exatamente o que produziu o defeito. E nao volta para a versao antiga do jogo: o objetivo de
    /// quem chega aqui e ficar com o conjunto novo funcionando, nao desistir dele. Quem quiser o
    /// original tem <see cref="Restore"/>, que continua no botao separado.
    ///
    /// Sem um conjunto completo na biblioteca, cai para o backup do jogo; sem nenhum dos dois, nao
    /// conserta e diz o porque — inventar meia correcao aqui deixaria o jogo pior.
    /// </summary>
    public static ApplyResult Repair(string installDir, string targetDir, IProgress<string>? progress = null)
    {
        if (AddonService.IsGameRunning(targetDir))
            throw new InvalidOperationException(L.T("Error_GameRunning"));

        // 1. conjunto novo e coerente, que e o que a pessoa quer rodando
        if (Directory.Exists(LibrarySetDir) &&
            File.Exists(Path.Combine(LibrarySetDir, "sl.interposer.dll")))
        {
            progress?.Report(L.T("Dlss_Repair_Applying"));
            var r = ApplyFrameGeneration(installDir, targetDir, LibrarySetDir, progress);
            var restante = CheckHealth(installDir);
            if (restante.Count > 0)
                Log.Warn($"dlss repair: {restante.Count} problema(s) permanecem em {installDir}");
            return r;
        }

        // 2. sem referencia completa: devolve o do estudio, que ao menos e coerente
        var restored = Restore(installDir, targetDir);
        if (restored > 0)
        {
            progress?.Report(L.T("Dlss_Repair_Restored", restored));
            return new ApplyResult(restored, 0, [L.T("Dlss_Repair_Restored", restored)]);
        }

        throw new InvalidOperationException(L.T("Dlss_Repair_NoReference"));
    }

    /// <summary>Put the studio's builds back and retire the backup once the original is in place.
    ///
    /// The backup used to stay on disk after a restore, on the reasoning that it was the only
    /// copy of what the game shipped with. After a restore it is not: the original IS back. What
    /// keeping it did was leave <see cref="IsApplied"/> true forever — it is defined as "a runtime
    /// backup exists" — so the per-game button stayed in "restore" mode, the game kept showing in
    /// the swapped-games banner, and the newer runtime could never be applied again from the UI.
    /// <see cref="RestoreAll"/> already retired identical backups; this path now does the same,
    /// and only after confirming the copy landed.</summary>
    public static int Restore(string installDir, string targetDir)
    {
        if (AddonService.IsGameRunning(targetDir))
            throw new InvalidOperationException(L.T("Error_GameRunning"));

        var restored = 0;
        try
        {
            // Backups de RUNTIME, e so eles. Restaurar meio conjunto reproduziria o desencontro
            // de versao que a reversao existe para desfazer — dai nao ser "nvngx_dls*" — mas o
            // outro extremo tambem e errado: o instalador de DLSS 5 deixa backup do ADDON, e o
            // "*" varria isso junto. Reverter o runtime do jogo passava a reverter o addon para
            // um build antigo de brinde, sem ninguem ter pedido.
            foreach (var bak in RuntimeBackups(installDir).ToList())
            {
                var original = bak[..^BackupSuffix.Length];
                try
                {
                    File.Copy(bak, original, overwrite: true);
                    restored++;
                    Log.Info($"dlss restore: {original}");
                    RetireBackup(bak, original);
                }
                catch (Exception ex) { Log.Warn($"dlss restore {bak}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Log.Warn($"dlss restore scan {installDir}: {ex.Message}"); }
        return restored;
    }

    /// <summary>
    /// Apaga o backup DEPOIS de conferir que o original devolvido e identico a ele. Nunca antes:
    /// o backup e a unica copia do que o estudio distribuiu, e uma copia que falhou pela metade
    /// nao pode custar essa copia. Um backup que nao confere fica, e o log diz por que.
    /// </summary>
    private static void RetireBackup(string bak, string original)
    {
        try
        {
            if (File.Exists(original)
                && new FileInfo(original).Length == new FileInfo(bak).Length
                && ReadVersion(original) == ReadVersion(bak))
            {
                File.Delete(bak);
                return;
            }
            Log.Warn($"dlss restore: backup mantido, original nao confere: {bak}");
        }
        catch (Exception ex) { Log.Warn($"dlss restore backup {bak}: {ex.Message}"); }
    }

    /// <param name="Restored">Arquivos devolvidos ao original.</param>
    /// <param name="Locked">Arquivos que nao deu para devolver porque o jogo estava aberto.</param>
    /// <param name="Games">Jogos tocados.</param>
    public record SweepResult(int Restored, IReadOnlyList<string> Locked, IReadOnlyList<string> Games);

    /// <summary>
    /// Devolve TODOS os runtimes trocados, em todos os jogos conhecidos, de uma vez.
    ///
    /// Existe porque <see cref="Restore"/> sozinho e por jogo, e quem trocou seis jogos e viu um
    /// travar nao tem como saber quais outros ficaram alterados — a alteracao nao aparece em lugar
    /// nenhum ate o jogo abrir errado. Deixar isso a cargo do usuario e o que torna a ferramenta
    /// incompleta: ela sabe exatamente o que mudou e onde.
    ///
    /// Arquivo em uso nao e apagado nem silenciado: volta na lista, com o nome do jogo, para o
    /// usuario fechar e repetir.
    /// </summary>
    public static SweepResult RestoreAll(IEnumerable<string> gameDirs, IProgress<string>? progress = null)
    {
        var restored = 0;
        var locked = new List<string>();
        var games = new List<string>();

        foreach (var dir in gameDirs.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            IReadOnlyList<string> baks;
            // So backups de runtime — ver Restore. Aqui o estrago seria pior: este caminho APAGA
            // o backup depois de restaurar, entao varrer o backup do addon junto destruiria a
            // unica copia do build que foi substituido.
            try { baks = RuntimeBackups(dir).ToList(); }
            catch (Exception ex) { Log.Warn($"dlss sweep {dir}: {ex.Message}"); continue; }
            if (baks.Count == 0) continue;

            var nome = Path.GetFileName(dir.TrimEnd('\\', '/'));
            games.Add(nome);
            progress?.Report(L.T("Dlss_Sweep_Game", nome));

            foreach (var bak in baks)
            {
                var original = bak[..^BackupSuffix.Length];
                try
                {
                    // Backup identico ao arquivo atual: nada a devolver, so lixo a recolher.
                    // Deixa-lo faria o jogo aparecer como "alterado" para sempre.
                    if (File.Exists(original) &&
                        new FileInfo(original).Length == new FileInfo(bak).Length &&
                        ReadVersion(original) == ReadVersion(bak))
                    {
                        File.Delete(bak);
                        continue;
                    }
                    File.Copy(bak, original, overwrite: true);
                    File.Delete(bak);
                    restored++;
                    Log.Info($"dlss sweep restore: {original}");
                }
                catch (Exception ex)
                {
                    Log.Warn($"dlss sweep {bak}: {ex.Message}");
                    locked.Add($"{nome}: {Path.GetFileName(original)}");
                }
            }
        }
        return new SweepResult(restored, locked, games);
    }

    /// <summary>True when any runtime in the game is currently one of ours (a backup exists).</summary>
    /// <summary>Backups que este servico pode reverter: os dos runtimes NGX e do conjunto
    /// Streamline. Tudo mais que use o mesmo sufixo pertence a outro dono.</summary>
    private static IEnumerable<string> RuntimeBackups(string installDir) =>
        Directory.EnumerateFiles(installDir, "*" + BackupSuffix, Walk)
            .Where(f => IsSetMember(Path.GetFileName(f)[..^BackupSuffix.Length]));

    public static bool IsApplied(string installDir)
    {
        try
        {
            // Only a RUNTIME backup counts as "this game has a swapped DLSS runtime".
            //
            // It used to be any `.renodx-bak` at all, and the DLSS 5 installer makes several that
            // have nothing to do with a runtime swap — the addon it refreshes, the ReShade proxy
            // it replaces. Once installing the feature marked every game as "swapped", the banner
            // offering to restore them all was firing on the state the user had just asked for,
            // and its button would have undone the whole install.
            return Directory.EnumerateFiles(installDir, "*" + BackupSuffix, Walk)
                .Any(f => IsSetMember(Path.GetFileName(f)[..^BackupSuffix.Length]));
        }
        catch (Exception ex) { Log.Warn($"dlss isapplied {installDir}: {ex.Message}"); return false; }
    }
}
