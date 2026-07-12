using System.IO;
using System.Text;

namespace RenoDXLauncher.Services;

/// <summary>
/// Line-preserving INI editor for ReShade.ini. Only touched keys change;
/// comments, ordering and unknown sections are kept byte-for-byte.
/// ReShade parses case-sensitively, so lookups here are case-sensitive too,
/// with helpers for case-insensitive probing.
/// </summary>
public class IniFile
{
    private readonly List<string> _lines;
    public string Path { get; }

    public IniFile(string path)
    {
        Path = path;
        _lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
    }

    private static bool IsSectionLine(string line, out string name)
    {
        name = "";
        var t = line.Trim();
        if (t.Length < 2 || t[0] != '[' || t[^1] != ']') return false;
        name = t[1..^1];
        return true;
    }

    private (int start, int end) FindSection(string section)
    {
        int start = -1;
        for (int i = 0; i < _lines.Count; i++)
        {
            if (!IsSectionLine(_lines[i], out var name)) continue;
            if (start >= 0) return (start, i);
            if (string.Equals(name, section, StringComparison.OrdinalIgnoreCase)) start = i;
        }
        return start >= 0 ? (start, _lines.Count) : (-1, -1);
    }

    public IReadOnlyList<string> SectionNames()
    {
        var result = new List<string>();
        foreach (var line in _lines)
            if (IsSectionLine(line, out var name)) result.Add(name);
        return result;
    }

    /// <summary>All key=value pairs of a section, in file order, with exact key casing.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> GetSection(string section)
    {
        var result = new List<KeyValuePair<string, string>>();
        var (start, end) = FindSection(section);
        if (start < 0) return result;
        for (int i = start + 1; i < end; i++)
        {
            var line = _lines[i];
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            if (key.StartsWith(';') || key.StartsWith('#')) continue;
            result.Add(new(key, line[(eq + 1)..].Trim()));
        }
        return result;
    }

    public string? Get(string section, string key, bool ignoreCase = false)
    {
        var cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var kv in GetSection(section))
            if (string.Equals(kv.Key, key, cmp)) return kv.Value;
        return null;
    }

    /// <summary>Set key in section, creating the section at the end of the file if missing.
    /// If the key exists with different casing, the existing casing wins (ReShade reads case-sensitively,
    /// so we must not introduce a duplicate key that the mod would ignore).</summary>
    public void Set(string section, string key, string value)
    {
        var (start, end) = FindSection(section);
        if (start < 0)
        {
            if (_lines.Count > 0 && _lines[^1].Trim().Length > 0) _lines.Add("");
            _lines.Add($"[{section}]");
            _lines.Add($"{key}={value}");
            return;
        }
        int insertAt = end;
        for (int i = start + 1; i < end; i++)
        {
            var line = _lines[i];
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var existing = line[..eq].Trim();
            if (string.Equals(existing, key, StringComparison.OrdinalIgnoreCase))
            {
                _lines[i] = $"{existing}={value}";
                return;
            }
        }
        // insert before trailing blank lines of the section
        while (insertAt > start + 1 && _lines[insertAt - 1].Trim().Length == 0) insertAt--;
        _lines.Insert(insertAt, $"{key}={value}");
    }

    public void RemoveKey(string section, string key)
    {
        var (start, end) = FindSection(section);
        if (start < 0) return;
        for (int i = end - 1; i > start; i--)
        {
            var line = _lines[i];
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            if (string.Equals(line[..eq].Trim(), key, StringComparison.OrdinalIgnoreCase))
                _lines.RemoveAt(i);
        }
    }

    public void Save()
    {
        // ReShade writes plain UTF-8 without BOM.
        File.WriteAllText(Path, string.Join(Environment.NewLine, _lines) + Environment.NewLine,
            new UTF8Encoding(false));
    }
}
