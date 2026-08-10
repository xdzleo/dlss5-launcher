using System.IO;
using System.Text.Json;

namespace RenoDXLauncher.Services;

/// <summary>What we know about one addon file we installed, for precise update checks.</summary>
public class InstalledModRecord
{
    public string? Slug { get; set; }
    public string? FileName { get; set; }
    public string? Url { get; set; }
    /// <summary>HTTP ETag of the exact build we installed — the canonical "is it still the
    /// same file?" token. All three mod hosts (GitHub Pages, GitHub Releases, fork pages)
    /// return it on HEAD.</summary>
    public string? ETag { get; set; }
    public long Size { get; set; }
    public DateTime DownloadedUtc { get; set; }
}

/// <summary>
/// Tracks which build of each addon is installed, keyed by the addon's full path. Lets the
/// launcher answer "is there a newer version of this mod?" with an ETag comparison instead of
/// guessing from file size and timestamps.
/// </summary>
public static class InstalledModRegistry
{
    private static readonly object Gate = new();
    private static Dictionary<string, InstalledModRecord>? _cache;

    private static string Path_ => System.IO.Path.Combine(AppPaths.DataDir, "installed.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static Dictionary<string, InstalledModRecord> Load()
    {
        if (_cache != null) return _cache;
        try
        {
            if (File.Exists(Path_))
                _cache = JsonSerializer.Deserialize<Dictionary<string, InstalledModRecord>>(
                    File.ReadAllText(Path_)) ?? new();
            else _cache = new();
        }
        catch (Exception ex)
        {
            Log.Warn($"installed.json load: {ex.Message}");
            _cache = new();
        }
        return _cache;
    }

    private static string Key(string addonPath) => System.IO.Path.GetFullPath(addonPath).ToLowerInvariant();

    public static InstalledModRecord? Get(string addonPath)
    {
        lock (Gate) return Load().GetValueOrDefault(Key(addonPath));
    }

    public static void Set(string addonPath, InstalledModRecord record)
    {
        lock (Gate)
        {
            var map = Load();
            map[Key(addonPath)] = record;
            SaveLocked(map);
        }
    }

    public static void Remove(string addonPath)
    {
        lock (Gate)
        {
            var map = Load();
            if (map.Remove(Key(addonPath))) SaveLocked(map);
        }
    }

    private static void SaveLocked(Dictionary<string, InstalledModRecord> map)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            var temp = Path_ + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(map, JsonOpts));
            if (File.Exists(Path_)) File.Replace(temp, Path_, null);
            else File.Move(temp, Path_);
        }
        catch (Exception ex) { Log.Warn($"installed.json save: {ex.Message}"); }
    }
}
