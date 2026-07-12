using System.Diagnostics;
using System.IO;
using System.Net.Http;
using RenoDXLauncher.Models;

namespace RenoDXLauncher.Services;

/// <summary>
/// Manages the renodx-*.addonNN file inside a game's deploy dir:
/// install (download from the snapshot URL), enable/disable (extension rename —
/// ReShade only loads *.addon/*.addon32/*.addon64, so renaming to .disabled is a
/// clean, verified soft-off), update, remove, and state detection.
/// </summary>
public class AddonService
{
    public const string DisabledSuffix = ".disabled";

    /// <summary>Scan a deploy dir for ReShade + renodx addon files.</summary>
    public static ModState GetState(string targetDir, string? exePath)
    {
        var state = new ModState { TargetDir = targetDir, ExePath = exePath };
        try
        {
            if (!Directory.Exists(targetDir)) return state;
            var (dll, version) = ReShadeService.Detect(targetDir);
            state.ReShadePresent = dll != null;
            state.ReShadeDllName = dll;
            state.ReShadeVersion = version;

            var addons = Directory.GetFiles(targetDir, "renodx-*.addon*", SearchOption.TopDirectoryOnly)
                .Where(f => f.EndsWith(".addon64", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".addon32", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".addon64" + DisabledSuffix, StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".addon32" + DisabledSuffix, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var enabled = addons.FirstOrDefault(f => !f.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase));
            state.AddonPath = enabled ?? addons.FirstOrDefault();
            state.AddonEnabled = enabled != null;
        }
        catch (Exception ex) { Log.Warn($"addon state {targetDir}: {ex.Message}"); }
        return state;
    }

    public static async Task<string> DownloadAddonAsync(CatalogEntry entry, string targetDir,
        IProgress<string>? progress = null)
    {
        if (entry.DownloadUrl is null)
            throw new InvalidOperationException("Este mod não tem download direto (só página no Nexus).");
        var fileName = Path.GetFileName(new Uri(entry.DownloadUrl).LocalPath);
        progress?.Report($"Baixando {fileName}...");

        Directory.CreateDirectory(AppPaths.DownloadsDir);
        var cached = Path.Combine(AppPaths.DownloadsDir, fileName);
        using (var http = new HttpClient())
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("RenoDXLauncher/1.0");
            var bytes = await http.GetByteArrayAsync(entry.DownloadUrl);
            if (bytes.Length < 1024)
                throw new InvalidOperationException($"Download de {fileName} veio vazio/corrompido.");
            await File.WriteAllBytesAsync(cached, bytes);
        }

        // exactly one renodx addon per deploy dir: remove other slugs' files first
        foreach (var other in Directory.GetFiles(targetDir, "renodx-*.addon*"))
        {
            if (!Path.GetFileName(other).StartsWith(Path.GetFileNameWithoutExtension(fileName),
                    StringComparison.OrdinalIgnoreCase))
            {
                Log.Info($"removendo addon conflitante {other}");
                File.Delete(other);
            }
        }

        var target = Path.Combine(targetDir, fileName);
        // if a disabled copy exists, replace it (install implies enable)
        var disabled = target + DisabledSuffix;
        if (File.Exists(disabled)) File.Delete(disabled);
        File.Copy(cached, target, overwrite: true);
        progress?.Report($"{fileName} instalado.");
        return target;
    }

    /// <summary>Enable/disable by renaming the extension. Returns the new path.</summary>
    public static string SetEnabled(ModState state, bool enable)
    {
        if (state.AddonPath is null) throw new InvalidOperationException("Nenhum addon instalado.");
        if (IsGameRunning(state.TargetDir))
            throw new InvalidOperationException("O jogo está aberto — feche-o antes de ativar/desativar o mod.");
        var path = state.AddonPath;
        if (enable && path.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase))
        {
            var target = path[..^DisabledSuffix.Length];
            if (File.Exists(target)) File.Delete(target);
            File.Move(path, target);
            return target;
        }
        if (!enable && !path.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase))
        {
            var target = path + DisabledSuffix;
            if (File.Exists(target)) File.Delete(target);
            File.Move(path, target);
            return target;
        }
        return path;
    }

    public static void Remove(ModState state, bool alsoReShade)
    {
        if (IsGameRunning(state.TargetDir))
            throw new InvalidOperationException("O jogo está aberto — feche-o antes de remover o mod.");
        if (state.AddonPath != null && File.Exists(state.AddonPath))
            File.Delete(state.AddonPath);
        if (alsoReShade && state.ReShadeDllName != null)
        {
            var dllPath = Path.Combine(state.TargetDir, state.ReShadeDllName);
            var other = Directory.GetFiles(state.TargetDir, "*.addon*").Length;
            if (other == 0 && File.Exists(dllPath))
            {
                var pe = PeUtils.Inspect(dllPath, readImports: false);
                if (pe?.ProductName?.Contains("ReShade", StringComparison.OrdinalIgnoreCase) == true)
                    File.Delete(dllPath);
            }
        }
    }

    /// <summary>True if any running process' main module lives under the deploy dir.</summary>
    public static bool IsGameRunning(string targetDir)
    {
        var prefix = Path.GetFullPath(targetDir).TrimEnd('\\') + "\\";
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var path = p.MainModule?.FileName;
                if (path != null && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch { /* access denied on system processes — irrelevant */ }
            finally { p.Dispose(); }
        }
        return false;
    }
}
