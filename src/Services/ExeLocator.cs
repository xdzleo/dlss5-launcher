using System.IO;
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
    private static readonly string[] StubNames =
    {
        "launcher", "setup", "unins", "crash", "redist", "dxsetup", "vc_redist", "vcredist",
        "install", "config", "report", "eac", "easyanticheat", "activation", "support",
        "benchmark", "updater", "bootstrap", "helper", "cleanup", "touchup",
    };

    public static List<string> FindCandidates(GameInfo game, string? installSubdirOverride)
    {
        var results = new List<string>();
        try
        {
            // 1. curated subdir override
            if (installSubdirOverride != null)
            {
                var dir = Path.Combine(game.InstallDir, installSubdirOverride);
                if (Directory.Exists(dir))
                    results.AddRange(SafeGetExes(dir).Where(e => !IsStub(e)));
            }

            // 2. store metadata hint
            if (game.ExeHint != null)
            {
                var hint = Path.Combine(game.InstallDir, game.ExeHint);
                if (File.Exists(hint)) results.Add(hint);
                else
                {
                    var found = SafeEnumerate(game.InstallDir)
                        .FirstOrDefault(f => Path.GetFileName(f).Equals(
                            Path.GetFileName(game.ExeHint), StringComparison.OrdinalIgnoreCase));
                    if (found != null) results.Add(found);
                }
            }

            var all = SafeEnumerate(game.InstallDir).ToList();

            // 3. Unreal shipping exe is always the real render exe
            results.AddRange(all.Where(f =>
                Path.GetFileName(f).EndsWith("-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(f).EndsWith("-WinGDK-Shipping.exe", StringComparison.OrdinalIgnoreCase)));

            // 4. everything else, largest first
            results.AddRange(all
                .Where(f => !IsStub(f))
                .OrderByDescending(f => { try { return new FileInfo(f).Length; } catch { return 0L; } }));
        }
        catch (Exception ex) { Log.Warn($"ExeLocator {game.Name}: {ex.Message}"); }

        return results
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();
    }

    private static bool IsStub(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        return StubNames.Any(name.Contains);
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
