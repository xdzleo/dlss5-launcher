using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.RegularExpressions;

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

    [GeneratedRegex(@"/downloads/ReShade_Setup_([\d.]+)_Addon\.exe")]
    private static partial Regex SetupLinkRegex();

    public static readonly string[] KnownProxyNames =
    {
        "dxgi.dll", "d3d12.dll", "d3d11.dll", "d3d10.dll", "d3d9.dll", "d3d8.dll",
        "opengl32.dll", "ddraw.dll", "dinput8.dll", "version.dll", "winmm.dll",
    };

    /// <summary>Ensure ReShade DLLs are staged locally; returns the staged version.</summary>
    public async Task<string> ProvisionAsync(IProgress<string>? progress = null)
    {
        // reuse newest staged version if present
        var root = Path.Combine(AppPaths.DataDir, "reshade");
        if (Directory.Exists(root))
        {
            var staged = Directory.GetDirectories(root)
                .Where(d => File.Exists(Path.Combine(d, "ReShade64.dll")))
                .Select(Path.GetFileName)
                .OrderByDescending(v => Version.TryParse(v, out var ver) ? ver : new Version(0, 0))
                .FirstOrDefault();
            if (staged != null) return staged!;
        }

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 RenoDXLauncher/1.0");
        http.DefaultRequestHeaders.Referrer = new Uri("https://reshade.me");

        string version = PinnedVersion;
        try
        {
            progress?.Report("Consultando versão do ReShade...");
            var html = await http.GetStringAsync("https://reshade.me");
            var m = SetupLinkRegex().Match(html);
            if (m.Success) version = m.Groups[1].Value;
        }
        catch (Exception ex) { Log.Warn($"reshade.me version probe: {ex.Message} — usando {PinnedVersion}"); }

        var stage = StageDir(version);
        if (File.Exists(Path.Combine(stage, "ReShade64.dll"))) return version;

        progress?.Report($"Baixando ReShade {version} (addon support)...");
        var url = $"https://reshade.me/downloads/ReShade_Setup_{version}_Addon.exe";
        byte[] exe;
        try
        {
            exe = await http.GetByteArrayAsync(url);
        }
        catch (Exception)
        {
            exe = await http.GetByteArrayAsync($"http://static.reshade.me/downloads/ReShade_Setup_{version}_Addon.exe");
        }
        if (exe.Length < 1024 || exe[0] != 'M' || exe[1] != 'Z')
            throw new InvalidOperationException("Download do ReShade inválido (não é um executável).");

        // The setup exe carries a plain ZIP appended after the PE image. Internal offsets are
        // relative to the ZIP's own start, so compute the base from the End-Of-Central-Directory
        // record instead of trusting the first local-header signature.
        long zipStart = FindZipBase(exe);
        if (zipStart < 0)
            throw new InvalidOperationException("Assinatura ZIP não encontrada no instalador do ReShade.");

        progress?.Report("Extraindo ReShade64.dll / ReShade32.dll...");
        Directory.CreateDirectory(stage);
        using var ms = new MemoryStream(exe, (int)zipStart, exe.Length - (int)zipStart);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        int extracted = 0;
        foreach (var entry in zip.Entries)
        {
            if (entry.Name is "ReShade64.dll" or "ReShade32.dll")
            {
                entry.ExtractToFile(Path.Combine(stage, entry.Name), overwrite: true);
                extracted++;
            }
        }
        if (extracted == 0)
            throw new InvalidOperationException("ReShade64.dll não encontrada dentro do instalador.");
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
            if (pe.Imports.Contains("d3d9.dll")) return "d3d9.dll";
            if (pe.Imports.Contains("opengl32.dll")
                && !pe.Imports.Any(i => i.StartsWith("d3d") || i == "dxgi.dll")) return "opengl32.dll";
        }
        // DX10/11/12 and unknown: dxgi.dll is the standard hook
        return "dxgi.dll";
    }

    public record DeployResult(bool Success, string Message, string? DllName = null);

    /// <summary>Copy the staged ReShade DLL into the game dir under the proxy name.</summary>
    public async Task<DeployResult> DeployAsync(string targetDir, string exePath, string? apiOverride,
        string? dllNameOverride, IProgress<string>? progress = null)
    {
        var pe = PeUtils.Inspect(exePath, readImports: false);
        bool is64 = pe?.Is64Bit ?? true;
        var version = await ProvisionAsync(progress);
        var source = Path.Combine(StageDir(version), is64 ? "ReShade64.dll" : "ReShade32.dll");
        if (!File.Exists(source))
            return new DeployResult(false, $"ReShade{(is64 ? 64 : 32)}.dll não disponível no cache local.");

        var dllName = PickDllName(exePath, apiOverride, dllNameOverride);
        var target = Path.Combine(targetDir, dllName);

        if (File.Exists(target))
        {
            var existing = PeUtils.Inspect(target, readImports: false);
            if (existing?.ProductName is { } product && !product.Contains("ReShade", StringComparison.OrdinalIgnoreCase))
                return new DeployResult(false,
                    $"Já existe um {dllName} de outro mod ({product}) nessa pasta. Não vou sobrescrever — remova/renomeie manualmente ou use outro nome de proxy.");
        }

        progress?.Report($"Instalando ReShade como {dllName}...");
        File.Copy(source, target, overwrite: true);

        // minimal ReShade.ini (RHI template): disable built-in addons that cost perf, esp. DX12
        var ini = new IniFile(Path.Combine(targetDir, "ReShade.ini"));
        if (ini.Get("ADDON", "DisabledAddons", ignoreCase: true) is null)
            ini.Set("ADDON", "DisabledAddons", "Generic Depth,Effect Runtime Sync");
        if (ini.Get("OVERLAY", "TutorialProgress", ignoreCase: true) is null)
            ini.Set("OVERLAY", "TutorialProgress", "4");
        ini.Save();

        return new DeployResult(true, $"ReShade {version} instalado como {dllName}.", dllName);
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
