using System.IO;
using System.Text.RegularExpressions;
using RenoDXLauncher.Models;

namespace RenoDXLauncher.Services;

/// <summary>
/// Works out WHICH GAME a hand-picked folder holds.
///
/// A folder added by hand rarely carries the game's title. The folder the user actually points at
/// is usually the one with the exe in it — "…\007.First.Light-InsaneRamZes\Retail" — so the name
/// is "Retail", which matches nothing. The parent is not much better: a download folder carries
/// release decorations ("-InsaneRamZes", "[Repack]", "v1.2.3") that no catalog title has.
///
/// So instead of one name, this offers several: the folder, its parents while the folder name is a
/// generic container, each with the decorations stripped, and — the strongest signal — the render
/// exe's own file name. "007FirstLight.exe" normalizes to exactly the catalog's "007 First Light".
/// </summary>
public static partial class FolderGameResolver
{
    /// <summary>Folder names that describe a LAYOUT, not a game.</summary>
    private static readonly HashSet<string> Containers = new(StringComparer.OrdinalIgnoreCase)
    {
        "retail", "bin", "bin64", "binaries", "win64", "win32", "x64", "x86", "game", "games",
        "build", "release", "client", "data", "app", "content", "shipping", "pc", "windows",
        "wingdk", "steamapps", "common", "launcher", "files",
    };

    [GeneratedRegex(@"[\[\(\{][^\]\)\}]*[\]\)\}]")]
    private static partial Regex BracketTagRegex();

    [GeneratedRegex(@"[-_. ]v?\d+(\.\d+){1,3}([-_. ].*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionTailRegex();

    [GeneratedRegex(@"-[A-Za-z0-9]+$")]
    private static partial Regex GroupSuffixRegex();

    [GeneratedRegex(@"\b(repack|multi\d*|proper|readnfo|incl|dlc|update|build\s*\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReleaseWordRegex();

    /// <summary>Drop release-folder decorations. The result is only ever used as an EXTRA
    /// candidate, never as a replacement — "Half-Life" would lose its second half here, and the
    /// unstripped name is always tried first.</summary>
    public static string StripReleaseTags(string name)
    {
        var s = BracketTagRegex().Replace(name, " ");
        s = VersionTailRegex().Replace(s, " ");
        s = ReleaseWordRegex().Replace(s, " ");
        s = GroupSuffixRegex().Replace(s.Trim(), " ");
        return Regex.Replace(s, @"\s+", " ").Trim(' ', '.', '-', '_');
    }

    /// <summary>Names worth trying for this folder, best signal first.</summary>
    public static List<string> CandidateNames(string dir, string? exePath = null)
    {
        var names = new List<string>();
        void Offer(string? n)
        {
            if (string.IsNullOrWhiteSpace(n)) return;
            if (!names.Contains(n, StringComparer.OrdinalIgnoreCase)) names.Add(n);
            var stripped = StripReleaseTags(n);
            if (stripped.Length >= 3 && !names.Contains(stripped, StringComparer.OrdinalIgnoreCase))
                names.Add(stripped);
        }

        var current = dir.TrimEnd('\\', '/');
        var folder = Path.GetFileName(current);
        Offer(folder);

        // climb out of layout folders: …\<Game>\Retail and …\<Game>\Binaries\Win64 both end up
        // pointing at the folder that actually carries the title
        int hops = 0;
        while (hops++ < 3 && folder.Length > 0 && Containers.Contains(folder))
        {
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent)) break;
            current = parent.TrimEnd('\\', '/');
            folder = Path.GetFileName(current);
            if (folder.Length == 0) break;
            Offer(folder);
        }

        // the exe is the most reliable of all: it is named by the developer, not by whoever
        // packed the folder
        if (exePath != null)
        {
            Offer(Path.GetFileNameWithoutExtension(exePath));
            try
            {
                var product = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath).ProductName;
                Offer(product);
            }
            catch { }
        }
        return names;
    }

    /// <summary>Build a GameInfo for a hand-picked folder, naming it after whichever candidate the
    /// catalog recognizes. Falls back to the folder's own name so the entry still shows up.</summary>
    public static GameInfo Resolve(string dir, IReadOnlyList<CatalogEntry> catalog)
    {
        var folderName = Path.GetFileName(dir.TrimEnd('\\', '/'));
        string? exe = null;
        try
        {
            var probe = new GameInfo { Name = folderName, InstallDir = dir, Store = GameStore.Manual };
            exe = ExeLocator.FindCandidates(probe, null).FirstOrDefault();
        }
        catch (Exception ex) { Log.Warn($"resolver exe de {dir}: {ex.Message}"); }

        foreach (var candidate in CandidateNames(dir, exe))
        {
            var probe = new GameInfo { Name = candidate, InstallDir = dir, Store = GameStore.Manual };
            if (MatchService.FindMatch(probe, catalog) is { } hit)
            {
                Log.Info($"pasta manual {dir}: reconhecida como \"{hit.GameName}\" por \"{candidate}\"");
                // show the catalog's spelling, not "Retail" or "007.First.Light-InsaneRamZes"
                return new GameInfo { Name = hit.GameName, InstallDir = dir, Store = GameStore.Manual };
            }
        }
        return new GameInfo { Name = folderName, InstallDir = dir, Store = GameStore.Manual };
    }
}
