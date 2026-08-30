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

    /// <summary>
    /// Addons that are meant to live BESIDE a game's mod instead of replacing it.
    ///
    /// A game mod owns the game's shaders, so two of them in one folder is a real conflict. These
    /// are not that: they hook something else entirely — the NGX exports, the Streamline
    /// interposer, Unreal's HDR path — and the documented setup for every one of them is to sit
    /// next to the game's own mod. Treating them as rivals deleted working installs.
    ///
    /// Matched by prefix because the builds are renamed constantly as versions circulate
    /// (renodx-dlss5-v2.5.addon64 and so on).
    /// </summary>
    private static readonly string[] CompanionAddonPrefixes =
        ["renodx-neural", "renodx-dlss5", "renodx-dlssfix", "renodx-ue-extended", "renodx-fpslimiter"];

    private static bool IsCompanionAddon(string fileName) =>
        CompanionAddonPrefixes.Any(p => fileName.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>Nome do arquivo sem o sufixo que marca "desativado".</summary>
    private static string BareName(string path)
    {
        var nome = Path.GetFileName(path);
        return nome.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase)
            ? nome[..^DisabledSuffix.Length] : nome;
    }

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
            // O mod DO JOGO, e nao qualquer renodx-*. Os companions moram na mesma pasta de
            // proposito, e vinham antes na ordem alfabetica (renodx-neural, renodx-dlss5 e
            // renodx-dlssfix vem antes de renodx-stalker2), entao o primeiro da lista costumava
            // ser o companion. Dai o launcher comparava o addon neural de 573 KB com o mod do
            // jogo de 2 MB, concluia "tem versao nova", instalava o mod do jogo ao lado — e na
            // checagem seguinte reelegia o companion e dizia de novo. Update que nunca acaba.
            var doJogo = addons.Where(f => !IsCompanionAddon(BareName(f))).ToList();
            var enabled = doJogo.FirstOrDefault(f => !f.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase));
            state.AddonPath = enabled ?? doJogo.FirstOrDefault();
            state.AddonEnabled = enabled != null;
        }
        catch (Exception ex) { Log.Warn($"addon state {targetDir}: {ex.Message}"); }
        return state;
    }

    /// <summary>
    /// Is this ETag just the file's mtime and size in disguise?
    ///
    /// nginx's default is <c>"{mtime:x}-{size:x}"</c>, and the size half is the giveaway: when it
    /// matches the byte count we are comparing, the other half is a timestamp, and a timestamp
    /// changes every time the site is re-deployed. An ETag like that answers "were these bytes
    /// written at the same instant", not "are these the same bytes" — so it is not evidence.
    ///
    /// Servers that hash the content (GitHub among them) do not match this shape, and their ETag
    /// stays the strongest signal we have: it still catches a new build of identical size.
    /// </summary>
    private static bool IsMtimeEtag(string etag, long? size)
    {
        if (size is not > 0) return false;
        var v = etag.Trim();
        if (v.StartsWith("W/", StringComparison.Ordinal)) v = v[2..];
        v = v.Trim('"');
        var hifen = v.LastIndexOf('-');
        if (hifen <= 0 || hifen == v.Length - 1) return false;
        return long.TryParse(v[(hifen + 1)..], System.Globalization.NumberStyles.HexNumber,
                             System.Globalization.CultureInfo.InvariantCulture, out var doEtag)
               && doEtag == size;
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

            var local = new FileInfo(state.AddonPath);

            // The ETag identifies the exact build we installed — but only when it says something
            // about the bytes. nginx, which serves these addons, builds it out of the file's
            // mtime and size ("6a91a630-1e2600"), so re-publishing the site changes every ETag
            // without changing a single byte. That is not hypothetical: it is what made every
            // installed mod claim an update, survive the update, and claim it again.
            var record = InstalledModRegistry.Get(state.AddonPath);
            if (record?.ETag is { Length: > 0 } localEtag && remoteEtag is { Length: > 0 }
                && !IsMtimeEtag(localEtag, local.Length) && !IsMtimeEtag(remoteEtag, remoteLen))
                return !string.Equals(localEtag, remoteEtag, StringComparison.Ordinal);

            // fallback for addons installed before this tracking existed (or by hand)
            if (remoteLen is > 0)
            {
                // Size is the only evidence here that speaks about CONTENT. When it matches, the
                // answer is "no update" and nothing below may overturn it.
                //
                // The modification date used to get a say after this point, and it is not evidence
                // of anything: File.Copy carries the source's timestamp, a hand-installed addon
                // carries whatever the browser wrote, and a re-hosted asset gets a fresh
                // Last-Modified without a byte changing. Any of those made the same build report
                // "update available" forever — a badge that is always on tells the user nothing.
                return remoteLen != local.Length;
            }

            // No size from the server: the date is all that is left, and it is weak enough that a
            // full day of slack is the honest threshold rather than a minute.
            if (remoteMod is { } rm) return rm > local.LastWriteTimeUtc.AddDays(1);
            return null;
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

            // The same bytes we already have? Then this install is a local copy.
            //
            // Turning the mod off removes it, so turning it back on comes through here — and a
            // switch that costs a multi-megabyte download every time it is flipped is a switch
            // people stop flipping. A HEAD is cheap, and the size is what says whether the build
            // changed; anything else falls through to the full download.
            var reusable = false;
            if (File.Exists(cached))
            {
                try
                {
                    using var head = new HttpRequestMessage(HttpMethod.Head, entry.DownloadUrl);
                    using var probe = await http.SendAsync(head);
                    var remote = probe.IsSuccessStatusCode ? probe.Content.Headers.ContentLength : null;
                    if (remote is > 0 && remote == new FileInfo(cached).Length)
                    {
                        progress?.Report(L.T("Install_Mod_Cached", fileName));
                        etag = probe.Headers.ETag?.Tag;
                        reusable = true;
                    }
                }
                catch (Exception ex) { Log.Warn($"cache probe {fileName}: {ex.Message}"); }
            }

            if (!reusable)
            {
                using var resp = await http.GetAsync(entry.DownloadUrl);
                resp.EnsureSuccessStatusCode();
                etag = resp.Headers.ETag?.Tag;       // build identity, for future update checks
                var bytes = await resp.Content.ReadAsByteArrayAsync();
                // addons are PE DLLs — reject HTML error pages and truncated downloads
                if (bytes.Length < 4096 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
                    throw new InvalidOperationException(L.T("Error_Mod_DownloadCorrupt", fileName));
                await File.WriteAllBytesAsync(cached, bytes);
            }
        }
        size = new FileInfo(cached).Length;

        // One GAME MOD per deploy dir: two mods for the same game fight over the same shaders.
        // That is the conflict this clears — and only that.
        //
        // It used to sweep `renodx-*.addon*`, which is neither of those things. It ate:
        //   - the companion addons, which exist precisely to sit BESIDE a game mod. The community
        //     DLSS 5 installer deploys its own game mod and the neural addon side by side, as its
        //     documented setup. Installing a mod deleted the neural addon in that game, and doing
        //     a batch update deleted it in every game at once;
        //   - `.bak` files, because `.addon*` matches `.addon64.bak` too. A backup that a swap
        //     depends on to be undoable was removed by an unrelated install.
        var keep = new[] { fileName, fileName + DisabledSuffix };
        foreach (var other in Directory.GetFiles(targetDir, "renodx-*"))
        {
            var name = Path.GetFileName(other);
            // exactly an addon, not something that merely starts like one
            var bare = name.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase)
                ? name[..^DisabledSuffix.Length] : name;
            if (!bare.EndsWith(".addon64", StringComparison.OrdinalIgnoreCase)
                && !bare.EndsWith(".addon32", StringComparison.OrdinalIgnoreCase)) continue;
            if (keep.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            if (IsCompanionAddon(bare)) continue;

            Log.Info($"removendo addon conflitante {other}");
            File.Delete(other);
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
