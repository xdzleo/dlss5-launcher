using System.IO;

namespace RenoDXLauncher.Services;

/// <summary>
/// Detects anti-cheat by the FILES on disk, independent of whether the wiki note happens to
/// mention it. Injecting an unsigned proxy DLL into an EAC/BattlEye title can get the user's
/// account permanently banned — the only irreversible damage this app can cause — so this must
/// not depend on free-text parsing.
/// </summary>
public static class AntiCheatScanner
{
    private static readonly (string Needle, string Name)[] Signatures =
    {
        ("easyanticheat", "Easy Anti-Cheat"),
        ("eac_launcher", "Easy Anti-Cheat"),
        ("battleye", "BattlEye"),
        ("beservice", "BattlEye"),
        ("beclient", "BattlEye"),
        ("vgk.sys", "Vanguard"),
        ("vanguard", "Vanguard"),
    };

    /// <summary>Name of the anti-cheat found, or null. Scans the install root (the
    /// EasyAntiCheat\ folder lives there, not next to the shipping exe) and the deploy dir.</summary>
    public static string? Detect(string? installDir, string? targetDir)
    {
        foreach (var root in new[] { installDir, targetDir })
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
            try
            {
                var options = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = true,
                    MaxRecursionDepth = 3,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                };
                foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", options))
                {
                    var name = Path.GetFileName(entry).ToLowerInvariant();
                    foreach (var (needle, display) in Signatures)
                        if (name.Contains(needle, StringComparison.Ordinal))
                            return display;
                }
            }
            catch (Exception ex) { Log.Warn($"anti-cheat scan {root}: {ex.Message}"); }
        }
        return null;
    }
}
