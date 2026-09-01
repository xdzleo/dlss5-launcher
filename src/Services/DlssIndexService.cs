using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using RenoDXLauncher.Localization;

namespace RenoDXLauncher.Services;

/// <summary>
/// The curated index of NGX runtimes and Streamline builds maintained by the RHI project
/// (github.com/RankFTW/RHI — <c>dlss_manifest.json</c>, mirrored as release assets on
/// <c>RankFTW/rhi-repo</c>).
///
/// Why the launcher uses it: until now the library could only be filled from copies already on
/// this machine, and the class comment on <see cref="DlssRuntimeService"/> gave the reason —
/// pulling an NVIDIA binary off "some mirror" is not something an installer should do on the
/// user's behalf. That reasoning holds for an arbitrary mirror. It does not hold for a curated,
/// versioned index that this launcher already downloads and trusts for per-game configuration.
///
/// The part that makes it safe is not the source, though: every file that comes out of here is
/// checked with <see cref="DlssRuntimeService.IsGenuine"/> before it can reach a game folder, so
/// a tampered mirror produces a refusal rather than a swapped DLL. The index only decides what to
/// try; NVIDIA's own signature decides what is installed.
///
/// <c>nvngx_dlssnr.dll</c> is the reason this exists. It ships in no driver and no public SDK, so
/// "find it yourself" was the one manual step left in an otherwise automatic install — and a
/// 158 MB file most users have no copy of.
/// </summary>
public class DlssIndexService
{
    private const string Url = "https://raw.githubusercontent.com/RankFTW/RHI/main/dlss_manifest.json";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(3);

    /// <summary>Index key → the runtime file the archive is expected to contain.</summary>
    public const string KindNeural = "dlssnr";
    public const string KindStreamline = "streamline";

    /// <param name="Version">As the index states it, e.g. "310.8.0".</param>
    public record Entry(string Kind, string Version, string Url);

    private readonly Dictionary<string, List<Entry>> _byKind = new(StringComparer.OrdinalIgnoreCase);

    public async Task LoadAsync()
    {
        string? json = null;
        var cachePath = Path.Combine(AppPaths.DataDir, "dlss_manifest.json");
        try
        {
            if (File.Exists(cachePath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath) < CacheTtl)
            {
                json = await File.ReadAllTextAsync(cachePath);
            }
            else
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("RenoDXLauncher/1.0");
                json = await http.GetStringAsync(Url);
                Directory.CreateDirectory(AppPaths.DataDir);
                await File.WriteAllTextAsync(cachePath, json);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"dlss index fetch: {ex.Message}");
            if (File.Exists(cachePath)) json = await File.ReadAllTextAsync(cachePath);
        }
        if (json is null) return;

        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Array) continue;
                var list = new List<Entry>();
                foreach (var e in prop.Value.EnumerateArray())
                {
                    if (!e.TryGetProperty("version", out var v) || v.GetString() is not { Length: > 0 } ver) continue;
                    if (!e.TryGetProperty("url", out var u) || u.GetString() is not { Length: > 0 } url) continue;
                    // Only the project's own mirror. An index entry is data, not an instruction:
                    // a rewritten manifest must not be able to point the launcher at any host it
                    // likes, and there is no legitimate reason for one of these to live elsewhere.
                    if (!url.StartsWith("https://github.com/RankFTW/", StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Warn($"dlss index: ignoring off-mirror url for {prop.Name} {ver}");
                        continue;
                    }
                    list.Add(new Entry(prop.Name, ver, url));
                }
                if (list.Count > 0) _byKind[prop.Name] = list;
            }
        }
        catch (Exception ex) { Log.Warn($"dlss index parse: {ex.Message}"); }
    }

    /// <summary>The newest build the index lists of one kind. The index is ordered newest-first,
    /// but it is sorted here anyway rather than trusting the file's order.</summary>
    public Entry? Newest(string kind)
    {
        if (!_byKind.TryGetValue(kind, out var list)) return null;
        return list
            .OrderByDescending(e => Version.TryParse(e.Version, out var v) ? v : new Version(0, 0))
            .FirstOrDefault();
    }

    /// <summary>
    /// O build de Neural Rendering que ESTA GPU consegue rodar.
    ///
    /// O modelo original (310.8.0) traz kernels sm_120 e roda so em Blackwell — e por isso que o
    /// launcher recusava RTX 20/30/40 e mandava o usuario achar um arquivo sozinho. O manifesto
    /// do RHI publica tambem os builds `.SF` (do ShortFuse), que acrescentam binarios patcheados
    /// para RTX 40 e um caminho FP16 para RTX 20/30.
    ///
    /// A ordenacao normal nunca os escolhia: "310.8.SF-v2" nao e uma versao parseavel, entao
    /// Version.TryParse falhava e a entrada caia para 0.0, atras do 310.8.0. O resultado era o
    /// launcher baixar justamente o build que a placa do usuario nao roda.
    ///
    /// Em Blackwell qualquer um serve e o mais novo ganha; fora dela, so o `.SF` serve.
    /// </summary>
    public Entry? NeuralFor(bool blackwell)
    {
        if (!_byKind.TryGetValue(KindNeural, out var list) || list.Count == 0) return null;

        static int Peso(Entry e) =>
            e.Version.Contains("SF-v2", StringComparison.OrdinalIgnoreCase) ? 3
            : e.Version.Contains(".SF", StringComparison.OrdinalIgnoreCase) ? 2
            : 1;

        var candidatos = blackwell ? list : list.Where(e => Peso(e) >= 2).ToList();
        if (candidatos.Count == 0)
        {
            // Nenhum build multi-geracao no indice: devolve o que houver e deixa a checagem de
            // GPU decidir, em vez de dizer "nao ha runtime" para quem so precisa de outro build.
            Log.Warn("dlss index: nenhum build .SF listado; caindo no mais novo");
            return Newest(KindNeural);
        }
        // Desempate pela ordem do proprio manifesto, que ja vem do mais novo para o mais antigo.
        return candidatos.OrderByDescending(Peso).FirstOrDefault();
    }

    /// <summary>
    /// Download one entry's archive and unpack it into its own folder under the launcher's
    /// downloads directory. Reuses an already-unpacked copy so a retry after a failed install
    /// does not pull 158 MB again.
    /// </summary>
    /// <returns>The folder the archive was unpacked into.</returns>
    public static async Task<string> FetchAsync(Entry entry, IProgress<string>? progress = null,
                                                CancellationToken ct = default)
    {
        Directory.CreateDirectory(AppPaths.DownloadsDir);
        var stem = $"{entry.Kind}-{entry.Version}";
        var unpacked = Path.Combine(AppPaths.DownloadsDir, stem);
        // Recursivo, porque o consumidor procura recursivamente: um arquivo que descompacta numa
        // subpasta deixava esta checagem falsa para sempre, e cada tentativa repetia o download
        // de 158 MB que ela existe justamente para evitar.
        if (Directory.Exists(unpacked)
            && Directory.EnumerateFiles(unpacked, "*", SearchOption.AllDirectories).Any())
            return unpacked;

        var archive = Path.Combine(AppPaths.DownloadsDir, stem + ".zip");
        if (!File.Exists(archive))
        {
            progress?.Report(L.T("Dlss_Index_Downloading", entry.Kind, entry.Version));
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("RenoDXLauncher/1.0");
            // Straight to disk: the neural runtime is 158 MB and buffering it in memory first
            // costs that much of the process's working set for no reason.
            var temp = archive + ".part";
            await using (var net = await http.GetStreamAsync(entry.Url, ct))
            await using (var file = File.Create(temp))
                await net.CopyToAsync(file, ct);
            File.Move(temp, archive, overwrite: true);
        }

        progress?.Report(L.T("Dlss_Index_Extracting", entry.Kind));
        Directory.CreateDirectory(unpacked);
        ExtractSafely(archive, unpacked);
        // The archive is not kept: it is the same bytes as the unpacked copy, and these are the
        // largest files the launcher ever touches.
        try { File.Delete(archive); } catch (Exception ex) { Log.Warn($"dlss index cleanup: {ex.Message}"); }
        return unpacked;
    }

    /// <summary>
    /// Unpack, refusing any entry whose path escapes the destination. A zip is an untrusted
    /// archive from the network, and an entry named <c>..\..\something.dll</c> is the standard
    /// way one writes outside the folder it was told to use.
    /// </summary>
    private static void ExtractSafely(string archivePath, string destDir)
    {
        var root = Path.GetFullPath(destDir + Path.DirectorySeparatorChar);
        using var zip = ZipFile.OpenRead(archivePath);
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
            var target = Path.GetFullPath(Path.Combine(destDir, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                Log.Warn($"dlss index: refusing archive entry outside destination: {entry.FullName}");
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }
}
