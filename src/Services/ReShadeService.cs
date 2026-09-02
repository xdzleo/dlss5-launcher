using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.RegularExpressions;
using RenoDXLauncher.Localization;

namespace RenoDXLauncher.Services;

/// <summary>
/// Provisions ReShade (addon-support build) without running its installer:
/// downloads ReShade_Setup_X.Y.Z_Addon.exe from reshade.me (Referer header required),
/// finds the ZIP archive appended to the exe (first PK\x03\x04 signature) and extracts
/// ReShade64.dll / ReShade32.dll. Deploys the DLL into a game dir under the API-specific
/// proxy name (dxgi.dll by default).
/// </summary>
public partial class ReShadeService
{
    public const string PinnedVersion = "6.7.3";
    private static string StageDir(string version) => Path.Combine(AppPaths.DataDir, "reshade", version);

    /// <summary>
    /// SHA-256 do certificado que assina os instaladores do ReShade
    /// (CN=ReShade, E=info@reshade.me — auto-assinado, valido de 2019 ate 2039).
    ///
    /// Fixamos o CERTIFICADO e nao o hash do arquivo: o hash muda a cada versao do ReShade
    /// e viraria manutencao eterna; a identidade de quem assina nao muda.
    ///
    /// Isso funciona porque o ZIP que o instalador carrega anexado fica ANTES da tabela de
    /// certificado do PE, e o Authenticode faz digest de tudo menos da propria tabela —
    /// conferido na pratica: alterar um unico byte dentro do ZIP muda o status de
    /// "raiz nao confiavel" para HashMismatch. Ou seja, validar a assinatura do setup.exe
    /// prova tambem a integridade das DLLs extraidas de dentro dele.
    /// </summary>
    public const string ReShadeCertSha256 = "445802BCB04E18E4EE6AADC00FB79AC39A17CB5F57B2B34DD69FED9383395790";

    private static readonly string ThisVersion =
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0";

    [GeneratedRegex(@"/downloads/ReShade_Setup_([\d.]+)_Addon\.exe")]
    private static partial Regex SetupLinkRegex();

    public static readonly string[] KnownProxyNames =
    {
        "dxgi.dll", "d3d12.dll", "d3d11.dll", "d3d10.dll", "d3d9.dll", "d3d8.dll",
        "opengl32.dll", "ddraw.dll", "dinput8.dll", "version.dll", "winmm.dll",
        // ReShade nem sempre ocupa o slot de proxy. Quando outro mod ja o ocupa — o OptiScaler
        // e o caso comum — ele fica como ReShade64.dll e e carregado em cadeia por esse mod.
        // Sem este nome, uma instalacao perfeitamente funcional era lida como "sem ReShade", e o
        // launcher oferecia instalar por cima do proxy alheio.
        "ReShade64.dll", "ReShade32.dll",
    };

    /// <summary>Ensure ReShade DLLs are staged locally; returns the staged version.</summary>
    public async Task<string> ProvisionAsync(IProgress<string>? progress = null)
    {
        // reuse newest fully-staged version (both DLLs — a partial stage must not survive)
        var root = Path.Combine(AppPaths.DataDir, "reshade");
        if (Directory.Exists(root))
        {
            var staged = Directory.GetDirectories(root)
                .Where(d => File.Exists(Path.Combine(d, "ReShade64.dll"))
                         && File.Exists(Path.Combine(d, "ReShade32.dll")))
                .Select(Path.GetFileName)
                .OrderByDescending(v => Version.TryParse(v, out var ver) ? ver : new Version(0, 0))
                .FirstOrDefault();
            if (staged != null) return staged!;
        }

        using var http = new HttpClient();
        // User-Agent proprio, e nao "Mozilla/5.0 ...": se passar por navegador e browser
        // impersonation, um sinal que analista de antivirus pontua, e nao compra nada —
        // conferido que o reshade.me responde 200 ao download com este UA.
        // O Referer, ao contrario, e exigido pelo servidor do reshade.me (protecao contra
        // hotlink); sem ele o download e recusado. Fica, com o porque escrito aqui para
        // ninguem ler como evasao.
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"RenoDXLauncher/{ThisVersion} (+https://github.com/xdzleo/dlss5-launcher)");
        http.DefaultRequestHeaders.Referrer = new Uri("https://reshade.me");

        string version = PinnedVersion;
        try
        {
            progress?.Report(L.T("Install_ReShade_CheckingVersion"));
            var html = await http.GetStringAsync("https://reshade.me");
            var m = SetupLinkRegex().Match(html);
            if (m.Success) version = m.Groups[1].Value;
        }
        catch (Exception ex) { Log.Warn($"reshade.me version probe: {ex.Message} — usando {PinnedVersion}"); }

        var stage = StageDir(version);
        if (File.Exists(Path.Combine(stage, "ReShade64.dll"))
            && File.Exists(Path.Combine(stage, "ReShade32.dll"))) return version;

        progress?.Report(L.T("Install_ReShade_Downloading", version));
        var url = $"https://reshade.me/downloads/ReShade_Setup_{version}_Addon.exe";
        byte[] exe;
        try
        {
            exe = await http.GetByteArrayAsync(url);
        }
        catch (Exception)
        {
            exe = await http.GetByteArrayAsync($"https://static.reshade.me/downloads/ReShade_Setup_{version}_Addon.exe");
        }
        if (exe.Length < 1024 || exe[0] != 'M' || exe[1] != 'Z')
            throw new InvalidOperationException(L.T("Error_ReShade_NotExecutable"));

        // Prova de origem ANTES de extrair qualquer coisa. Sem esta etapa, o que o launcher
        // faz e "baixa um executavel da internet e usa o binario de dentro" — a descricao
        // literal de um dropper. Com ela, e "instala um artefato assinado pelo autor do
        // ReShade", que e verificavel por quem estiver do outro lado de uma disputa de
        // falso-positivo. A checagem antiga (ProductName do PE) nao servia para isso:
        // ProductName e campo de recurso editavel, qualquer um escreve "ReShade" nele.
        // WinVerifyTrust precisa de caminho em disco, e o arquivo vai para a area do
        // proprio app — nunca %TEMP%, que e onde regra de "binario suspeito" mora.
        Directory.CreateDirectory(AppPaths.CacheDir);
        var setupPath = Path.Combine(AppPaths.CacheDir, $"ReShade_Setup_{version}_Addon.exe");
        await File.WriteAllBytesAsync(setupPath, exe);
        try
        {
            progress?.Report(L.T("Install_ReShade_VerifyingSignature"));
            if (!Authenticode.IsSignedBy(setupPath, ReShadeCertSha256, out var detail))
                throw new InvalidOperationException(L.T("Error_ReShade_Signature", detail));
            Log.Info($"ReShade {version}: assinatura conferida — {detail}");
        }
        finally
        {
            try { File.Delete(setupPath); } catch { }
        }

        // The setup exe carries a plain ZIP appended after the PE image. Internal offsets are
        // relative to the ZIP's own start, so compute the base from the End-Of-Central-Directory
        // record instead of trusting the first local-header signature.
        long zipStart = FindZipBase(exe);
        if (zipStart < 0)
            throw new InvalidOperationException(L.T("Error_ReShade_ArchiveNotFound"));

        progress?.Report(L.T("Install_ReShade_Extracting"));
        // extract to a temp dir first so a failed run can never look like a valid stage
        var tempStage = stage + ".tmp";
        if (Directory.Exists(tempStage)) Directory.Delete(tempStage, recursive: true);
        Directory.CreateDirectory(tempStage);
        using (var ms = new MemoryStream(exe, (int)zipStart, exe.Length - (int)zipStart))
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
        {
            foreach (var entry in zip.Entries)
                if (entry.Name is "ReShade64.dll" or "ReShade32.dll")
                    entry.ExtractToFile(Path.Combine(tempStage, entry.Name), overwrite: true);
        }
        foreach (var dll in new[] { "ReShade64.dll", "ReShade32.dll" })
        {
            var path = Path.Combine(tempStage, dll);
            var product = File.Exists(path)
                ? System.Diagnostics.FileVersionInfo.GetVersionInfo(path).ProductName : null;
            if (product?.Contains("ReShade", StringComparison.OrdinalIgnoreCase) != true)
                throw new InvalidOperationException(L.T("Error_ReShade_DllNotReShade", dll));
        }
        if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        Directory.Move(tempStage, stage);
        Log.Info($"ReShade {version} extraído para {stage}");
        return version;
    }

    /// <summary>Base offset of a ZIP appended to a file: locate the EOCD record
    /// (PK\x05\x06, within the last 64 KB), then base = EOCD - cdSize - cdOffset.</summary>
    private static long FindZipBase(byte[] data)
    {
        int floor = Math.Max(0, data.Length - 0x10000 - 22);
        for (int i = data.Length - 22; i >= floor; i--)
        {
            if (data[i] != 0x50 || data[i + 1] != 0x4B || data[i + 2] != 0x05 || data[i + 3] != 0x06) continue;
            uint cdSize = BitConverter.ToUInt32(data, i + 12);
            uint cdOffset = BitConverter.ToUInt32(data, i + 16);
            long zipBase = (long)i - cdSize - cdOffset;
            if (zipBase >= 0 && zipBase + 4 <= data.Length
                && data[zipBase] == 0x50 && data[zipBase + 1] == 0x4B)
                return zipBase;
        }
        return -1;
    }

    /// <summary>Pick the proxy DLL file name for a game exe (imports → API), honoring overrides.</summary>
    public static string PickDllName(string exePath, string? apiOverride, string? dllNameOverride)
    {
        if (dllNameOverride != null) return dllNameOverride;
        if (apiOverride != null)
        {
            var api = apiOverride.ToUpperInvariant();
            if (api.Contains("DX12") || api.Contains("DX11") || api.Contains("DX10")) return "dxgi.dll";
            if (api.Contains("DX9")) return "d3d9.dll";
            if (api.Contains("GL")) return "opengl32.dll";
        }
        var pe = PeUtils.Inspect(exePath);
        if (pe != null)
        {
            // "Importa d3d9" so significa D3D9 quando NAO ha uma API moderna junto. Uma build de
            // shipping da Unreal linka todas as RHI de uma vez — d3d9, d3d11, d3d12, dxgi e
            // opengl32 aparecem juntas num jogo que renderiza em D3D12 — e sem esta guarda o
            // proxy saia como d3d9.dll num jogo que nunca usa D3D9. O ramo do OpenGL logo abaixo
            // ja tinha exatamente esta guarda; faltava a mesma aqui.
            var modern = pe.Imports.Any(i => i.Equals("dxgi.dll", StringComparison.OrdinalIgnoreCase)
                                             || i.StartsWith("d3d1", StringComparison.OrdinalIgnoreCase));

            // A tabela de importacao ainda mente por omissao: o Just Cause 2 IMPORTA d3d9.dll e
            // renderiza em Direct3D 10, carregado por LoadLibrary. Sem olhar as strings do
            // binario, o proxy sai como d3d9.dll num jogo que nunca cria um device D3D9 — e o
            // ReShade fica na pasta sem enganchar coisa alguma.
            if (!modern)
                modern = MencionaApiModerna(exePath);

            if (pe.Imports.Contains("d3d9.dll") && !modern) return "d3d9.dll";
            if (pe.Imports.Contains("opengl32.dll")
                && !pe.Imports.Any(i => i.StartsWith("d3d") || i == "dxgi.dll")) return "opengl32.dll";
        }
        // DX10/11/12 and unknown: dxgi.dll is the standard hook
        return "dxgi.dll";
    }

    /// <summary>
    /// O binario menciona uma API grafica moderna, ainda que nao a importe?
    ///
    /// Um jogo que resolve a API por LoadLibrary nao aparece em import table nenhuma, mas o nome
    /// do modulo tem de existir em algum lugar do arquivo para ser passado adiante. Procurar a
    /// string decide os casos que a tabela sozinha erra.
    /// </summary>
    private static bool MencionaApiModerna(string exePath)
    {
        foreach (var api in new[] { "d3d10.dll", "d3d10_1.dll", "d3d11.dll", "d3d12.dll" })
            if (ContemTexto(exePath, api)) return true;
        return false;
    }

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
        catch (Exception ex) { Log.Warn($"reshade api probe {path}: {ex.Message}"); }
        return false;
    }

    public record DeployResult(bool Success, string Message, string? DllName = null);

    /// <summary>
    /// Poe um ReShade de 64 bits numa pasta sob o nome pedido, independente do jogo.
    ///
    /// Existe para o processo auxiliar do Feeder em jogos de 32 bits: NGX e o addon neural sao
    /// 64-bit-only, entao um jogo 32-bit ganha um host separado, de verdade 64 bits, com o seu
    /// proprio ReShade. Ali o bitness nao vem do executavel do jogo — vem do host.
    /// </summary>
    public async Task<DeployResult> Deploy64BitAsync(string targetDir, string dllName,
                                                     IProgress<string>? progress = null)
    {
        var version = await ProvisionAsync(progress);
        var source = Path.Combine(StageDir(version), "ReShade64.dll");
        if (!File.Exists(source))
            return new DeployResult(false, L.T("Error_ReShade_DllMissing", "ReShade64.dll"));

        Directory.CreateDirectory(targetDir);
        File.Copy(source, Path.Combine(targetDir, dllName), overwrite: true);
        return new DeployResult(true, "", dllName);
    }

    /// <summary>
    /// Copia o ReShade cru para uma pasta, no bitness pedido e sob o nome pedido.
    ///
    /// Diferente do Deploy64BitAsync, que fixa 64 bits: aqui quem decide e o chamador. Serve a
    /// camada Vulkan, que precisa do ReShade fora do slot de proxy — em Vulkan o nome do arquivo
    /// nao significa nada, quem carrega e o loader pelo manifesto.
    /// </summary>
    public async Task<DeployResult> DeployRawAsync(string targetDir, string dllName, bool is64,
                                                   IProgress<string>? progress = null)
    {
        var version = await ProvisionAsync(progress);
        var source = Path.Combine(StageDir(version), is64 ? "ReShade64.dll" : "ReShade32.dll");
        if (!File.Exists(source))
            return new DeployResult(false, L.T("Error_ReShade_DllMissing", Path.GetFileName(source)));

        Directory.CreateDirectory(targetDir);
        File.Copy(source, Path.Combine(targetDir, dllName), overwrite: true);
        return new DeployResult(true, "", dllName);
    }

    /// <summary>Copy the staged ReShade DLL into the game dir under the proxy name.</summary>
    public async Task<DeployResult> DeployAsync(string targetDir, string exePath, string? apiOverride,
        string? dllNameOverride, IProgress<string>? progress = null)
    {
        if (AddonService.IsGameRunning(targetDir))
            return new DeployResult(false, L.T("Error_GameRunning"));
        var pe = PeUtils.Inspect(exePath, readImports: false);
        bool is64 = pe?.Is64Bit ?? true;
        var version = await ProvisionAsync(progress);
        var source = Path.Combine(StageDir(version), is64 ? "ReShade64.dll" : "ReShade32.dll");
        if (!File.Exists(source))
            return new DeployResult(false, L.T("Error_ReShade_DllMissing", Path.GetFileName(source)));

        var dllName = PickDllName(exePath, apiOverride, dllNameOverride);
        var target = Path.Combine(targetDir, dllName);

        // Ja ha um ReShade CARREGADO nesta pasta por outro caminho?
        //
        // Quando outro mod ocupa o slot de proxy — OptiScaler e o caso comum — o ReShade fica
        // como ReShade64.dll e e carregado em cadeia por ele. Isso e uma instalacao valida e
        // funcionando, com addons rodando em cima. Nao ha nada a depositar.
        //
        // Sem isto, instalar qualquer mod nessa pasta esbarrava no proxy alheio e abortava com
        // "remova o dxgi.dll antes de instalar" — mandando o usuario quebrar um mod que funciona
        // para resolver um conflito que nao existe.
        var (jaTem, versaoLa) = Detect(targetDir);
        if (jaTem is not null && !jaTem.Equals(dllName, StringComparison.OrdinalIgnoreCase)
            && File.Exists(target))
        {
            progress?.Report(L.T("Install_ReShade_Chained", jaTem));
            return new DeployResult(true, L.T("Install_ReShade_Chained", jaTem), jaTem);
        }

        if (File.Exists(target))
        {
            // fail-safe: only overwrite a file POSITIVELY identified as ReShade. A DLL with no
            // version info (dxvk builds, wrappers, ASI loaders) must never be clobbered.
            var existing = PeUtils.Inspect(target, readImports: false);
            var product = existing?.ProductName;
            if (product?.Contains("ReShade", StringComparison.OrdinalIgnoreCase) != true)
                return new DeployResult(false,
                    L.T("Error_ReShade_ProxyConflict", dllName, product ?? L.T("Common_Unidentified")));

            // Um ReShade que ja estava aqui pode ser do USUARIO — instalado a mao, na versao que
            // ele escolheu — e sobrescreve-lo era perde-lo: a desinstalacao apaga o proxy e nao
            // havia o que devolver. A copia guardada e o que a remocao restaura (AddonService).
            //
            // Ela so nao e feita quando o que esta la e, byte a byte, um ReShade que este proprio
            // launcher ja preparou — o desta versao ou o de uma anterior, que fica na biblioteca.
            // Uma copia do nosso proprio arquivo nao e o estado de antes de nos, e devolve-la na
            // desinstalacao seria reinstalar o ReShade no ato de tira-lo.
            var backup = target + ".renodx-bak";
            if (!File.Exists(backup) && !EhReShadeDaBiblioteca(target))
            {
                File.Copy(target, backup);
                Log.Info($"reshade: {dllName} de outra origem guardado como {Path.GetFileName(backup)}");
            }
        }

        progress?.Report(L.T("Install_ReShade_Deploying", dllName));
        File.Copy(source, target, overwrite: true);

        // minimal ReShade.ini (RHI template): disable built-in addons that cost perf, esp. DX12
        var ini = new IniFile(Path.Combine(targetDir, "ReShade.ini"));
        if (ini.Get("ADDON", "DisabledAddons", ignoreCase: true) is null)
            ini.Set("ADDON", "DisabledAddons", "Generic Depth,Effect Runtime Sync");
        if (ini.Get("OVERLAY", "TutorialProgress", ignoreCase: true) is null)
            ini.Set("OVERLAY", "TutorialProgress", "4");
        ini.Save();

        return new DeployResult(true, L.T("Install_ReShade_Done", version, dllName), dllName);
    }

    /// <summary>
    /// Este arquivo e, byte a byte, um ReShade que este launcher ja preparou em alguma versao?
    ///
    /// A biblioteca guarda uma pasta por versao, e nenhuma e apagada ao subir de versao — entao
    /// "identico a qualquer uma delas" e a assinatura de um ReShade que fomos nos que pusemos, sem
    /// depender de marca que o build anterior nao escrevia.
    /// </summary>
    private static bool EhReShadeDaBiblioteca(string caminho)
    {
        try
        {
            var root = Path.Combine(AppPaths.DataDir, "reshade");
            if (!Directory.Exists(root)) return false;
            foreach (var stage in Directory.EnumerateDirectories(root))
                foreach (var dll in new[] { "ReShade64.dll", "ReShade32.dll" })
                    if (Iguais(caminho, Path.Combine(stage, dll))) return true;
        }
        catch (Exception ex) { Log.Warn($"reshade library compare {caminho}: {ex.Message}"); }
        return false;
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

    /// <summary>Detect a ReShade proxy DLL already present in a dir.</summary>
    public static (string? dllName, string? version) Detect(string targetDir)
    {
        foreach (var name in KnownProxyNames)
        {
            var path = Path.Combine(targetDir, name);
            if (!File.Exists(path)) continue;
            try
            {
                var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                if (info.ProductName?.Contains("ReShade", StringComparison.OrdinalIgnoreCase) == true)
                    return (name, info.FileVersion);
            }
            catch { }
        }
        return (null, null);
    }
}
