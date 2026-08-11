using System.IO;
using System.Text.RegularExpressions;
using RenoDXLauncher.Models;

namespace RenoDXLauncher.Services;

/// <summary>Minimal PE header reader: bitness and imported DLL names.</summary>
public static class PeUtils
{
    public record PeInfo(bool Is64Bit, IReadOnlyList<string> Imports, string? ProductName);

    public static PeInfo? Inspect(string path, bool readImports = true)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var br = new BinaryReader(fs);
            if (fs.Length < 0x100 || br.ReadUInt16() != 0x5A4D) return null; // MZ
            fs.Position = 0x3C;
            uint peOffset = br.ReadUInt32();
            if (peOffset + 24 > fs.Length) return null;
            fs.Position = peOffset;
            if (br.ReadUInt32() != 0x00004550) return null; // PE\0\0
            ushort machine = br.ReadUInt16();
            ushort numSections = br.ReadUInt16();
            fs.Position += 12;
            ushort optSize = br.ReadUInt16();
            fs.Position += 2;
            bool is64 = machine == 0x8664 || machine == 0xAA64;

            var imports = new List<string>();
            if (readImports && optSize > 0)
            {
                long optStart = fs.Position;
                ushort magic = br.ReadUInt16();
                bool pe32Plus = magic == 0x20B;
                // data directories start at offset 96 (PE32) / 112 (PE32+) into the optional header
                long ddStart = optStart + (pe32Plus ? 112 : 96);
                fs.Position = ddStart + 8; // second directory = import table
                uint importRva = br.ReadUInt32();
                uint importSize = br.ReadUInt32();
                if (importRva != 0 && importSize != 0)
                {
                    // section table follows the optional header
                    long sectionTable = optStart + optSize;
                    var sections = new List<(uint va, uint vsize, uint raw)>();
                    for (int i = 0; i < numSections; i++)
                    {
                        fs.Position = sectionTable + i * 40 + 8;
                        uint vsize = br.ReadUInt32();
                        uint va = br.ReadUInt32();
                        fs.Position += 4;
                        uint raw = br.ReadUInt32();
                        sections.Add((va, vsize, raw));
                    }
                    long RvaToFile(uint rva)
                    {
                        foreach (var (va, vsize, raw) in sections)
                            if (rva >= va && rva < va + Math.Max(vsize, 1)) return raw + (rva - va);
                        return -1;
                    }
                    long impFile = RvaToFile(importRva);
                    if (impFile > 0)
                    {
                        for (int i = 0; i < 128; i++) // descriptor table, 20 bytes each, null-terminated
                        {
                            fs.Position = impFile + i * 20 + 12;
                            uint nameRva = br.ReadUInt32();
                            if (nameRva == 0) break;
                            long nameFile = RvaToFile(nameRva);
                            if (nameFile <= 0) continue;
                            fs.Position = nameFile;
                            var sb = new System.Text.StringBuilder();
                            for (int c = 0; c < 64; c++)
                            {
                                int b = fs.ReadByte();
                                if (b <= 0) break;
                                sb.Append((char)b);
                            }
                            imports.Add(sb.ToString().ToLowerInvariant());
                        }
                    }
                }
            }
            string? product = null;
            try { product = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).ProductName; }
            catch { }
            return new PeInfo(is64, imports, product);
        }
        catch (Exception ex)
        {
            Log.Warn($"PE inspect {path}: {ex.Message}");
            return null;
        }
    }
}

/// <summary>
/// Finds the game's rendering exe (deploy target for ReShade + addon).
/// Priority: RHI installPathOverrides subdir → store exe hint → *-Win64-Shipping.exe →
/// heuristic scan (largest exe, launcher-stub names excluded).
/// </summary>
public static class ExeLocator
{
    /// <summary>Words that only ever appear in a launcher/helper binary. "crash" and "report"
    /// are deliberately NOT here — Crash Bandicoot is a game and Reporter is a game; those two
    /// are handled by the compound list below, which cannot fire on a real title.</summary>
    private static readonly string[] StubNames =
    {
        "launcher", "setup", "unins", "uninstall", "redist", "dxsetup", "vc_redist", "vcredist",
        "install", "installer", "config", "eac", "easyanticheat", "activation", "support",
        "benchmark", "updater", "bootstrap", "helper", "cleanup", "touchup",
    };

    /// <summary>Compound stubs, matched against the name with separators stripped so
    /// crash_reporter / CrashReporter / crashreporting all resolve to the same thing.</summary>
    private static readonly string[] GluedStubs =
    {
        "gamelaunchhelper", "easyanticheat", "vcredist", "dxsetup", "unins000",
        "crashreport", "crashhandler", "crashpad", "crashsender", "errorreport",
        "certsupdater", "prereqsetup",
    };

    public static List<string> FindCandidates(GameInfo game, string? installSubdirOverride)
    {
        var results = new List<string>();
        var peCache = new Dictionary<string, PeUtils.PeInfo?>(StringComparer.OrdinalIgnoreCase);
        PeUtils.PeInfo? Pe(string path)
        {
            if (!peCache.TryGetValue(path, out var info))
                peCache[path] = info = PeUtils.Inspect(path);
            return info;
        }

        try
        {
            var all = SafeEnumerate(game.InstallDir).ToList();

            // The store's launch target. It is a HINT, not an answer: Steam launches Stellar
            // Blade through crs-handler.exe (1 MB) and Max Payne 3 through PlayMaxPayne3.exe
            // (0.4 MB) — both are shims that relaunch the real binary. So it earns a bonus in
            // the ranking, it never jumps the queue.
            string? hint = null;
            if (game.ExeHint != null)
            {
                var direct = Path.Combine(game.InstallDir, game.ExeHint);
                hint = File.Exists(direct) ? direct
                    : all.FirstOrDefault(f => Path.GetFileName(f).Equals(
                        Path.GetFileName(game.ExeHint), StringComparison.OrdinalIgnoreCase));
                if (hint != null && !all.Contains(hint, StringComparer.OrdinalIgnoreCase))
                    all.Add(hint);   // deeper than the scan depth, but real
            }

            // The curated deploy subdir from the RenoDX HDR Index — hand-checked data.
            string? curated = null;
            if (installSubdirOverride != null)
            {
                var dir = Path.Combine(game.InstallDir, installSubdirOverride);
                if (Directory.Exists(dir)) curated = Path.GetFullPath(dir);
            }

            // One ranking for everything. Every earlier revision of this had priority TIERS,
            // and every wrong pick so far came from a tier jumping the queue with a shim.
            string? stubHint = null;
            var ranked = new List<(string Path, int Score, long Size)>();
            foreach (var f in all)
            {
                if (InVendorDir(f, game.InstallDir)) continue;
                if (IsStub(f, game.Name))
                {
                    // keep a stubby hint selectable in the combo, but dead last
                    if (hint != null && f.Equals(hint, StringComparison.OrdinalIgnoreCase))
                        stubHint = f;
                    continue;
                }

                int score = 0;
                var fileName = Path.GetFileName(f);
                if (fileName.EndsWith("-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase)
                    || fileName.EndsWith("-WinGDK-Shipping.exe", StringComparison.OrdinalIgnoreCase))
                    score += 1000;                                    // Unreal never lies
                if (curated != null && string.Equals(Path.GetDirectoryName(Path.GetFullPath(f)),
                        curated, StringComparison.OrdinalIgnoreCase))
                    score += 300;                                     // curated deploy dir
                score += NameScore(f, game.Name) * 3;                 // 0..300
                var pe = Pe(f);
                if (UsesGpu(pe)) score += 400;                        // it talks to the GPU
                if (pe?.Is64Bit == true) score += 120;                // prefer, never require
                if (hint != null && f.Equals(hint, StringComparison.OrdinalIgnoreCase))
                    score += 80;                                      // the store's guess

                long size = 0;
                try { size = new FileInfo(f).Length; } catch { }
                ranked.Add((f, score, size));
            }

            results.AddRange(ranked
                .OrderByDescending(r => r.Score)
                .ThenByDescending(r => r.Size)
                .Select(r => r.Path));
            if (stubHint != null) results.Add(stubHint);
        }
        catch (Exception ex) { Log.Warn($"ExeLocator {game.Name}: {ex.Message}"); }

        return results
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();
    }

    /// <summary>Import prefixes only a rendering binary links against. Prefixes, not exact
    /// names: Max Payne 3 imports d3dcompiler_43.dll and nothing else graphical, and that one
    /// import is what tells the 22 MB game apart from its 64-bit 0.4 MB launcher shim.</summary>
    private static readonly string[] GpuDlls =
    {
        "d3d", "dxgi", "opengl32", "vulkan-1", "amd_ags", "nvapi",
    };

    private static bool UsesGpu(PeUtils.PeInfo? pe) =>
        pe != null && pe.Imports.Any(i => GpuDlls.Any(g => i.StartsWith(g, StringComparison.Ordinal)));

    /// <summary>Folders that ship third-party installers and services — never the render exe.
    /// Their binaries can be far bigger than the game's own (Epic's installer is 126 MB).</summary>
    private static readonly string[] VendorDirs =
    {
        "epiconlineservices", "easyanticheat", "battleye", "_commonredist", "commonredist",
        "redist", "directx", "vcredist", "dotnet", "prereq", "_installer", "installers",
        "steamvr", "support", "tools", "sdk", "crashreporter",
    };

    private static bool InVendorDir(string path, string installDir)
    {
        var rel = path.Length > installDir.Length ? path[installDir.Length..] : path;
        foreach (var part in rel.Split('\\', '/'))
        {
            var p = part.ToLowerInvariant();
            if (VendorDirs.Any(v => p == v || p.Replace(" ", "") == v)) return true;
        }
        return false;
    }

    /// <summary>How much an exe name looks like the game's own title — the strongest signal
    /// that this is the binary the user actually plays.</summary>
    private static int NameScore(string path, string gameName)
    {
        var exe = MatchService.Normalize(Path.GetFileNameWithoutExtension(path));
        var game = MatchService.Normalize(gameName);
        if (exe.Length == 0 || game.Length == 0) return 0;
        if (exe == game) return 100;
        if (exe.StartsWith(game) || game.StartsWith(exe)) return 80;   // "...SpaceMarine2Retail"
        if (exe.Contains(game) || game.Contains(exe)) return 60;
        // partial: share a long prefix (abbreviated names like "eldenring" vs "eldenringgame")
        int common = 0;
        while (common < exe.Length && common < game.Length && exe[common] == game[common]) common++;
        return common >= 6 ? 30 : 0;
    }

    /// <summary>Stub detection on WORD boundaries: "eac" must not condemn "Peaceful.exe".
    /// An exe whose name echoes the game's own title is never a stub, so "CrashBandicoot.exe"
    /// survives the "crash" rule.</summary>
    private static bool IsStub(string path, string gameName)
    {
        var raw = Path.GetFileNameWithoutExtension(path);
        var name = raw.ToLowerInvariant();

        // the game's own name beats every heuristic below
        if (NameScore(path, gameName) >= 80) return false;

        // Split camelCase/underscore/dash/space/digits into words. The split runs on the
        // ORIGINAL spelling: lowercasing first erases the case boundary, and IgnoreCase makes
        // [A-Z] match lowercase too — which used to turn every name into single letters and
        // silently disabled this whole list.
        var words = Regex.Split(raw, @"(?<!^)(?=[A-Z])|[^A-Za-z0-9]+|(?<=[a-z])(?=[0-9])")
            .Where(w => w.Length > 0)
            .Select(w => w.ToLowerInvariant())
            .ToArray();
        if (StubNames.Any(s => words.Contains(s, StringComparer.Ordinal))) return true;

        var compact = Regex.Replace(name, "[^a-z0-9]", "");
        return GluedStubs.Any(g => compact.Contains(g, StringComparison.Ordinal));
    }

    private static IEnumerable<string> SafeGetExes(string dir)
    {
        try { return Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly); }
        catch { return Enumerable.Empty<string>(); }
    }

    private static IEnumerable<string> SafeEnumerate(string root)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            MaxRecursionDepth = 5,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        try { return Directory.EnumerateFiles(root, "*.exe", options); }
        catch { return Enumerable.Empty<string>(); }
    }
}
