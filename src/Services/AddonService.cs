using System.Diagnostics;
using System.IO;
using System.Net.Http;
using RenoDXLauncher.Localization;
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

    /// <summary>
    /// Is a newer build of this addon available upstream? RenoDX mods update continuously and
    /// the snapshot URLs are stable, so we compare the installed file's size + last-modified
    /// against the server's HEAD response (no full download, no hashing the whole catalog).
    /// Returns null when it cannot be determined (offline, no direct URL, etc.).
    /// </summary>
    public static async Task<bool?> IsUpdateAvailableAsync(CatalogEntry entry, ModState state)
    {
        try
        {
            if (entry.DownloadUrl is null || state.AddonPath is null || !File.Exists(state.AddonPath))
                return null;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("RenoDXLauncher/1.0");
            using var req = new HttpRequestMessage(HttpMethod.Head, entry.DownloadUrl);
            using var resp = await http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;

            var remoteEtag = resp.Headers.ETag?.Tag;
            var remoteLen = resp.Content.Headers.ContentLength;
            var remoteMod = resp.Content.Headers.LastModified?.UtcDateTime;

            // preferred: compare against the ETag of the exact build we installed
            var record = InstalledModRegistry.Get(state.AddonPath);
            if (record?.ETag is { Length: > 0 } localEtag && remoteEtag is { Length: > 0 })
                return !string.Equals(localEtag, remoteEtag, StringComparison.Ordinal);

            // fallback for addons installed before this tracking existed (or by hand)
            var local = new FileInfo(state.AddonPath);
            if (remoteLen is > 0 && remoteLen != local.Length) return true;
            // slack of a minute: our copy's mtime is the download time, not the build time
            if (remoteMod is { } rm && rm > local.LastWriteTimeUtc.AddMinutes(1)) return true;

            return remoteLen is > 0 || remoteMod is not null ? false : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"update check {entry.Slug}: {ex.Message}");
            return null;
        }
    }

    public static async Task<string> DownloadAddonAsync(CatalogEntry entry, string targetDir,
        IProgress<string>? progress = null)
    {
        if (entry.DownloadUrl is null)
            throw new InvalidOperationException(L.T("Error_Mod_NoDirectDownload"));
        if (IsGameRunning(targetDir))
            throw new InvalidOperationException(L.T("Error_GameRunning"));
        var fileName = Path.GetFileName(new Uri(entry.DownloadUrl).LocalPath);
        progress?.Report(L.T("Install_Mod_Downloading", fileName));

        Directory.CreateDirectory(AppPaths.DownloadsDir);
        var cached = Path.Combine(AppPaths.DownloadsDir, fileName);
        string? etag = null;
        long size;
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("RenoDXLauncher/1.0");
            using var resp = await http.GetAsync(entry.DownloadUrl);
            resp.EnsureSuccessStatusCode();
            etag = resp.Headers.ETag?.Tag;           // build identity, for future update checks
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            // addons are PE DLLs — reject HTML error pages and truncated downloads
            if (bytes.Length < 4096 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
                throw new InvalidOperationException(L.T("Error_Mod_DownloadCorrupt", fileName));
            await File.WriteAllBytesAsync(cached, bytes);
            size = bytes.LongLength;
        }

        // exactly one renodx addon per deploy dir: remove every other addon file first.
        // Match on "<base>." so renodx-hades doesn't spare renodx-hades2, and same-slug
        // files of the other bitness are cleaned too.
        var keep = new[] { fileName, fileName + DisabledSuffix };
        foreach (var other in Directory.GetFiles(targetDir, "renodx-*.addon*"))
        {
            if (!keep.Contains(Path.GetFileName(other), StringComparer.OrdinalIgnoreCase))
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
        InstalledModRegistry.Set(target, new InstalledModRecord
        {
            Slug = entry.Slug,
            FileName = fileName,
            Url = entry.DownloadUrl,
            ETag = etag,
            Size = size,
            DownloadedUtc = DateTime.UtcNow,
        });
        progress?.Report(L.T("Install_Mod_Done", fileName));
        return target;
    }

    /// <summary>Enable/disable by renaming the extension. Returns the new path.</summary>
    public static string SetEnabled(ModState state, bool enable)
    {
        if (state.AddonPath is null) throw new InvalidOperationException(L.T("Error_Mod_NotInstalled"));
        if (IsGameRunning(state.TargetDir))
            throw new InvalidOperationException(L.T("Error_GameRunning"));
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

    /// <summary>Undo a ReShade deploy after a failed install: removes the proxy DLL we just
    /// copied (only if it really is ReShade and no addon is left behind), so a half-finished
    /// install never leaves an unrequested DLL injected into the user's game.</summary>
    public static void RollbackReShade(string targetDir, string dllName)
    {
        var dllPath = Path.Combine(targetDir, dllName);
        if (!File.Exists(dllPath)) return;
        if (Directory.GetFiles(targetDir, "*.addon*").Length > 0) return; // outro addon usa o ReShade
        var pe = PeUtils.Inspect(dllPath, readImports: false);
        if (pe?.ProductName?.Contains("ReShade", StringComparison.OrdinalIgnoreCase) == true)
        {
            File.Delete(dllPath);
            Log.Info($"rollback: {dllPath} removido após falha de instalação");
        }
    }

    public static void Remove(ModState state, bool alsoReShade)
    {
        if (IsGameRunning(state.TargetDir))
            throw new InvalidOperationException(L.T("Error_GameRunning"));
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

    /// <summary>True if any running process' main module lives under the deploy dir.
    /// Elevated processes hide their module path, so process NAMES are also compared
    /// against the exe files present in the dir.</summary>
    public static bool IsGameRunning(string targetDir)
    {
        var prefix = Path.GetFullPath(targetDir).TrimEnd('\\') + "\\";
        HashSet<string> exeNames;
        try
        {
            exeNames = Directory.GetFiles(targetDir, "*.exe", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => n != null)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)!;
        }
        catch { exeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase); }

        foreach (var p in Process.GetProcesses())
        {
            bool pathKnown = false;
            try
            {
                var path = p.MainModule?.FileName;
                if (path != null)
                {
                    pathKnown = true;
                    // caminho real disponível: veredito definitivo, sem falso positivo por nome
                    if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch { /* elevado/sistema: sem caminho, cai no fallback por nome */ }

            try
            {
                // só quando o caminho é inacessível o nome vale — um homônimo de outra pasta
                // pode dar falso positivo, o que é preferível a escrever no ini com o jogo aberto
                if (!pathKnown && exeNames.Contains(p.ProcessName)) return true;
            }
            catch { }
            finally { p.Dispose(); }
        }
        return false;
    }
}
